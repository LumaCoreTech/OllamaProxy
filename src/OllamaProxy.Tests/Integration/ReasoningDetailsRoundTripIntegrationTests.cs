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
using OllamaProxy.Providers.OpenAi;
using OllamaProxy.Providers.OpenAiProtocol;
using OllamaProxy.Providers.OpenRouter;
using OllamaProxy.Providers.Venice;
using OllamaProxy.Providers.Vllm;

namespace OllamaProxy.Tests.Integration;

/// <summary>
/// Integration tests for the server-side <c>reasoning_details</c> round-trip, exercised end to end against a
/// canned backend so each test observes the exact request body the provider posts on the follow-up turn. The
/// story walks the full loop on a backend that emits the field (Venice and OpenRouter being the real-world
/// emitters): a first response carries an opaque <c>reasoning_details</c> blob on a tool-calling turn, the
/// proxy strips it from the Ollama answer, and a subsequent request that replays the assistant turn's tool
/// calls re-attaches the blob onto the matching upstream message. It then covers the streaming capture path,
/// the graceful omission on a cache miss, the data-driven participation of every dialect — including the
/// strict OpenAI and vLLM dialects — whenever the backend returns the field, the backend-scoped isolation
/// that stops the shared cache from crossing backends, and the disabled-switch escape hatch. The correlation
/// and cache behavior is not measured against a live Claude/Gemini backend; these tests use mocked backends
/// only.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReasoningDetailsRoundTripIntegrationTests
{
	private const string BackendName = "mock";

	// Distinct backend names for the cross-backend isolation test, where one shared cache serves both.
	private const string VeniceBackend     = "venice-backend";
	private const string OpenRouterBackend = "openrouter-backend";

	private const string ReasoningDetailsJson =
		"""[{"type":"reasoning.encrypted","data":"OPAQUE-SIGNATURE"}]""";

	private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
		{ Content = new StringContent(body, Encoding.UTF8, "application/json") };

	private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK)
		{ Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };

	// A non-streaming completion whose tool-calling assistant turn also carries the opaque reasoning_details
	// blob the participating backend expects replayed on the follow-up request.
	private static string ToolCallCompletionWithReasoningDetails() =>
		"""{"id":"c1","model":"m","choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_AAA","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Berlin\"}"}}],"reasoning_details":"""
		+ ReasoningDetailsJson
		+ """},"finish_reason":"tool_calls"}]}""";

	// A simple text completion, used as the canned response for the second (follow-up) request whose body we
	// inspect for the re-attached blob.
	private const string TextCompletion =
		"""{"id":"c2","model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"done"},"finish_reason":"stop"}]}""";

	private static IOptions<ProxyOptions> Options(string providerType, bool cacheEnabled = true) =>
		Microsoft.Extensions.Options.Options.Create(
			new ProxyOptions
			{
				Backends =
				{
					[BackendName] = new BackendOptions
					{
						BaseUrl = "https://mock.test/v1/",
						ProviderType = providerType,
						ApiKey = "test-key-1234"
					}
				},
				ReasoningDetailsCache = new ReasoningDetailsCacheOptions { Enabled = cacheEnabled }
			});

	// Two backends behind one options object, so a single shared cache can serve both in the cross-backend
	// isolation test.
	private static IOptions<ProxyOptions> OptionsTwoBackends() => Microsoft.Extensions.Options.Options.Create(
		new ProxyOptions
		{
			Backends =
			{
				[VeniceBackend] = new BackendOptions
				{
					BaseUrl = "https://mock.test/v1/",
					ProviderType = "venice",
					ApiKey = "test-key-1234"
				},
				[OpenRouterBackend] = new BackendOptions
				{
					BaseUrl = "https://mock.test/v1/",
					ProviderType = "openrouter",
					ApiKey = "test-key-1234"
				}
			},
			ReasoningDetailsCache = new ReasoningDetailsCacheOptions { Enabled = true }
		});

	// The assistant turn the client replays on the follow-up request: it carries the same tool call the
	// backend emitted (name + arguments) under which the blob was cached, plus the tool result.
	private static OllamaChatRequest FollowUpRequest() => new(
		"client-model",
		[
			new OllamaChatMessage("user", "weather in Berlin?"),
			new OllamaChatMessage(
				"assistant",
				string.Empty,
				ToolCalls:
				[
					new OllamaToolCall(
						new OllamaToolCallFunction(
							"get_weather",
							Description: null,
							new JsonObject { ["city"] = "Berlin" }),
						Id: "call_AAA")
				]),
			new OllamaChatMessage("tool", "18C", ToolName: "get_weather", ToolCallId: "call_AAA")
		]);

	private static OllamaChatRequest FirstRequest() => new(
		"client-model",
		[new OllamaChatMessage("user", "weather in Berlin?")]);

	// --- Venice: non-streaming capture then re-attach ---

	/// <summary>
	/// Verifies the full Venice round-trip: the first response's opaque <c>reasoning_details</c> is captured
	/// and stripped from the Ollama answer, and the follow-up request that replays the assistant turn's tool
	/// calls carries the blob re-attached onto the matching upstream assistant message.
	/// </summary>
	[Fact]
	public async Task Venice_NonStreaming_CapturesAndReattachesReasoningDetails()
	{
		// Arrange: first call returns the tool-calling turn with the blob; second returns plain text.
		Queue<HttpResponseMessage> responses = new();
		responses.Enqueue(Json(ToolCallCompletionWithReasoningDetails()));
		responses.Enqueue(Json(TextCompletion));
		ScriptedHandler handler = new(_ => responses.Dequeue());
		VeniceProvider sut = CreateVenice(handler);

		// Act: first turn captures; the Ollama answer must not leak the blob.
		OllamaChatResponse first = await sut.CompleteChatAsync(
			                           new BackendContext(BackendName),
			                           "m",
			                           FirstRequest(),
			                           pinnedEffort: null,
			                           CancellationToken.None);

		// Follow-up turn replays the tool calls and must re-attach the blob upstream.
		await sut.CompleteChatAsync(
			new BackendContext(BackendName),
			"m",
			FollowUpRequest(),
			pinnedEffort: null,
			CancellationToken.None);

		// Assert: the client-facing response carried the tool call (it is a tool-calling turn) but the proxy
		// holds the blob server-side, and the upstream follow-up body re-attached it onto the assistant turn
		// (index 1 in the replayed message list).
		Assert.NotNull(first.Message.ToolCalls);
		Assert.Equal("get_weather", first.Message.ToolCalls![0].Function.Name);

		var followUpBody = (JsonNode.Parse(handler.LastRequestBody!) as JsonObject)!;
		var messages = (followUpBody["messages"] as JsonArray)!;
		JsonNode? reattached = messages[1]!["reasoning_details"];

		Assert.NotNull(reattached);
		Assert.Equal(
			JsonNode.Parse(ReasoningDetailsJson)!.ToJsonString(),
			reattached.ToJsonString());
	}

	/// <summary>
	/// Verifies that the captured blob is omitted from the Ollama response message entirely (the proxy holds
	/// it server-side and never exposes it to the client).
	/// </summary>
	[Fact]
	public async Task Venice_NonStreaming_DoesNotExposeBlobToClient()
	{
		// Arrange
		ScriptedHandler handler = new(_ => Json(ToolCallCompletionWithReasoningDetails()));
		VeniceProvider sut = CreateVenice(handler);

		// Act
		OllamaChatResponse response = await sut.CompleteChatAsync(
			                              new BackendContext(BackendName),
			                              "m",
			                              FirstRequest(),
			                              pinnedEffort: null,
			                              CancellationToken.None);

		// Assert: the Ollama message carries the tool call but no reasoning-details payload of any kind. The
		// only reasoning surface the Ollama message has is Thinking, which a reasoning_details blob must not
		// populate.
		Assert.NotNull(response.Message.ToolCalls);
		Assert.Null(response.Message.Thinking);
	}

	// --- OpenRouter: streaming capture then re-attach ---

	/// <summary>
	/// Verifies the streaming round-trip on OpenRouter: a streamed tool-calling turn whose terminal delta
	/// carries <c>reasoning_details</c> is captured, and the follow-up request re-attaches the blob.
	/// </summary>
	[Fact]
	public async Task OpenRouter_Streaming_CapturesAndReattachesReasoningDetails()
	{
		// Arrange: an SSE stream whose terminal delta carries both the tool call and the reasoning_details
		// blob, followed by a usage-only event and the sentinel; the second call returns plain text.
		string toolCallEvent =
			"""data: {"id":"c1","model":"m","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_AAA","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Berlin\"}"}}],"reasoning_details":"""
			+ ReasoningDetailsJson
			+ """},"finish_reason":"tool_calls"}]}""";

		string sse = string.Join(
			"\n",
			"""data: {"id":"c1","model":"m","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null}]}""",
			toolCallEvent,
			"""data: {"id":"c1","model":"m","choices":[],"usage":{"prompt_tokens":4,"completion_tokens":6,"total_tokens":10}}""",
			"data: [DONE]",
			string.Empty);

		Queue<HttpResponseMessage> responses = new();
		responses.Enqueue(Sse(sse));
		responses.Enqueue(Json(TextCompletion));
		ScriptedHandler handler = new(_ => responses.Dequeue());
		OpenRouterProvider sut = CreateOpenRouter(handler);

		// Act: drain the stream (capture happens in the finally), then issue the follow-up.
		OllamaChatRequest streamRequest = FirstRequest() with { Stream = true };
		await foreach (OllamaChatResponse _ in sut.StreamChatAsync(
			               new BackendContext(BackendName),
			               "m",
			               streamRequest,
			               pinnedEffort: null,
			               CancellationToken.None)) { }

		await sut.CompleteChatAsync(
			new BackendContext(BackendName),
			"m",
			FollowUpRequest(),
			pinnedEffort: null,
			CancellationToken.None);

		// Assert: the follow-up upstream body re-attached the streamed turn's blob onto the assistant message.
		var followUpBody = (JsonNode.Parse(handler.LastRequestBody!) as JsonObject)!;
		var messages = (followUpBody["messages"] as JsonArray)!;
		JsonNode? reattached = messages[1]!["reasoning_details"];

		Assert.NotNull(reattached);
		Assert.Equal(
			JsonNode.Parse(ReasoningDetailsJson)!.ToJsonString(),
			reattached.ToJsonString());
	}

	// --- Graceful miss and data-driven participation across dialects ---

	/// <summary>
	/// Verifies that when nothing was cached for the replayed turn (the common cold-start case), the
	/// follow-up request simply omits <c>reasoning_details</c> rather than failing.
	/// </summary>
	[Fact]
	public async Task Venice_FollowUpWithoutCapture_OmitsReasoningDetails()
	{
		// Arrange: no prior capture; the follow-up is the first request the provider sees.
		ScriptedHandler handler = new(_ => Json(TextCompletion));
		VeniceProvider sut = CreateVenice(handler);

		// Act
		await sut.CompleteChatAsync(
			new BackendContext(BackendName),
			"m",
			FollowUpRequest(),
			pinnedEffort: null,
			CancellationToken.None);

		// Assert
		var body = (JsonNode.Parse(handler.LastRequestBody!) as JsonObject)!;
		var messages = (body["messages"] as JsonArray)!;
		Assert.Null(messages[1]!["reasoning_details"]);
	}

	/// <summary>
	/// Verifies that the round-trip is data-driven, not dialect-gated: even the strict OpenAI dialect captures
	/// and re-attaches <c>reasoning_details</c> when the backend actually returns it, rather than discarding a
	/// field it could correctly preserve.
	/// </summary>
	[Fact]
	public async Task OpenAi_WhenBackendEmitsBlob_RoundTripsReasoningDetails()
	{
		// Arrange
		Queue<HttpResponseMessage> responses = new();
		responses.Enqueue(Json(ToolCallCompletionWithReasoningDetails()));
		responses.Enqueue(Json(TextCompletion));
		ScriptedHandler handler = new(_ => responses.Dequeue());
		OpenAiProvider sut = CreateOpenAi(handler);

		// Act
		await sut.CompleteChatAsync(
			new BackendContext(BackendName),
			"m",
			FirstRequest(),
			pinnedEffort: null,
			CancellationToken.None);
		await sut.CompleteChatAsync(
			new BackendContext(BackendName),
			"m",
			FollowUpRequest(),
			pinnedEffort: null,
			CancellationToken.None);

		// Assert: the follow-up body re-attached the captured blob.
		var body = (JsonNode.Parse(handler.LastRequestBody!) as JsonObject)!;
		var messages = (body["messages"] as JsonArray)!;
		JsonNode? reattached = messages[1]!["reasoning_details"];

		Assert.NotNull(reattached);
		Assert.Equal(
			JsonNode.Parse(ReasoningDetailsJson)!.ToJsonString(),
			reattached.ToJsonString());
	}

	/// <summary>
	/// Verifies that vLLM, too, round-trips the blob when its backend returns one — participation follows the
	/// data, not the dialect.
	/// </summary>
	[Fact]
	public async Task Vllm_WhenBackendEmitsBlob_RoundTripsReasoningDetails()
	{
		// Arrange
		Queue<HttpResponseMessage> responses = new();
		responses.Enqueue(Json(ToolCallCompletionWithReasoningDetails()));
		responses.Enqueue(Json(TextCompletion));
		ScriptedHandler handler = new(_ => responses.Dequeue());
		VllmProvider sut = CreateVllm(handler);

		// Act
		await sut.CompleteChatAsync(
			new BackendContext(BackendName),
			"m",
			FirstRequest(),
			pinnedEffort: null,
			CancellationToken.None);
		await sut.CompleteChatAsync(
			new BackendContext(BackendName),
			"m",
			FollowUpRequest(),
			pinnedEffort: null,
			CancellationToken.None);

		// Assert
		var body = (JsonNode.Parse(handler.LastRequestBody!) as JsonObject)!;
		var messages = (body["messages"] as JsonArray)!;
		JsonNode? reattached = messages[1]!["reasoning_details"];

		Assert.NotNull(reattached);
		Assert.Equal(
			JsonNode.Parse(ReasoningDetailsJson)!.ToJsonString(),
			reattached.ToJsonString());
	}

	/// <summary>
	/// Verifies that the disabled-cache switch fully suppresses the round-trip on an otherwise participating
	/// provider: a captured blob is not retained, so the follow-up request omits it.
	/// </summary>
	[Fact]
	public async Task Venice_WithCacheDisabled_OmitsReasoningDetails()
	{
		// Arrange: Venice participates, but the cache is switched off.
		Queue<HttpResponseMessage> responses = new();
		responses.Enqueue(Json(ToolCallCompletionWithReasoningDetails()));
		responses.Enqueue(Json(TextCompletion));
		ScriptedHandler handler = new(_ => responses.Dequeue());
		VeniceProvider sut = CreateVenice(handler, cacheEnabled: false);

		// Act
		await sut.CompleteChatAsync(
			new BackendContext(BackendName),
			"m",
			FirstRequest(),
			pinnedEffort: null,
			CancellationToken.None);
		await sut.CompleteChatAsync(
			new BackendContext(BackendName),
			"m",
			FollowUpRequest(),
			pinnedEffort: null,
			CancellationToken.None);

		// Assert
		var body = (JsonNode.Parse(handler.LastRequestBody!) as JsonObject)!;
		var messages = (body["messages"] as JsonArray)!;
		Assert.Null(messages[1]!["reasoning_details"]);
	}

	// --- Cross-backend isolation: one shared cache must not leak a blob between backends ---

	/// <summary>
	/// Verifies the backend-scoped key in action: a blob captured from the Venice backend is not re-attached
	/// when the very same tool call is replayed against the OpenRouter backend, even though both providers
	/// share one process-wide cache. This is the concrete protection against the cross-backend bleed that a
	/// content-only key would allow when Venice and OpenRouter run side by side.
	/// </summary>
	[Fact]
	public async Task SharedCache_DoesNotLeakBlobAcrossBackends()
	{
		// Arrange: one cache and one options object shared by both providers; Venice returns the blob, then
		// OpenRouter answers the follow-up. Each provider has its own handler so we can read OpenRouter's body.
		IOptions<ProxyOptions> options = OptionsTwoBackends();
		ReasoningDetailsCache sharedCache = new(options, TimeProvider.System);

		ScriptedHandler veniceHandler = new(_ => Json(ToolCallCompletionWithReasoningDetails()));
		ScriptedHandler openRouterHandler = new(_ => Json(TextCompletion));

		VeniceProvider venice = new(
			new StubHttpClientProvider(veniceHandler),
			new StubCapabilityProber(),
			TimeProvider.System,
			options,
			new RequestTraceAccessor(),
			sharedCache,
			NullLogger<VeniceProvider>.Instance);

		OpenRouterProvider openRouter = new(
			new StubHttpClientProvider(openRouterHandler),
			new StubCapabilityProber(),
			TimeProvider.System,
			options,
			new RequestTraceAccessor(),
			sharedCache,
			NullLogger<OpenRouterProvider>.Instance);

		// Act: capture on the Venice backend, then replay the same tool call on the OpenRouter backend.
		await venice.CompleteChatAsync(
			new BackendContext(VeniceBackend),
			"m",
			FirstRequest(),
			pinnedEffort: null,
			CancellationToken.None);

		await openRouter.CompleteChatAsync(
			new BackendContext(OpenRouterBackend),
			"m",
			FollowUpRequest(),
			pinnedEffort: null,
			CancellationToken.None);

		// Assert: the OpenRouter follow-up must NOT carry Venice's blob — the backend-scoped key missed.
		var body = (JsonNode.Parse(openRouterHandler.LastRequestBody!) as JsonObject)!;
		var messages = (body["messages"] as JsonArray)!;
		Assert.Null(messages[1]!["reasoning_details"]);
	}

	// --- Provider factories: each shares the same cache across the two turns of a test ---

	private static VeniceProvider CreateVenice(ScriptedHandler handler, bool cacheEnabled = true)
	{
		IOptions<ProxyOptions> options = Options("venice", cacheEnabled);
		return new VeniceProvider(
			new StubHttpClientProvider(handler),
			new StubCapabilityProber(),
			TimeProvider.System,
			options,
			new RequestTraceAccessor(),
			new ReasoningDetailsCache(options, TimeProvider.System),
			NullLogger<VeniceProvider>.Instance);
	}

	private static OpenRouterProvider CreateOpenRouter(ScriptedHandler handler)
	{
		IOptions<ProxyOptions> options = Options("openrouter");
		return new OpenRouterProvider(
			new StubHttpClientProvider(handler),
			new StubCapabilityProber(),
			TimeProvider.System,
			options,
			new RequestTraceAccessor(),
			new ReasoningDetailsCache(options, TimeProvider.System),
			NullLogger<OpenRouterProvider>.Instance);
	}

	private static OpenAiProvider CreateOpenAi(ScriptedHandler handler)
	{
		IOptions<ProxyOptions> options = Options("openai");
		return new OpenAiProvider(
			new StubHttpClientProvider(handler),
			new StubCapabilityProber(),
			TimeProvider.System,
			options,
			new RequestTraceAccessor(),
			new ReasoningDetailsCache(options, TimeProvider.System),
			NullLogger<OpenAiProvider>.Instance);
	}

	private static VllmProvider CreateVllm(ScriptedHandler handler)
	{
		IOptions<ProxyOptions> options = Options("vllm");
		return new VllmProvider(
			new StubHttpClientProvider(handler),
			new StubCapabilityProber(),
			TimeProvider.System,
			options,
			new RequestTraceAccessor(),
			new ReasoningDetailsCache(options, TimeProvider.System),
			NullLogger<VllmProvider>.Instance);
	}

	/// <summary>Captures the last request body and returns the scripted response for each call.</summary>
	private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
	{
		public string? LastRequestBody { get; private set; }

		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken  cancellationToken)
		{
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

	/// <summary>A capability prober stub that stays inconclusive; discovery is not under test here.</summary>
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
