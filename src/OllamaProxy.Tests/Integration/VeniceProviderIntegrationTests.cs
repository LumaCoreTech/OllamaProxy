// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using OllamaProxy.Configuration;
using OllamaProxy.Contracts.Ollama;
using OllamaProxy.Diagnostics;
using OllamaProxy.Providers.Abstractions;
using OllamaProxy.Providers.Http;
using OllamaProxy.Providers.OpenAiProtocol;
using OllamaProxy.Providers.Venice;

namespace OllamaProxy.Tests.Integration;

/// <summary>
/// Integration tests exercising <see cref="VeniceProvider"/> against a mock Venice-compatible backend.
/// Venice publishes its capabilities as a structured <c>model_spec</c> block (available context tokens,
/// vision, function calling) plus a top-level <c>type</c> discriminator. The discovery tests verify that
/// this provider-specific shape is translated into authoritative <see cref="ModelCapabilities"/> on the
/// discovered model — vision becomes <c>SupportsVision</c>, function calling becomes <c>SupportsTools</c>,
/// and an <c>image</c> <c>type</c> withholds completion. A second group of chat tests proves that Venice's
/// specialization (vendor-parameter forcing and its reasoning dialect) does not disturb the inherited
/// parallel-tool-call correlation: distinct call ids survive both non-streaming and streamed responses, and
/// a replayed continuation keeps each result's <c>tool_call_id</c> while Venice's forced vendor flag rides
/// alongside.
/// </summary>
[Trait("Category", "Integration")]
public sealed class VeniceProviderIntegrationTests
{
	private const string BackendName = "mock";

	private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
		{ Content = new StringContent(body, Encoding.UTF8, "application/json") };

	private static VeniceProvider CreateProvider(ScriptedHandler handler) => new(
		new StubHttpClientProvider(handler),
		new StubCapabilityProber(),
		TimeProvider.System,
		Options.Create(
			new ProxyOptions
			{
				Backends =
				{
					[BackendName] = new BackendOptions
					{
						BaseUrl = "https://mock.test/v1/", ProviderType = "venice", ApiKey = "test-key-1234"
					}
				}
			}),
		new RequestTraceAccessor(),
		TestReasoningDetailsCache.CreateDefault(),
		NullLogger<VeniceProvider>.Instance);

	/// <summary>
	/// Verifies that discovery reads Venice's nested <c>model_spec.availableContextTokens</c> and
	/// translates its structured capability flags into authoritative <see cref="ModelCapabilities"/> on the
	/// discovered model.
	/// </summary>
	[Fact]
	public async Task DiscoverModelsAsync_WhenBackendReportsModelSpec_ProjectsContextAndCapabilities()
	{
		// Arrange: a Venice-style listing with a nested model_spec carrying context and capabilities.
		const string responseBody = """
		                            {"data":[{"id":"qwen3-235b","model_spec":{"availableContextTokens":131072,"capabilities":{"supportsFunctionCalling":true,"supportsVision":true}}}]}
		                            """;
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		VeniceProvider sut = CreateProvider(handler);

		// Act
		IReadOnlyList<DiscoveredModel> models = await sut.DiscoverModelsAsync(
			                                        new BackendContext(BackendName),
			                                        CancellationToken.None);

		// Assert: context length plus authoritative capabilities (vision + tools, completion conservative).
		Assert.EndsWith("models", handler.LastRequest!.RequestUri!.AbsolutePath, StringComparison.Ordinal);
		DiscoveredModel model = Assert.Single(models);
		Assert.Equal("qwen3-235b", model.Id);
		Assert.Equal(131072, model.ContextLength);
		var capabilities = Assert.IsType<ModelCapabilities>(model.Capabilities);
		Assert.True(capabilities.SupportsCompletion);
		Assert.True(capabilities.SupportsTools);
		Assert.True(capabilities.SupportsVision);
		Assert.False(capabilities.SupportsEmbeddings);
		Assert.Equal(CapabilitySource.ProviderMetadata, capabilities.Source);
	}

