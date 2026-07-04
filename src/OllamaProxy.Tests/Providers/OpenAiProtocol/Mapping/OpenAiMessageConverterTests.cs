// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json.Nodes;

using OllamaProxy.Contracts.Ollama;
using OllamaProxy.Providers.OpenAiProtocol.Contracts;
using OllamaProxy.Providers.OpenAiProtocol.Mapping;

namespace OllamaProxy.Tests.Providers.OpenAiProtocol.Mapping;

/// <summary>
/// Tests for <see cref="OpenAiMessageConverter"/>, the shared OpenAI-to-Ollama conversion helpers.
/// The story walks the members in the order a response is decoded: finish-reason normalization, text
/// extraction from the several content shapes, reasoning extraction, log-probability unwrapping,
/// tool-call conversion (including the malformed-argument fallback), and argument parsing.
/// </summary>
[Trait("Category", "Unit")]
public sealed class OpenAiMessageConverterTests
{
	#region MapFinishReason

	/// <summary>
	/// Verifies that <see cref="OpenAiMessageConverter.MapFinishReason"/> normalizes the absent and
	/// <c>tool_calls</c> reasons to <c>stop</c> while passing other reasons through unchanged.
	/// </summary>
	/// <param name="finishReason">The OpenAI finish reason under test.</param>
	/// <param name="expected">The expected Ollama done reason.</param>
	[Theory]
	[InlineData(null, "stop")]
	[InlineData("tool_calls", "stop")]
	[InlineData("stop", "stop")]
	[InlineData("length", "length")]
	public void MapFinishReason_WhenGivenReason_NormalizesToOllamaDoneReason(string? finishReason, string expected)
	{
		// Act
		string mapped = OpenAiMessageConverter.MapFinishReason(finishReason);

		// Assert
		Assert.Equal(expected, mapped);
	}

	#endregion

	#region ExtractText

