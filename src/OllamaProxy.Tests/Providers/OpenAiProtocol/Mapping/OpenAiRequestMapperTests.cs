// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json;
using System.Text.Json.Nodes;

using OllamaProxy.Contracts.Ollama;
using OllamaProxy.Providers.OpenAiProtocol;
using OllamaProxy.Providers.OpenAiProtocol.Contracts;
using OllamaProxy.Providers.OpenAiProtocol.Mapping;

namespace OllamaProxy.Tests.Providers.OpenAiProtocol.Mapping;

/// <summary>
/// Tests for <see cref="OpenAiRequestMapper"/>, which translates an inbound Ollama chat request into
/// the OpenAI wire format. The story moves from the request envelope (model, streaming/usage,
/// guards), through message projection (plain text, multimodal images, assistant tool calls, tool
/// results), into the option mapping (standard sampling fields, the <c>max_completion_tokens</c>
/// sentinel, the deliberate omission of the non-standard <c>top_k</c>/<c>min_p</c> extensions from the
/// shared output, and the <c>logprobs</c> request flags), tool forwarding, and finally the
/// <c>response_format</c> directive.
/// </summary>
[Trait("Category", "Unit")]
public sealed class OpenAiRequestMapperTests
{
	private static OllamaChatRequest Request(
		IReadOnlyList<OllamaChatMessage>? messages = null,
		IReadOnlyList<OllamaTool>?        tools    = null,
		JsonNode?                         format   = null,
		OllamaOptions?                    options  = null) => new(
		Model: "client-model",
		Messages: messages ?? [new OllamaChatMessage("user", "hi")],
		Tools: tools,
		Format: format,
		Options: options);

	#region MapRequest envelope