	/// <summary>
	/// Verifies that a Venice <c>model_spec</c> advertising neither vision nor function calling yields
	/// capabilities with vision and tools off, so the model is exposed as completion-only.
	/// </summary>
	[Fact]
	public async Task DiscoverModelsAsync_WhenModelSpecLacksCapabilities_ProjectsCompletionOnlyCapabilities()
	{
		// Arrange: a Venice spec with a context length but both capability flags false.
		const string responseBody = """
		                            {"data":[{"id":"llama-3.3-70b","model_spec":{"availableContextTokens":65536,"capabilities":{"supportsFunctionCalling":false,"supportsVision":false}}}]}
		                            """;
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		VeniceProvider sut = CreateProvider(handler);

		// Act
		IReadOnlyList<DiscoveredModel> models = await sut.DiscoverModelsAsync(
			                                        new BackendContext(BackendName),
			                                        CancellationToken.None);

		// Assert
		DiscoveredModel model = Assert.Single(models);
		Assert.Equal(65536, model.ContextLength);
		var capabilities = Assert.IsType<ModelCapabilities>(model.Capabilities);
		Assert.True(capabilities.SupportsCompletion);
		Assert.False(capabilities.SupportsTools);
		Assert.False(capabilities.SupportsVision);
		Assert.Equal(CapabilitySource.ProviderMetadata, capabilities.Source);
	}

	/// <summary>
	/// Verifies that a Venice model entry with <c>"type": "image"</c> is projected with a capability set
	/// whose <c>SupportsCompletion</c> is <see langword="false"/>, so image-generation models are not
	/// exposed for completion.
	/// </summary>
	[Fact]
	public async Task DiscoverModelsAsync_WhenModelTypeIsImage_WithholdsCompletion()
	{
		// Arrange: a Venice listing where the model is flagged as an image-generation model.
		const string responseBody = """
		                            {"data":[{"id":"fluently-xl","model_spec":{"availableContextTokens":4096},"type":"image"}]}
		                            """;
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		VeniceProvider sut = CreateProvider(handler);

		// Act
		IReadOnlyList<DiscoveredModel> models = await sut.DiscoverModelsAsync(
			                                        new BackendContext(BackendName),
			                                        CancellationToken.None);

		// Assert: the image output modality must drive SupportsCompletion=false.
		DiscoveredModel model = Assert.Single(models);
		var capabilities = Assert.IsType<ModelCapabilities>(model.Capabilities);
		Assert.False(capabilities.SupportsCompletion);
		Assert.Equal(CapabilitySource.ProviderMetadata, capabilities.Source);
	}

	/// <summary>
	/// Verifies that a Venice model entry with <c>"type": "text"</c> is projected with a capability set
	/// whose <c>SupportsCompletion</c> is <see langword="true"/>, so it is recognised as completion-capable.
	/// </summary>
	[Fact]
	public async Task DiscoverModelsAsync_WhenModelTypeIsText_AllowsCompletion()
	{
		// Arrange: a Venice listing where the model is a standard language model.
		const string responseBody = """
		                            {"data":[{"id":"llama-3.2-3b","model_spec":{"availableContextTokens":131072},"type":"text"}]}
		                            """;
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		VeniceProvider sut = CreateProvider(handler);

		// Act
		IReadOnlyList<DiscoveredModel> models = await sut.DiscoverModelsAsync(
			                                        new BackendContext(BackendName),
			                                        CancellationToken.None);

		// Assert: the text output modality keeps SupportsCompletion=true.
		DiscoveredModel model = Assert.Single(models);
		var capabilities = Assert.IsType<ModelCapabilities>(model.Capabilities);
		Assert.True(capabilities.SupportsCompletion);
		Assert.Equal(CapabilitySource.ProviderMetadata, capabilities.Source);
	}