	/// <summary>
	/// Verifies that <see cref="OpenAiMessageConverter.ExtractText"/> returns an empty string for a
	/// <see langword="null"/> content node.
	/// </summary>
	[Fact]
	public void ExtractText_WhenContentIsNull_ReturnsEmptyString()
	{
		// Act
		string text = OpenAiMessageConverter.ExtractText(null);

		// Assert
		Assert.Equal(string.Empty, text);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiMessageConverter.ExtractText"/> returns the value of a plain
	/// string content node unchanged.
	/// </summary>
	[Fact]
	public void ExtractText_WhenContentIsString_ReturnsThatString()
	{
		// Arrange
		JsonNode content = JsonValue.Create("hello world");

		// Act
		string text = OpenAiMessageConverter.ExtractText(content);

		// Assert
		Assert.Equal("hello world", text);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiMessageConverter.ExtractText"/> concatenates the text parts of a
	/// multimodal content array and ignores non-text parts such as images.
	/// </summary>
	[Fact]
	public void ExtractText_WhenContentIsPartArray_ConcatenatesTextPartsAndIgnoresOthers()
	{
		// Arrange: two text parts surrounding an image part that must be skipped.
		JsonArray content =
		[
			new JsonObject { ["type"] = "text", ["text"] = "foo" },
			new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "x" } },
			new JsonObject { ["type"] = "text", ["text"] = "bar" }
		];

		// Act
		string text = OpenAiMessageConverter.ExtractText(content);

		// Assert
		Assert.Equal("foobar", text);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiMessageConverter.ExtractText"/> returns an empty string for an
	/// unsupported content shape such as a bare number.
	/// </summary>
	[Fact]
	public void ExtractText_WhenContentIsUnsupportedShape_ReturnsEmptyString()
	{
		// Arrange
		JsonNode content = JsonValue.Create(42);

		// Act
		string text = OpenAiMessageConverter.ExtractText(content);

		// Assert
		Assert.Equal(string.Empty, text);
	}

	#endregion

	#region ExtractReasoning

	/// <summary>
	/// Verifies that <see cref="OpenAiMessageConverter.ExtractReasoning"/> reads the de-facto standard
	/// <see cref="OpenAiChatMessage.ReasoningContent"/> field.
	/// </summary>
	[Fact]
	public void ExtractReasoning_WhenReasoningContentPresent_ReturnsText()
	{
		// Arrange
		OpenAiChatMessage message = new("assistant", JsonValue.Create("hi"), ReasoningContent: "deep thought");

		// Act
		string? reasoning = OpenAiMessageConverter.ExtractReasoning(message);

		// Assert
		Assert.Equal("deep thought", reasoning);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiMessageConverter.ExtractReasoning"/> falls back to OpenRouter's
	/// <see cref="OpenAiChatMessage.Reasoning"/> spelling when the standard field is absent.
	/// </summary>
	[Fact]
	public void ExtractReasoning_WhenOnlyOpenRouterReasoningPresent_ReturnsText()
	{
		// Arrange
		OpenAiChatMessage message = new("assistant", JsonValue.Create("hi"), Reasoning: "router thought");

		// Act
		string? reasoning = OpenAiMessageConverter.ExtractReasoning(message);

		// Assert
		Assert.Equal("router thought", reasoning);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiMessageConverter.ExtractReasoning"/> prefers
	/// <see cref="OpenAiChatMessage.ReasoningContent"/> over <see cref="OpenAiChatMessage.Reasoning"/>
	/// when both are present.
	/// </summary>
	[Fact]
	public void ExtractReasoning_WhenBothFieldsPresent_PrefersReasoningContent()
	{
		// Arrange
		OpenAiChatMessage message = new(
			"assistant",
			JsonValue.Create("hi"),
			ReasoningContent: "standard",
			Reasoning: "router");

		// Act
		string? reasoning = OpenAiMessageConverter.ExtractReasoning(message);

		// Assert
		Assert.Equal("standard", reasoning);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiMessageConverter.ExtractReasoning"/> returns <see langword="null"/>
	/// for a message without a reasoning channel, so callers can omit the field.
	/// </summary>
	[Fact]
	public void ExtractReasoning_WhenNoReasoning_ReturnsNull()
	{
		// Arrange
		OpenAiChatMessage message = new("assistant", JsonValue.Create("hi"));

		// Act
		string? reasoning = OpenAiMessageConverter.ExtractReasoning(message);

		// Assert
		Assert.Null(reasoning);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiMessageConverter.ExtractReasoning"/> treats an empty reasoning
	/// string as absent, returning <see langword="null"/> rather than an empty value.
	/// </summary>
	[Fact]
	public void ExtractReasoning_WhenReasoningEmpty_ReturnsNull()
	{
		// Arrange
		OpenAiChatMessage message = new("assistant", JsonValue.Create("hi"), ReasoningContent: string.Empty);

		// Act
		string? reasoning = OpenAiMessageConverter.ExtractReasoning(message);

		// Assert
		Assert.Null(reasoning);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiMessageConverter.ExtractReasoning"/> returns <see langword="null"/>
	/// for a <see langword="null"/> message.
	/// </summary>
	[Fact]
	public void ExtractReasoning_WhenMessageIsNull_ReturnsNull()
	{
		// Act
		string? reasoning = OpenAiMessageConverter.ExtractReasoning(null);

		// Assert
		Assert.Null(reasoning);
	}

	#endregion

	#region ExtractLogprobs

	/// <summary>
	/// Verifies that <see cref="OpenAiMessageConverter.ExtractLogprobs"/> unwraps OpenAI's
	/// <c>content</c>-nested log-probability object into the bare per-token array Ollama exposes.
	/// </summary>
	[Fact]
	public void ExtractLogprobs_WhenContentArrayPresent_UnwrapsToBareArray()
	{
		// Arrange: OpenAI nests the per-token entries under a content array.
		JsonNode logprobs = new JsonObject
		{
			["content"] = new JsonArray
			{
				new JsonObject { ["token"] = "Hel", ["logprob"] = -0.1 },
				new JsonObject { ["token"] = "lo", ["logprob"] = -0.2 }
			}
		};

		// Act
		JsonNode? result = OpenAiMessageConverter.ExtractLogprobs(logprobs);

		// Assert: the content wrapper is stripped, leaving the bare per-token array in order.
		var array = Assert.IsType<JsonArray>(result);
		Assert.Equal(2, array.Count);
		Assert.Equal("Hel", array[0]?["token"]?.GetValue<string>());
		Assert.Equal(-0.1, array[0]?["logprob"]?.GetValue<double>());
		Assert.Equal("lo", array[1]?["token"]?.GetValue<string>());
		Assert.Equal(-0.2, array[1]?["logprob"]?.GetValue<double>());
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiMessageConverter.ExtractLogprobs"/> returns a detached array so it
	/// can be re-parented onto the outgoing Ollama response without throwing.
	/// </summary>
	[Fact]
	public void ExtractLogprobs_WhenContentArrayPresent_ReturnsDetachedArray()
	{
		// Arrange
		JsonNode logprobs = new JsonObject
		{
			["content"] = new JsonArray { new JsonObject { ["token"] = "hi" } }
		};

		// Act
		JsonNode? result = OpenAiMessageConverter.ExtractLogprobs(logprobs);

		// Assert: a detached node has no parent, so re-parenting onto the Ollama response cannot throw.
		var array = Assert.IsType<JsonArray>(result);
		Assert.Null(array.Parent);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiMessageConverter.ExtractLogprobs"/> returns <see langword="null"/>
	/// for a <see langword="null"/> node, so callers omit the field.
	/// </summary>
	[Fact]
	public void ExtractLogprobs_WhenNull_ReturnsNull()
	{
		// Act
		JsonNode? result = OpenAiMessageConverter.ExtractLogprobs(null);

		// Assert
		Assert.Null(result);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiMessageConverter.ExtractLogprobs"/> returns <see langword="null"/>
	/// when the object carries no <c>content</c> member.
	/// </summary>
	[Fact]
	public void ExtractLogprobs_WhenContentMemberMissing_ReturnsNull()
	{
		// Arrange
		JsonNode logprobs = new JsonObject { ["other"] = 1 };

		// Act
		JsonNode? result = OpenAiMessageConverter.ExtractLogprobs(logprobs);

		// Assert
		Assert.Null(result);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiMessageConverter.ExtractLogprobs"/> returns <see langword="null"/>
	/// when the <c>content</c> member is not an array.
	/// </summary>
	[Fact]
	public void ExtractLogprobs_WhenContentNotArray_ReturnsNull()
	{
		// Arrange
		JsonNode logprobs = new JsonObject { ["content"] = "not-an-array" };

		// Act
		JsonNode? result = OpenAiMessageConverter.ExtractLogprobs(logprobs);

		// Assert
		Assert.Null(result);
	}

	#endregion

	#region ConvertToolCalls

	/// <summary>
	/// Verifies that <see cref="OpenAiMessageConverter.ConvertToolCalls"/> returns <see langword="null"/>
	/// when there are no tool calls, so the field is omitted entirely.
	/// </summary>
	[Fact]
	public void ConvertToolCalls_WhenNoToolCalls_ReturnsNull()
	{
		// Act
		IReadOnlyList<OllamaToolCall>? converted = OpenAiMessageConverter.ConvertToolCalls(null);

		// Assert
		Assert.Null(converted);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiMessageConverter.ConvertToolCalls"/> converts an OpenAI tool call
	/// (string arguments) into an Ollama tool call with the arguments parsed into a structured object.
	/// </summary>
	[Fact]
	public void ConvertToolCalls_WhenArgumentsAreValidJson_ParsesIntoStructuredObject()
	{
		// Arrange
		OpenAiToolCall[] calls =
		[
			new("id-1", "function", new OpenAiToolCallFunction("get_weather", """{"city":"Berlin"}"""))
		];

		// Act
		IReadOnlyList<OllamaToolCall>? converted = OpenAiMessageConverter.ConvertToolCalls(calls);

		// Assert
		OllamaToolCall single = Assert.Single(converted!);
		Assert.Equal("id-1", single.Id);
		Assert.Equal("get_weather", single.Function.Name);
		Assert.Equal("Berlin", single.Function.Arguments?["city"]?.GetValue<string>());
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiMessageConverter.ConvertToolCalls"/> substitutes an empty function
	/// name when the upstream tool call omits one.
	/// </summary>
	[Fact]
	public void ConvertToolCalls_WhenFunctionNameMissing_UsesEmptyName()
	{
		// Arrange: a tool call whose function detail is entirely absent.
		OpenAiToolCall[] calls = [new("id-1", "function", Function: null)];

		// Act
		IReadOnlyList<OllamaToolCall>? converted = OpenAiMessageConverter.ConvertToolCalls(calls);

		// Assert
		OllamaToolCall single = Assert.Single(converted!);
		Assert.Equal(string.Empty, single.Function.Name);
	}

	#endregion

	#region ParseArgumentsOrEmpty

	/// <summary>
	/// Verifies that <see cref="OpenAiMessageConverter.ParseArgumentsOrEmpty"/> returns an empty object
	/// for a <see langword="null"/>, empty, or whitespace argument string.
	/// </summary>
	/// <param name="arguments">The blank argument string under test.</param>
	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void ParseArgumentsOrEmpty_WhenBlank_ReturnsEmptyObject(string? arguments)
	{
		// Act
		JsonNode node = OpenAiMessageConverter.ParseArgumentsOrEmpty(arguments);

		// Assert
		var obj = Assert.IsType<JsonObject>(node);
		Assert.Empty(obj);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiMessageConverter.ParseArgumentsOrEmpty"/> parses a well-formed
	/// JSON argument string into the corresponding structured node.
	/// </summary>
	[Fact]
	public void ParseArgumentsOrEmpty_WhenValidJson_ReturnsParsedNode()
	{
		// Act
		JsonNode node = OpenAiMessageConverter.ParseArgumentsOrEmpty("""{"n":7}""");

		// Assert
		Assert.Equal(7, node["n"]?.GetValue<int>());
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiMessageConverter.ParseArgumentsOrEmpty"/> preserves a malformed
	/// argument fragment as a raw string node rather than discarding it.
	/// </summary>
	[Fact]
	public void ParseArgumentsOrEmpty_WhenMalformed_ReturnsRawStringNode()
	{
		// Arrange: an unterminated JSON fragment that cannot be parsed.
		const string fragment = """{"city":""";

		// Act
		JsonNode node = OpenAiMessageConverter.ParseArgumentsOrEmpty(fragment);

		// Assert
		Assert.Equal(fragment, node.GetValue<string>());
	}

	#endregion
}