	/// <summary>
	/// Verifies that <see cref="OpenAiRequestMapper.MapRequest"/> targets the resolved upstream model
	/// rather than the client-facing model name.
	/// </summary>
	[Fact]
	public void MapRequest_WhenMapped_TargetsResolvedUpstreamModel()
	{
		// Act
		OpenAiChatRequest result = OpenAiRequestMapper.MapRequest(Request(), "openai/gpt-4o", stream: false);

		// Assert
		Assert.Equal("openai/gpt-4o", result.Model);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiRequestMapper.MapRequest"/> requests usage reporting on the final
	/// streamed chunk when streaming, and omits stream options otherwise.
	/// </summary>
	/// <param name="stream">Whether a streamed response is requested.</param>
	/// <param name="expectUsageRequested">Whether usage reporting is expected to be requested.</param>
	[Theory]
	[InlineData(true, true)]
	[InlineData(false, false)]
	public void MapRequest_WhenStreaming_RequestsUsageOnlyForStreams(bool stream, bool expectUsageRequested)
	{
		// Act
		OpenAiChatRequest result = OpenAiRequestMapper.MapRequest(Request(), "m", stream);

		// Assert
		Assert.Equal(stream, result.Stream);
		Assert.Equal(expectUsageRequested, result.StreamOptions?.IncludeUsage ?? false);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiRequestMapper.MapRequest"/> rejects a <see langword="null"/>
	/// request.
	/// </summary>
	[Fact]
	public void MapRequest_WhenRequestIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception =
			Assert.Throws<ArgumentNullException>(() => OpenAiRequestMapper.MapRequest(null!, "m", stream: false));
		Assert.Equal("request", exception.ParamName);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiRequestMapper.MapRequest"/> rejects an empty or whitespace
	/// upstream model name.
	/// </summary>
	/// <param name="upstreamModel">The invalid upstream model name.</param>
	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void MapRequest_WhenUpstreamModelBlank_ThrowsArgumentException(string upstreamModel)
	{
		// Act + Assert
		var exception = Assert.Throws<ArgumentException>(() =>
			OpenAiRequestMapper.MapRequest(Request(), upstreamModel, stream: false));
		Assert.Equal("upstreamModel", exception.ParamName);
	}

	#endregion

	#region MapRequest messages

	/// <summary>
	/// Verifies that a plain user message is projected with its text carried as a bare string content
	/// value.
	/// </summary>
	[Fact]
	public void MapRequest_WhenPlainTextMessage_CarriesStringContent()
	{
		// Arrange
		OllamaChatRequest request = Request([new OllamaChatMessage("user", "hello")]);

		// Act
		OpenAiChatRequest result = OpenAiRequestMapper.MapRequest(request, "m", stream: false);

		// Assert
		OpenAiChatMessage message = Assert.Single(result.Messages);
		Assert.Equal("hello", message.Content?.GetValue<string>());
	}

	/// <summary>
	/// Verifies that a message carrying base64 images is projected as a multimodal content array with a
	/// leading text part followed by an <c>image_url</c> part whose bare payload is wrapped as a data
	/// URL.
	/// </summary>
	[Fact]
	public void MapRequest_WhenMessageHasBase64Image_BuildsMultimodalContentWithDataUrl()
	{
		// Arrange
		OllamaChatRequest request = Request(
		[
			new OllamaChatMessage("user", "describe", Images: ["QUJD"])
		]);

		// Act
		OpenAiChatRequest result = OpenAiRequestMapper.MapRequest(request, "m", stream: false);

		// Assert
		var parts = Assert.IsType<JsonArray>(result.Messages[0].Content);
		Assert.Equal("text", parts[0]?["type"]?.GetValue<string>());
		Assert.Equal("describe", parts[0]?["text"]?.GetValue<string>());
		Assert.Equal("image_url", parts[1]?["type"]?.GetValue<string>());
		Assert.Equal("data:image/png;base64,QUJD", parts[1]?["image_url"]?["url"]?.GetValue<string>());
	}

	/// <summary>
	/// Verifies that an image already expressed as an absolute URL is forwarded unchanged rather than
	/// wrapped as a base64 data URL.
	/// </summary>
	[Fact]
	public void MapRequest_WhenImageIsAbsoluteUrl_ForwardsUrlUnchanged()
	{
		// Arrange
		OllamaChatRequest request = Request(
		[
			new OllamaChatMessage("user", "look", Images: ["https://example.com/cat.png"])
		]);

		// Act
		OpenAiChatRequest result = OpenAiRequestMapper.MapRequest(request, "m", stream: false);

		// Assert
		var parts = Assert.IsType<JsonArray>(result.Messages[0].Content);
		Assert.Equal("https://example.com/cat.png", parts[1]?["image_url"]?["url"]?.GetValue<string>());
	}

	/// <summary>
	/// Verifies that an assistant message carrying only tool calls drops its empty textual content to
	/// <see langword="null"/> and converts the structured arguments to the OpenAI JSON-string form.
	/// </summary>
	[Fact]
	public void MapRequest_WhenAssistantHasToolCallsAndNoText_DropsContentAndStringifiesArguments()
	{
		// Arrange
		OllamaToolCall call = new(
			new OllamaToolCallFunction(
				"get_weather",
				Description: null,
				Arguments: new JsonObject { ["city"] = "Berlin" }));
		OllamaChatRequest request = Request(
		[
			new OllamaChatMessage("assistant", string.Empty, ToolCalls: [call])
		]);

		// Act
		OpenAiChatRequest result = OpenAiRequestMapper.MapRequest(request, "m", stream: false);

		// Assert
		OpenAiChatMessage message = result.Messages[0];
		Assert.Null(message.Content);
		OpenAiToolCall mapped = Assert.Single(message.ToolCalls!);
		Assert.Equal("function", mapped.Type);
		Assert.Equal("get_weather", mapped.Function?.Name);
		Assert.Equal("""{"city":"Berlin"}""", mapped.Function?.Arguments);
	}

	/// <summary>
	/// Verifies that the assistant tool call's id is echoed onto the OpenAI tool call so the backend can
	/// correlate the subsequent tool result — essential for parallel calls to the same tool.
	/// </summary>
	[Fact]
	public void MapRequest_WhenAssistantToolCallHasId_EchoesId()
	{
		// Arrange
		OllamaToolCall call = new(
			new OllamaToolCallFunction("get_weather", Description: null, Arguments: new JsonObject()),
			Id: "call_abc123");
		OllamaChatRequest request = Request(
		[
			new OllamaChatMessage("assistant", string.Empty, ToolCalls: [call])
		]);

		// Act
		OpenAiChatRequest result = OpenAiRequestMapper.MapRequest(request, "m", stream: false);

		// Assert
		Assert.Equal("call_abc123", result.Messages[0].ToolCalls![0].Id);
	}

	/// <summary>
	/// Verifies that a tool call with no arguments object is serialized as an empty JSON object string.
	/// </summary>
	[Fact]
	public void MapRequest_WhenToolCallHasNoArguments_SerializesEmptyObject()
	{
		// Arrange
		OllamaToolCall call = new(new OllamaToolCallFunction("ping", Description: null, Arguments: null));
		OllamaChatRequest request = Request(
		[
			new OllamaChatMessage("assistant", "calling", ToolCalls: [call])
		]);

		// Act
		OpenAiChatRequest result = OpenAiRequestMapper.MapRequest(request, "m", stream: false);

		// Assert
		Assert.Equal("{}", result.Messages[0].ToolCalls![0].Function?.Arguments);
	}

	/// <summary>
	/// Verifies that a tool-result message correlates by tool name, populating both the
	/// <c>tool_call_id</c> and <c>name</c> fields OpenAI uses to match a result to its call.
	/// </summary>
	[Fact]
	public void MapRequest_WhenToolResultMessage_CorrelatesByToolName()
	{
		// Arrange
		OllamaChatRequest request = Request(
		[
			new OllamaChatMessage("tool", "22C", ToolName: "get_weather")
		]);

		// Act
		OpenAiChatRequest result = OpenAiRequestMapper.MapRequest(request, "m", stream: false);

		// Assert
		OpenAiChatMessage message = result.Messages[0];
		Assert.Equal("get_weather", message.ToolCallId);
		Assert.Equal("get_weather", message.Name);
	}

	/// <summary>
	/// Verifies that a tool-result message carrying an explicit <c>tool_call_id</c> correlates by that id
	/// rather than the tool name, while still surfacing the tool name under <c>name</c>. This is what lets
	/// the backend match a result to the exact one of several parallel calls it answers.
	/// </summary>
	[Fact]
	public void MapRequest_WhenToolResultHasToolCallId_CorrelatesById()
	{
		// Arrange
		OllamaChatRequest request = Request(
		[
			new OllamaChatMessage("tool", "22C", ToolName: "get_weather", ToolCallId: "call_abc123")
		]);

		// Act
		OpenAiChatRequest result = OpenAiRequestMapper.MapRequest(request, "m", stream: false);

		// Assert
		OpenAiChatMessage message = result.Messages[0];
		Assert.Equal("call_abc123", message.ToolCallId);
		Assert.Equal("get_weather", message.Name);
	}

	#endregion

	#region MapRequest options

	/// <summary>
	/// Verifies that the Ollama generation options are projected onto the matching standard OpenAI
	/// sampling fields. The non-standard <c>top_k</c>/<c>min_p</c> are intentionally not part of the
	/// shared output and are covered separately.
	/// </summary>
	[Fact]
	public void MapRequest_WhenOptionsSupplied_MapsSamplingFields()
	{
		// Arrange
		OllamaOptions options = new(
			Temperature: 0.7,
			TopP: 0.9,
			Seed: 42,
			NumPredict: 128,
			Stop: ["END"],
			FrequencyPenalty: 0.1,
			PresencePenalty: 0.2);
		OllamaChatRequest request = Request(options: options);

		// Act
		OpenAiChatRequest result = OpenAiRequestMapper.MapRequest(request, "m", stream: false);

		// Assert
		Assert.Equal(0.7, result.Temperature);
		Assert.Equal(0.9, result.TopP);
		Assert.Equal(42, result.Seed);
		Assert.Equal(128, result.MaxCompletionTokens);
		Assert.Equal(["END"], result.Stop);
		Assert.Equal(0.1, result.FrequencyPenalty);
		Assert.Equal(0.2, result.PresencePenalty);
	}

	/// <summary>
	/// Verifies that the non-standard <c>top_k</c> and <c>min_p</c> sampling knobs never appear in the
	/// shared serialized output, even when the client supplies them. They are absent from
	/// <see cref="OpenAiChatRequest"/> by design so a strict OpenAI backend cannot receive an unknown
	/// field; providers whose backend honors them stamp them on through their own seam. This test guards
	/// against either field being re-added to the shared contract.
	/// </summary>
	[Fact]
	public void MapRequest_WhenTopKAndMinPSupplied_OmitsThemFromSharedOutput()
	{
		// Arrange: the client sets both extensions, but the shared mapper must not carry them upstream.
		OllamaChatRequest request = Request(options: new OllamaOptions(TopK: 20, MinP: 0.1));

		// Act
		OpenAiChatRequest mapped = OpenAiRequestMapper.MapRequest(request, "m", stream: false);
		JsonObject payload = JsonSerializer.SerializeToNode(mapped, OpenAiSerialization.Options)!.AsObject();

		// Assert: neither extension is present in the wire payload the shared mapper produced.
		Assert.False(payload.ContainsKey("top_k"));
		Assert.False(payload.ContainsKey("min_p"));
	}

	/// <summary>
	/// Verifies that the inbound <c>logprobs</c> and <c>top_logprobs</c> request flags are forwarded
	/// onto the OpenAI request so the backend returns token log-probabilities.
	/// </summary>
	[Fact]
	public void MapRequest_WhenLogprobsRequested_ForwardsLogprobFlags()
	{
		// Arrange: logprobs live on the request envelope, not under options.
		OllamaChatRequest request = new(
			Model: "client-model",
			Messages: [new OllamaChatMessage("user", "hi")],
			Logprobs: true,
			TopLogprobs: 5);

		// Act
		OpenAiChatRequest result = OpenAiRequestMapper.MapRequest(request, "m", stream: false);

		// Assert
		Assert.True(result.Logprobs);
		Assert.Equal(5, result.TopLogprobs);
	}

	/// <summary>
	/// Verifies that the <c>logprobs</c> and <c>top_logprobs</c> fields stay <see langword="null"/>
	/// when the client does not request log-probabilities, so they are omitted from the upstream request.
	/// </summary>
	[Fact]
	public void MapRequest_WhenLogprobsNotRequested_LeavesLogprobFlagsNull()
	{
		// Act
		OpenAiChatRequest result = OpenAiRequestMapper.MapRequest(Request(), "m", stream: false);

		// Assert
		Assert.Null(result.Logprobs);
		Assert.Null(result.TopLogprobs);
	}

	/// <summary>
	/// Verifies that the Ollama <c>num_predict</c> token cap maps onto OpenAI
	/// <c>max_completion_tokens</c>, with the <c>-1</c> sentinel and any non-positive value treated as
	/// "no limit" (omitted).
	/// </summary>
	/// <param name="numPredict">The supplied token cap.</param>
	/// <param name="expected">The expected OpenAI <c>max_completion_tokens</c> value, or <see langword="null"/>.</param>
	[Theory]
	[InlineData(64, 64)]
	[InlineData(-1, null)]
	[InlineData(0, null)]
	[InlineData(-5, null)]
	public void MapRequest_WhenNumPredictVaries_MapsMaxCompletionTokensSentinel(int numPredict, int? expected)
	{
		// Arrange
		OllamaChatRequest request = Request(options: new OllamaOptions(NumPredict: numPredict));

		// Act
		OpenAiChatRequest result = OpenAiRequestMapper.MapRequest(request, "m", stream: false);

		// Assert
		Assert.Equal(expected, result.MaxCompletionTokens);
	}

	#endregion

	#region MapRequest tools

	/// <summary>
	/// Verifies that advertised tool definitions are forwarded, preserving the function schema.
	/// </summary>
	[Fact]
	public void MapRequest_WhenToolsAdvertised_ForwardsFunctionSchema()
	{
		// Arrange
		JsonNode parameters = new JsonObject { ["type"] = "object" };
		OllamaTool tool = new("function", new OllamaToolFunction("search", "Find things", parameters));
		OllamaChatRequest request = Request(tools: [tool]);

		// Act
		OpenAiChatRequest result = OpenAiRequestMapper.MapRequest(request, "m", stream: false);

		// Assert
		OpenAiTool mapped = Assert.Single(result.Tools!);
		Assert.Equal("function", mapped.Type);
		Assert.Equal("search", mapped.Function.Name);
		Assert.Equal("Find things", mapped.Function.Description);
	}

	/// <summary>
	/// Verifies that the <c>tools</c> field is omitted entirely when no tools are advertised.
	/// </summary>
	[Fact]
	public void MapRequest_WhenNoTools_OmitsToolsField()
	{
		// Act
		OpenAiChatRequest result = OpenAiRequestMapper.MapRequest(Request(), "m", stream: false);

		// Assert
		Assert.Null(result.Tools);
	}

	#endregion

	#region MapRequest response format

	/// <summary>
	/// Verifies that the literal <c>"json"</c> format directive becomes an OpenAI <c>json_object</c>
	/// response format.
	/// </summary>
	[Fact]
	public void MapRequest_WhenFormatIsJsonLiteral_MapsToJsonObjectMode()
	{
		// Arrange
		OllamaChatRequest request = Request(format: JsonValue.Create("json"));

		// Act
		OpenAiChatRequest result = OpenAiRequestMapper.MapRequest(request, "m", stream: false);

		// Assert
		Assert.Equal("json_object", result.ResponseFormat?["type"]?.GetValue<string>());
	}

	/// <summary>
	/// Verifies that a JSON-schema format object becomes a strict OpenAI <c>json_schema</c> directive
	/// carrying a clone of the supplied schema.
	/// </summary>
	[Fact]
	public void MapRequest_WhenFormatIsSchema_MapsToStrictJsonSchema()
	{
		// Arrange
		JsonNode schema = new JsonObject { ["type"] = "object" };
		OllamaChatRequest request = Request(format: schema);

		// Act
		OpenAiChatRequest result = OpenAiRequestMapper.MapRequest(request, "m", stream: false);

		// Assert
		Assert.Equal("json_schema", result.ResponseFormat?["type"]?.GetValue<string>());
		Assert.True(result.ResponseFormat?["json_schema"]?["strict"]?.GetValue<bool>());
		Assert.Equal("object", result.ResponseFormat?["json_schema"]?["schema"]?["type"]?.GetValue<string>());
	}

	/// <summary>
	/// Verifies that an unrecognized format string is ignored, leaving no response-format directive.
	/// </summary>
	[Fact]
	public void MapRequest_WhenFormatIsUnknownString_OmitsResponseFormat()
	{
		// Arrange
		OllamaChatRequest request = Request(format: JsonValue.Create("yaml"));

		// Act
		OpenAiChatRequest result = OpenAiRequestMapper.MapRequest(request, "m", stream: false);

		// Assert
		Assert.Null(result.ResponseFormat);
	}

	#endregion
}