	/// <summary>
	/// Verifies that discovery projects Venice's <c>model_spec</c> descriptive fields onto the neutral
	/// <see cref="ProviderModelMetadata"/> — including the <c>quantization</c> capability, the one field that
	/// maps onto Ollama's own quantization-level notion — and adopts Venice's already-per-million pricing directly.
	/// </summary>
	[Fact]
	public async Task DiscoverModelsAsync_WhenModelSpecCarriesMetadata_ProjectsNeutralMetadata()
	{
		// Arrange: a spec carrying name/description/source/maxCompletionTokens, quantization, and USD pricing.
		const string responseBody = """
		                            {"data":[{"id":"llama-3.2-3b","model_spec":{"availableContextTokens":131072,"name":"Llama 3.2 3B","description":"Small fast model.","modelSource":"https://huggingface.co/x","maxCompletionTokens":4096,"capabilities":{"quantization":"fp16"},"pricing":{"input":{"usd":0.15},"output":{"usd":0.6}}}}]}
		                            """;
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		VeniceProvider sut = CreateProvider(handler);

		// Act
		IReadOnlyList<DiscoveredModel> models = await sut.DiscoverModelsAsync(
			                                        new BackendContext(BackendName),
			                                        CancellationToken.None);

		// Assert: every reported descriptive field maps across; Venice pricing is already per-million.
		DiscoveredModel model = Assert.Single(models);
		var metadata = Assert.IsType<ProviderModelMetadata>(model.Metadata);
		Assert.Equal("Llama 3.2 3B", metadata.DisplayName);
		Assert.Equal("Small fast model.", metadata.Description);
		Assert.Equal("fp16", metadata.Quantization);
		Assert.Equal(4096, metadata.MaxCompletionTokens);
		Assert.Equal("https://huggingface.co/x", metadata.SourceUrl);
		Assert.Equal(0.15m, metadata.PromptUsdPerMillionTokens);
		Assert.Equal(0.6m, metadata.CompletionUsdPerMillionTokens);
	}

	/// <summary>
	/// Verifies that a Venice entry whose <c>model_spec</c> carries no descriptive fields projects a
	/// <see langword="null"/> <see cref="DiscoveredModel.Metadata"/> rather than an empty record.
	/// </summary>
	[Fact]
	public async Task DiscoverModelsAsync_WhenModelSpecLacksMetadata_ProjectsNullMetadata()
	{
		// Arrange: a spec carrying only a context window — no name, pricing, quantization, or source.
		const string responseBody = """
		                            {"data":[{"id":"llama-3.2-3b","model_spec":{"availableContextTokens":131072}}]}
		                            """;
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		VeniceProvider sut = CreateProvider(handler);

		// Act
		IReadOnlyList<DiscoveredModel> models = await sut.DiscoverModelsAsync(
			                                        new BackendContext(BackendName),
			                                        CancellationToken.None);

		// Assert
		DiscoveredModel model = Assert.Single(models);
		Assert.Null(model.Metadata);
	}

	#region Passthrough vendor parameters

	/// <summary>
	/// Verifies that the verbatim <c>/v1</c> chat-completions passthrough enforces Venice's vendor-prompt
	/// suppression just like the Ollama-native path: <c>venice_parameters.include_venice_system_prompt</c>
	/// is forced to <see langword="false"/>, overwriting the client's <see langword="true"/>. Without this
	/// the <c>/v1</c> route would silently activate Venice's system prompt, breaking the chassis promise of
	/// transparent request semantics that the <c>/api/chat</c> route already keeps.
	/// </summary>
	[Fact]
	public async Task ForwardJsonAsync_WhenClientEnablesVeniceSystemPrompt_ForcesItOff()
	{
		// Arrange: the client tries to turn the vendor system prompt on; the proxy must override it to false.
		const string responseBody = """{"id":"c1","model":"qwen3-235b","object":"chat.completion"}""";
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		VeniceProvider sut = CreateProvider(handler);
		JsonObject body = new()
		{
			["model"] = "qwen3-235b",
			["stream"] = false,
			["venice_parameters"] = new JsonObject { ["include_venice_system_prompt"] = true }
		};

		// Act
		await sut.ForwardJsonAsync(
			new BackendContext(BackendName),
			"chat/completions",
			body,
			pinnedEffort: null,
			CancellationToken.None);

		// Assert: the forced false reached the wire, overwriting the client's true.
		JsonObject sent = JsonNode.Parse(handler.LastRequestBody!)!.AsObject();
		var veniceParameters = Assert.IsType<JsonObject>(sent["venice_parameters"]);
		Assert.False(veniceParameters["include_venice_system_prompt"]!.GetValue<bool>());
	}

