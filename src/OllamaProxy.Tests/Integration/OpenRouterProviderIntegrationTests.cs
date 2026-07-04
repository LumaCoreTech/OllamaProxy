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
using OllamaProxy.Providers.OpenRouter;

namespace OllamaProxy.Tests.Integration;

/// <summary>
/// Integration tests exercising <see cref="OpenRouterProvider"/>'s discovery projection against a mock
/// OpenRouter-compatible backend. OpenRouter annotates each model with rich, structured metadata —
/// input/output modalities, supported parameters, and two context-window sources — which this adapter
/// translates into authoritative <see cref="ModelCapabilities"/>. The tests verify the capability
/// projection and the context-window fallback from the top-level <c>context_length</c> to the nested
/// <c>top_provider.context_length</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OpenRouterProviderIntegrationTests
{
	private const string BackendName = "mock";

	private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
		{ Content = new StringContent(body, Encoding.UTF8, "application/json") };

	private static OpenRouterProvider CreateProvider(ScriptedHandler handler) => new(
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
						BaseUrl = "https://mock.test/v1/", ProviderType = "openrouter",
						ApiKey = "test-key-1234"
					}
				}
			}),
		new RequestTraceAccessor(),
		TestReasoningDetailsCache.CreateDefault(),
		NullLogger<OpenRouterProvider>.Instance);

	/// <summary>
	/// Verifies that discovery translates OpenRouter's native modality and supported-parameter metadata
	/// into authoritative <see cref="ModelCapabilities"/> so vision and tool support are inferred directly.
	/// </summary>
	[Fact]
	public async Task DiscoverModelsAsync_WhenBackendReportsArchitecture_ProjectsCapabilities()
	{
		// Arrange: an OpenRouter listing carrying input/output modalities and supported parameters.
		const string responseBody = """
		                            {"data":[{"id":"anthropic/claude","created":1,"architecture":{"input_modalities":["text","image"],"output_modalities":["text"]},"supported_parameters":["tools"]}]}
		                            """;
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		OpenRouterProvider sut = CreateProvider(handler);

		// Act
		IReadOnlyList<DiscoveredModel> models = await sut.DiscoverModelsAsync(
			                                        new BackendContext(BackendName),
			                                        CancellationToken.None);

		// Assert: image input => vision, tools param => tools, text output => completion.
		Assert.EndsWith("models", handler.LastRequest!.RequestUri!.AbsolutePath, StringComparison.Ordinal);
		DiscoveredModel model = Assert.Single(models);
		Assert.Equal("anthropic/claude", model.Id);
		var capabilities = Assert.IsType<ModelCapabilities>(model.Capabilities);
		Assert.True(capabilities.SupportsCompletion);
		Assert.True(capabilities.SupportsTools);
		Assert.True(capabilities.SupportsVision);
		Assert.False(capabilities.SupportsEmbeddings);
		Assert.Equal(CapabilitySource.ProviderMetadata, capabilities.Source);
	}

	/// <summary>
	/// Verifies that discovery reads OpenRouter's top-level <c>context_length</c> as the model's context
	/// window.
	/// </summary>
	[Fact]
	public async Task DiscoverModelsAsync_WhenBackendReportsContextLength_ProjectsContextLength()
	{
		// Arrange: an OpenRouter-style listing carrying the top-level context_length.
		const string responseBody = """{"data":[{"id":"mistral-large","context_length":131072}]}""";
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		OpenRouterProvider sut = CreateProvider(handler);

		// Act
		IReadOnlyList<DiscoveredModel> models = await sut.DiscoverModelsAsync(
			                                        new BackendContext(BackendName),
			                                        CancellationToken.None);

		// Assert
		DiscoveredModel model = Assert.Single(models);
		Assert.Equal(131072, model.ContextLength);
	}

	/// <summary>
	/// Verifies that discovery falls back to OpenRouter's nested <c>top_provider.context_length</c> when
	/// the top-level <c>context_length</c> is absent.
	/// </summary>
	[Fact]
	public async Task DiscoverModelsAsync_WhenBackendReportsOnlyTopProviderContext_ProjectsContextLength()
	{
		// Arrange: an OpenRouter-style listing carrying the window only under top_provider.
		const string responseBody = """{"data":[{"id":"anthropic/claude","top_provider":{"context_length":200000}}]}""";
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		OpenRouterProvider sut = CreateProvider(handler);

		// Act
		IReadOnlyList<DiscoveredModel> models = await sut.DiscoverModelsAsync(
			                                        new BackendContext(BackendName),
			                                        CancellationToken.None);

		// Assert
		DiscoveredModel model = Assert.Single(models);
		Assert.Equal(200000, model.ContextLength);
	}

	/// <summary>
	/// Verifies that the top-level <c>context_length</c> takes precedence over the nested
	/// <c>top_provider.context_length</c> when both are present.
	/// </summary>
	[Fact]
	public async Task DiscoverModelsAsync_WhenBothContextSourcesPresent_PrefersTopLevelContextLength()
	{
		// Arrange: both context sources present with distinct values.
		const string responseBody =
			"""{"data":[{"id":"anthropic/claude","context_length":131072,"top_provider":{"context_length":200000}}]}""";
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		OpenRouterProvider sut = CreateProvider(handler);

		// Act
		IReadOnlyList<DiscoveredModel> models = await sut.DiscoverModelsAsync(
			                                        new BackendContext(BackendName),
			                                        CancellationToken.None);

		// Assert: the top-level context_length (131072) wins over top_provider (200000).
		DiscoveredModel model = Assert.Single(models);
		Assert.Equal(131072, model.ContextLength);
	}

	/// <summary>
	/// Verifies that discovery projects OpenRouter's descriptive metadata — display name, description,
	/// tokenizer, and an upper bound on generated tokens — onto the neutral <see cref="ProviderModelMetadata"/>,
	/// and that the per-single-token pricing is scaled to a per-million-token figure.
	/// </summary>
	[Fact]
	public async Task DiscoverModelsAsync_WhenBackendReportsMetadata_ProjectsNeutralMetadata()
	{
		// Arrange: a listing carrying name/description/tokenizer, a max_completion_tokens, and per-token pricing.
		const string responseBody = """
		                            {"data":[{"id":"anthropic/claude","name":"Claude 3.5","description":"A helpful model.","architecture":{"tokenizer":"Claude"},"top_provider":{"context_length":200000,"max_completion_tokens":8192},"pricing":{"prompt":"0.000003","completion":"0.000015"}}]}
		                            """;
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		OpenRouterProvider sut = CreateProvider(handler);

		// Act
		IReadOnlyList<DiscoveredModel> models = await sut.DiscoverModelsAsync(
			                                        new BackendContext(BackendName),
			                                        CancellationToken.None);

		// Assert: the descriptive fields map across, and per-token prices scale by 1e6 to per-million.
		DiscoveredModel model = Assert.Single(models);
		var metadata = Assert.IsType<ProviderModelMetadata>(model.Metadata);
		Assert.Equal("Claude 3.5", metadata.DisplayName);
		Assert.Equal("A helpful model.", metadata.Description);
		Assert.Equal("Claude", metadata.Tokenizer);
		Assert.Equal(8192, metadata.MaxCompletionTokens);
		Assert.Equal(3m, metadata.PromptUsdPerMillionTokens);
		Assert.Equal(15m, metadata.CompletionUsdPerMillionTokens);
	}

	/// <summary>
	/// Verifies that a listing carrying no descriptive metadata projects a <see langword="null"/>
	/// <see cref="DiscoveredModel.Metadata"/> rather than an empty record, so a metadata-poor entry reads as
	/// "no metadata".
	/// </summary>
	[Fact]
	public async Task DiscoverModelsAsync_WhenBackendReportsNoMetadata_ProjectsNullMetadata()
	{
		// Arrange: a bare entry with only an id and a context window.
		const string responseBody = """{"data":[{"id":"mistral-large","context_length":131072}]}""";
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		OpenRouterProvider sut = CreateProvider(handler);

		// Act
		IReadOnlyList<DiscoveredModel> models = await sut.DiscoverModelsAsync(
			                                        new BackendContext(BackendName),
			                                        CancellationToken.None);

		// Assert
		DiscoveredModel model = Assert.Single(models);
		Assert.Null(model.Metadata);
	}

	/// <summary>
	/// Verifies that a request-level <c>think</c> directive is encoded using OpenRouter's unified
	/// <c>reasoning</c> object (<c>reasoning.effort</c>), not the flat OpenAI <c>reasoning_effort</c> field.
	/// </summary>
	[Fact]
	public async Task CompleteChatAsync_WhenThinkRequested_EncodesNestedReasoningEffort()
	{
		// Arrange: a minimal non-streaming completion so the request payload can be captured.
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, MinimalCompletionBody));
		OpenRouterProvider sut = CreateProvider(handler);

		OllamaChatRequest request = ChatRequest(think: "high");

		// Act
		await sut.CompleteChatAsync(
			new BackendContext(BackendName),
			"anthropic/claude",
			request,
			pinnedEffort: null,
			CancellationToken.None);

		// Assert: the body carries reasoning.effort = "high" and no flat reasoning_effort field.
		JsonObject body = CapturedRequestBody(handler);
		var reasoning = Assert.IsType<JsonObject>(body["reasoning"]);
		Assert.Equal("high", (string?)reasoning["effort"]);
		Assert.False(body.ContainsKey("reasoning_effort"));
	}

	/// <summary>
	/// Verifies that a request-sourced <c>max</c> is clamped down to <c>xhigh</c> in the nested
	/// <c>reasoning.effort</c>, because OpenRouter keeps the base dialect ceiling of
	/// <see cref="ReasoningEffort.XHigh"/>. A live probe (2026) showed OpenRouter's gateway rejects
	/// <c>reasoning.effort = "max"</c> with HTTP 400 for every model — <c>max</c> is outside its global enum
	/// (<c>xhigh, high, medium, low, minimal, none</c>) — so the proxy must lower a non-pinned over-cap level
	/// to the nearest accepted token rather than emit one the gateway rejects as unknown. This is the upper
	/// edge counterpart to <see cref="CompleteChatAsync_WhenThinkRequested_EncodesNestedReasoningEffort"/>,
	/// where an in-enum <c>high</c> rides through unchanged.
	/// </summary>
	[Fact]
	public async Task CompleteChatAsync_WhenThinkIsMax_ClampsToXHigh()
	{
		// Arrange
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, MinimalCompletionBody));
		OpenRouterProvider sut = CreateProvider(handler);

		OllamaChatRequest request = ChatRequest(think: "max");

		// Act
		await sut.CompleteChatAsync(
			new BackendContext(BackendName),
			"anthropic/claude",
			request,
			pinnedEffort: null,
			CancellationToken.None);

		// Assert: max exceeds OpenRouter's dialect ceiling, so the non-pinned level is lowered to xhigh.
		JsonObject body = CapturedRequestBody(handler);
		var reasoning = Assert.IsType<JsonObject>(body["reasoning"]);
		Assert.Equal("xhigh", (string?)reasoning["effort"]);
	}

	/// <summary>
	/// Verifies that a <em>pinned</em> <c>max</c> bypasses the dialect-ceiling clamp and is written verbatim to
	/// <c>reasoning.effort</c>, even overriding a weaker request-level directive. A registry pin is the
	/// operator's authoritative guarantee that the target model accepts the level, so — unlike the
	/// request-sourced path in <see cref="CompleteChatAsync_WhenThinkIsMax_ClampsToXHigh"/> — it is not lowered
	/// to the gateway enum. Against OpenRouter's gateway this verbatim <c>max</c> would draw an HTTP 400
	/// (measured 2026), which is the operator's deliberate trade-off when pinning an over-cap level.
	/// </summary>
	[Fact]
	public async Task CompleteChatAsync_WhenEffortIsPinnedToMax_ForwardsMaxVerbatim()
	{
		// Arrange: the request asks for a low effort, but the operator has pinned max for this model.
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, MinimalCompletionBody));
		OpenRouterProvider sut = CreateProvider(handler);

		OllamaChatRequest request = ChatRequest(think: "low");

		// Act
		await sut.CompleteChatAsync(
			new BackendContext(BackendName),
			"anthropic/claude",
			request,
			pinnedEffort: ReasoningEffort.Max,
			CancellationToken.None);

		// Assert: the pin is authoritative — it bypasses the clamp and overrides the request's "low".
		JsonObject body = CapturedRequestBody(handler);
		var reasoning = Assert.IsType<JsonObject>(body["reasoning"]);
		Assert.Equal("max", (string?)reasoning["effort"]);
	}

	#region Parallel tool calls

	/// <summary>
	/// Verifies that two parallel calls to the <em>same</em> tool, returned in one non-streaming completion,
	/// each surface to the client carrying their own distinct call id when routed through OpenRouter. The id
	/// is the only thing that distinguishes the calls — both are <c>get_weather</c> — so OpenRouter's
	/// specialization (which overrides only reasoning and discovery) must not disturb the inherited
	/// parallel-tool-call correlation the proxy preserves.
	/// </summary>
	[Fact]
	public async Task CompleteChatAsync_WhenBackendReturnsParallelToolCalls_CarriesEachDistinctId()
	{
		// Arrange: one assistant turn calling get_weather twice (Berlin and Hamburg), each with its own id.
		const string responseBody = """
		                            {"id":"c1","model":"anthropic/claude","choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_berlin","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Berlin\"}"}},{"id":"call_hamburg","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Hamburg\"}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":10,"completion_tokens":8,"total_tokens":18}}
		                            """;
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		OpenRouterProvider sut = CreateProvider(handler);
		OllamaChatRequest request = new(
			"client-model",
			[new OllamaChatMessage("user", "Weather in Berlin and Hamburg?")]);

		// Act
		OllamaChatResponse result = await sut.CompleteChatAsync(
			                            new BackendContext(BackendName),
			                            "anthropic/claude",
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
	/// terminal chunk with both ids intact when routed through OpenRouter — the streaming counterpart to the
	/// non-streaming parallel-tool-call guard.
	/// </summary>
	[Fact]
	public async Task StreamChatAsync_WhenBackendStreamsParallelToolCalls_ReassemblesBothIdsOnTerminalChunk()
	{
		// Arrange: index 0 (Berlin) and index 1 (Hamburg) each open with id+name, then stream their
		// arguments; a finish-reason delta and a usage-only event close the stream.
		string sse = string.Join(
			"\n",
			"""data: {"id":"c1","model":"anthropic/claude","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_berlin","type":"function","function":{"name":"get_weather","arguments":""}}]},"finish_reason":null}]}""",
			"""data: {"id":"c1","model":"anthropic/claude","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"city\":\"Berlin\"}"}}]},"finish_reason":null}]}""",
			"""data: {"id":"c1","model":"anthropic/claude","choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"id":"call_hamburg","type":"function","function":{"name":"get_weather","arguments":""}}]},"finish_reason":null}]}""",
			"""data: {"id":"c1","model":"anthropic/claude","choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"function":{"arguments":"{\"city\":\"Hamburg\"}"}}]},"finish_reason":null}]}""",
			"""data: {"id":"c1","model":"anthropic/claude","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
			"""data: {"id":"c1","model":"anthropic/claude","choices":[],"usage":{"prompt_tokens":10,"completion_tokens":8,"total_tokens":18}}""",
			"data: [DONE]",
			string.Empty);
		ScriptedHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
		});
		OpenRouterProvider sut = CreateProvider(handler);
		OllamaChatRequest request = new(
			"client-model",
			[new OllamaChatMessage("user", "Weather in Berlin and Hamburg?")],
			Stream: true);

		// Act
		List<OllamaChatResponse> chunks = [];
		await foreach (OllamaChatResponse chunk in sut.StreamChatAsync(
			               new BackendContext(BackendName),
			               "anthropic/claude",
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
	/// Verifies the inbound leg of the round-trip through OpenRouter: when the client replays a turn with two
	/// parallel tool calls and their two results, the outgoing request preserves each assistant call's id and
	/// stamps each tool result with the matching <c>tool_call_id</c>. OpenRouter inherits the shared tool-id
	/// wiring unchanged (it overrides only reasoning and discovery), so this proves the specialization does not
	/// shadow the correlation that lets the backend tie the 22°C result to the Berlin call and the 9°C result
	/// to the Hamburg call — a correlation the shared tool name alone cannot make.
	/// </summary>
	[Fact]
	public async Task CompleteChatAsync_WhenClientReplaysParallelToolResults_CorrelatesEachResultById()
	{
		// Arrange: a continuation turn — the prior assistant called get_weather twice, and the client now
		// returns both results, each keyed to the call it answers by tool_call_id.
		const string responseBody = """
		                            {"id":"c2","model":"anthropic/claude","choices":[{"index":0,"message":{"role":"assistant","content":"Berlin 22C, Hamburg 9C"},"finish_reason":"stop"}],"usage":{"prompt_tokens":20,"completion_tokens":10,"total_tokens":30}}
		                            """;
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		OpenRouterProvider sut = CreateProvider(handler);

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
			"anthropic/claude",
			request,
			pinnedEffort: null,
			CancellationToken.None);

		// Assert: the wire body preserves the order and, crucially, the id band on both legs.
		JsonObject body = CapturedRequestBody(handler);
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
	}

	#endregion

	/// <summary>A minimal OpenAI chat-completion body sufficient for the response mapper.</summary>
	private const string MinimalCompletionBody =
		"""{"id":"c","created":1,"model":"anthropic/claude","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""";

	/// <summary>Builds a one-message chat request carrying the supplied <c>think</c> directive.</summary>
	private static OllamaChatRequest ChatRequest(string think) => new(
		Model: "claude",
		Messages: [new OllamaChatMessage("user", "hi")],
		Think: JsonValue.Create(think));

	/// <summary>Parses the captured outbound request body as a JSON object.</summary>
	private static JsonObject CapturedRequestBody(ScriptedHandler handler)
	{
		Assert.NotNull(handler.LastRequestBody);
		return Assert.IsType<JsonObject>(JsonNode.Parse(handler.LastRequestBody!));
	}

	/// <summary>Captures the request (and its body) and returns a scripted response.</summary>
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
				LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

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