	/// <summary>
	/// Verifies that the vendor-prompt suppression is gated to the chat-completions path: a legacy
	/// <c>/v1/completions</c> passthrough carries no <c>venice_parameters</c>, because the vendor system
	/// prompt is a chat concept and the text-completion body must be forwarded untouched, mirroring how the
	/// reasoning policy is also withheld from that path.
	/// </summary>
	[Fact]
	public async Task ForwardJsonAsync_WhenPathIsLegacyCompletions_DoesNotForceVendorParameters()
	{
		// Arrange: a backend default would apply on chat, but the target is the legacy text-completions path.
		const string responseBody = """{"id":"c1","model":"qwen3-235b","object":"text_completion"}""";
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		VeniceProvider sut = CreateProvider(handler);
		JsonObject body = new() { ["model"] = "qwen3-235b", ["stream"] = false };

		// Act
		await sut.ForwardJsonAsync(
			new BackendContext(BackendName),
			"completions",
			body,
			pinnedEffort: null,
			CancellationToken.None);

		// Assert: the body is forwarded untouched — no venice_parameters block was injected.
		Assert.DoesNotContain("venice_parameters", handler.LastRequestBody, StringComparison.Ordinal);
	}

	#endregion

	#region Parallel tool calls

	/// <summary>
	/// Verifies that two parallel calls to the <em>same</em> tool, returned in one non-streaming completion,
	/// each surface to the client carrying their own distinct call id when routed through Venice. The id is
	/// the only thing that distinguishes the calls — both are <c>get_weather</c> — so Venice's specialization
	/// must not disturb the inherited correlation the proxy preserves.
	/// </summary>
	[Fact]
	public async Task CompleteChatAsync_WhenBackendReturnsParallelToolCalls_CarriesEachDistinctId()
	{
		// Arrange: one assistant turn calling get_weather twice (Berlin and Hamburg), each with its own id.
		const string responseBody = """
		                            {"id":"c1","model":"qwen3-235b","choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_berlin","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Berlin\"}"}},{"id":"call_hamburg","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Hamburg\"}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":10,"completion_tokens":8,"total_tokens":18}}
		                            """;
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		VeniceProvider sut = CreateProvider(handler);
		OllamaChatRequest request = new(
			"client-model",
			[new OllamaChatMessage("user", "Weather in Berlin and Hamburg?")]);

		// Act
		OllamaChatResponse result = await sut.CompleteChatAsync(
			                            new BackendContext(BackendName),
			                            "qwen3-235b",
			                            request,
			                            pinnedEffort: null,
			                            CancellationToken.None);

		// Assert: both calls surface, each tied to its id and its city — the id band the client correlates by.
		Assert.Equal(2, result.Message.ToolCalls!.Count);

		OllamaToolCall berlin = result.Message.ToolCalls[0];
		Assert.Equal("call_berlin", berlin.Id);
		Assert.Equal("get_weather", berlin.Function.Name);
		Assert.Equal("Berlin", berlin.Function.Arguments?["city"]?.GetValue<string>());

		OllamaToolCall hamburg = result.Message.ToolCalls[1];
		Assert.Equal("call_hamburg", hamburg.Id);
		Assert.Equal("get_weather", hamburg.Function.Name);
		Assert.Equal("Hamburg", hamburg.Function.Arguments?["city"]?.GetValue<string>());
	}

	/// <summary>
	/// Verifies that two parallel calls to the same tool, streamed across several SSE deltas (each call's id
	/// and name arrive on its opening delta, its arguments in later fragments), are reassembled onto the
	/// terminal chunk with both ids intact when routed through Venice — the streaming counterpart to the
	/// non-streaming parallel-tool-call guard.
	/// </summary>
	[Fact]
	public async Task StreamChatAsync_WhenBackendStreamsParallelToolCalls_ReassemblesBothIdsOnTerminalChunk()
	{
		// Arrange: index 0 (Berlin) and index 1 (Hamburg) each open with id+name, then stream their
		// arguments; a finish-reason delta and a usage-only event close the stream.
		string sse = string.Join(
			"\n",
			"""data: {"id":"c1","model":"qwen3-235b","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_berlin","type":"function","function":{"name":"get_weather","arguments":""}}]},"finish_reason":null}]}""",
			"""data: {"id":"c1","model":"qwen3-235b","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"city\":\"Berlin\"}"}}]},"finish_reason":null}]}""",
			"""data: {"id":"c1","model":"qwen3-235b","choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"id":"call_hamburg","type":"function","function":{"name":"get_weather","arguments":""}}]},"finish_reason":null}]}""",
			"""data: {"id":"c1","model":"qwen3-235b","choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"function":{"arguments":"{\"city\":\"Hamburg\"}"}}]},"finish_reason":null}]}""",
			"""data: {"id":"c1","model":"qwen3-235b","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
			"""data: {"id":"c1","model":"qwen3-235b","choices":[],"usage":{"prompt_tokens":10,"completion_tokens":8,"total_tokens":18}}""",
			"data: [DONE]",
			string.Empty);
		ScriptedHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
		});
		VeniceProvider sut = CreateProvider(handler);
		OllamaChatRequest request = new(
			"client-model",
			[new OllamaChatMessage("user", "Weather in Berlin and Hamburg?")],
			Stream: true);

		// Act
		List<OllamaChatResponse> chunks = [];
		await foreach (OllamaChatResponse chunk in sut.StreamChatAsync(
			               new BackendContext(BackendName),
			               "qwen3-235b",
			               request,
			               pinnedEffort: null,
			               CancellationToken.None))
		{
			chunks.Add(chunk);
		}

		// Assert: the terminal chunk carries both reassembled calls, each with its id and city preserved.
		OllamaChatResponse terminal = Assert.Single(chunks, c => c.Done);
		Assert.Equal(2, terminal.Message.ToolCalls!.Count);

		OllamaToolCall berlin = terminal.Message.ToolCalls[0];
		Assert.Equal("call_berlin", berlin.Id);
		Assert.Equal("Berlin", berlin.Function.Arguments?["city"]?.GetValue<string>());

		OllamaToolCall hamburg = terminal.Message.ToolCalls[1];
		Assert.Equal("call_hamburg", hamburg.Id);
		Assert.Equal("Hamburg", hamburg.Function.Arguments?["city"]?.GetValue<string>());
	}

	/// <summary>
	/// Verifies the inbound leg of the round-trip through Venice: when the client replays a turn with two
	/// parallel tool calls and their two results, the outgoing request preserves each assistant call's id and
	/// stamps each tool result with the matching <c>tool_call_id</c> — and Venice's forced
	/// <c>venice_parameters.include_venice_system_prompt = false</c> rides alongside without disturbing that
	/// correlation. This proves the specialization composes with the shared tool-id wiring rather than
	/// shadowing it.
	/// </summary>
	[Fact]
	public async Task
		CompleteChatAsync_WhenClientReplaysParallelToolResults_CorrelatesEachResultByIdAlongsideForcedVendorFlag()
	{
		// Arrange: a continuation turn — the prior assistant called get_weather twice, and the client now
		// returns both results, each keyed to the call it answers by tool_call_id.
		const string responseBody = """
		                            {"id":"c2","model":"qwen3-235b","choices":[{"index":0,"message":{"role":"assistant","content":"Berlin 22C, Hamburg 9C"},"finish_reason":"stop"}],"usage":{"prompt_tokens":20,"completion_tokens":10,"total_tokens":30}}
		                            """;
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		VeniceProvider sut = CreateProvider(handler);

		OllamaToolCall berlinCall = new(
			new OllamaToolCallFunction(
				"get_weather",
				Description: null,
				Arguments: new JsonObject { ["city"] = "Berlin" }),
			Id: "call_berlin");
		OllamaToolCall hamburgCall = new(
			new OllamaToolCallFunction(
				"get_weather",
				Description: null,
				Arguments: new JsonObject { ["city"] = "Hamburg" }),
			Id: "call_hamburg");
		OllamaChatRequest request = new(
			"client-model",
			[
				new OllamaChatMessage("user", "Weather in Berlin and Hamburg?"),
				new OllamaChatMessage("assistant", string.Empty, ToolCalls: [berlinCall, hamburgCall]),
				new OllamaChatMessage("tool", "22C", ToolName: "get_weather", ToolCallId: "call_berlin"),
				new OllamaChatMessage("tool", "9C", ToolName: "get_weather", ToolCallId: "call_hamburg")
			]);

		// Act
		await sut.CompleteChatAsync(
			new BackendContext(BackendName),
			"qwen3-235b",
			request,
			pinnedEffort: null,
			CancellationToken.None);

		// Assert: the wire body preserves the order and, crucially, the id band on both legs.
		JsonObject body = JsonNode.Parse(handler.LastRequestBody!)!.AsObject();
		JsonArray messages = body["messages"]!.AsArray();

		// The assistant turn keeps each call's id alongside its city.
		JsonArray assistantCalls = messages[1]!["tool_calls"]!.AsArray();
		Assert.Equal("call_berlin", assistantCalls[0]!["id"]!.GetValue<string>());
		Assert.Contains("Berlin", assistantCalls[0]!["function"]!["arguments"]!.GetValue<string>());
		Assert.Equal("call_hamburg", assistantCalls[1]!["id"]!.GetValue<string>());
		Assert.Contains("Hamburg", assistantCalls[1]!["function"]!["arguments"]!.GetValue<string>());

		// Each tool result is correlated to its originating call by tool_call_id, not by the shared name.
		JsonObject berlinResult = messages[2]!.AsObject();
		Assert.Equal("call_berlin", berlinResult["tool_call_id"]!.GetValue<string>());
		Assert.Equal("22C", berlinResult["content"]!.GetValue<string>());

		JsonObject hamburgResult = messages[3]!.AsObject();
		Assert.Equal("call_hamburg", hamburgResult["tool_call_id"]!.GetValue<string>());
		Assert.Equal("9C", hamburgResult["content"]!.GetValue<string>());

		// Venice's forced vendor flag is emitted on the same request without perturbing the tool correlation.
		var veniceParameters = Assert.IsType<JsonObject>(body["venice_parameters"]);
		Assert.False(veniceParameters["include_venice_system_prompt"]!.GetValue<bool>());
	}

	#endregion

	/// <summary>Captures the request (and its serialized body) and returns a scripted response.</summary>
	private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
	{
		public HttpRequestMessage? LastRequest { get; private set; }

		public string? LastRequestBody { get; private set; }

		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken  cancellationToken)
		{
			LastRequest = request;
			if (request.Content is not null)
				LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);

			return responder(request);
		}
	}

	/// <summary>Hands out a single <see cref="HttpClient"/> over the scripted handler with a test base address.</summary>
	private sealed class StubHttpClientProvider(ScriptedHandler handler) : IBackendHttpClientProvider
	{
		public HttpClient CreateClient(string backendName) => new(handler, disposeHandler: false)
			{ BaseAddress = new Uri("https://mock.test/v1/") };
	}

	/// <summary>
	/// Reports every probe as inconclusive so discovery does not depend on probing internals; these tests
	/// exercise the metadata-to-capability projection, not active probing.
	/// </summary>
	private sealed class StubCapabilityProber : ICapabilityProber
	{
		public Task<bool?> ProbeCompletionSupportAsync(
			BackendContext    backend,
			string            modelId,
			CancellationToken cancellationToken) => Task.FromResult<bool?>(null);

		public Task<bool?> ProbeToolSupportAsync(
			BackendContext    backend,
			string            modelId,
			CancellationToken cancellationToken) => Task.FromResult<bool?>(null);

		public Task<bool?> ProbeVisionSupportAsync(
			BackendContext    backend,
			string            modelId,
			CancellationToken cancellationToken) => Task.FromResult<bool?>(null);

		public Task<bool?> ProbeEmbeddingSupportAsync(
			BackendContext    backend,
			string            modelId,
			CancellationToken cancellationToken) => Task.FromResult<bool?>(null);
	}
}
