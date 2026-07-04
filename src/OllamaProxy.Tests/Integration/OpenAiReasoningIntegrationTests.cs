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
/// Integration tests for reasoning-effort mapping across the OpenAI-compatible provider hierarchy,
/// exercised end to end against a canned backend so each test observes the exact request body posted to
/// <c>chat/completions</c>. The tests walk the four dialects: the generic <see cref="OpenAiProvider"/>
/// and its standard flat <c>reasoning_effort</c> field, Venice's <c>venice_parameters.disable_thinking</c>
/// off switch, vLLM's dual <c>reasoning_effort</c> + <c>chat_template_kwargs.enable_thinking</c> emission,
/// and OpenRouter's unified nested <c>reasoning.effort</c> object. Further groups cover Venice's forced
/// <c>include_venice_system_prompt = false</c> vendor suppression, the backend-default and "unspecified
/// means nothing" rules, authoritative per-model pinned efforts, and the per-provider dialect clamping
/// (with the pin-verbatim exemption).
/// </summary>
[Trait("Category", "Integration")]
public sealed class OpenAiReasoningIntegrationTests
{
	private const string BackendName = "mock";

	private const string CompletionBody = """
	                                      {"id":"c1","model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}
	                                      """;

	private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
		{ Content = new StringContent(body, Encoding.UTF8, "application/json") };

	private static IOptions<ProxyOptions> Options(ReasoningEffort? backendDefault, string providerType) =>
		Microsoft.Extensions.Options.Options.Create(
			new ProxyOptions
			{
				Backends =
				{
					[BackendName] = new BackendOptions
					{
						BaseUrl = "https://mock.test/v1/",
						ProviderType = providerType,
						ApiKey = "test-key-1234",
						ReasoningEffort = backendDefault
					}
				}
			});

	private static OllamaChatRequest Request(JsonNode? think) => new(
		"client-model",
		[new OllamaChatMessage("user", "hi")],
		Think: think);

	private static async Task<JsonObject> PostAndCaptureAsync(
		OpenAiCompatibleProvider provider,
		ScriptedHandler          handler,
		OllamaChatRequest        request)
	{
		await provider.CompleteChatAsync(
			new BackendContext(BackendName),
			"m",
			request,
			pinnedEffort: null,
			CancellationToken.None);

		// The captured body is what the provider actually serialized onto the wire — the reasoning dialect.
		return (JsonNode.Parse(handler.LastRequestBody!) as JsonObject)!;
	}

	// --- 1. Generic OpenAI: flat reasoning_effort ---

	/// <summary>
	/// Verifies that the generic <see cref="OpenAiProvider"/> writes the resolved effort to the
	/// standard flat <c>reasoning_effort</c> field.
	/// </summary>
	[Fact]
	public async Task CompleteChatAsync_OpenAiProviderWithThink_WritesFlatReasoningEffort()
	{
		// Arrange
		ScriptedHandler handler = new(_ => Json(CompletionBody));
		OpenAiProvider sut = new(
			new StubHttpClientProvider(handler),
			new StubCapabilityProber(),
			TimeProvider.System,
			Options(backendDefault: null, "openai"),
			new RequestTraceAccessor(),
			TestReasoningDetailsCache.CreateDefault(),
			NullLogger<OpenAiProvider>.Instance);

		// Act
		JsonObject body = await PostAndCaptureAsync(sut, handler, Request(JsonValue.Create("high")));

		// Assert
		Assert.Equal("high", body["reasoning_effort"]!.GetValue<string>());
	}

	// --- 2. Venice: disable_thinking off switch, reasoning_effort for positive levels ---

	/// <summary>
	/// Verifies that <see cref="VeniceProvider"/> turns reasoning off through its
	/// <c>venice_parameters.disable_thinking</c> flag rather than a <c>reasoning_effort</c> of none.
	/// </summary>
	[Fact]
	public async Task CompleteChatAsync_VeniceProviderWithThinkFalse_WritesDisableThinking()
	{
		// Arrange
		ScriptedHandler handler = new(_ => Json(CompletionBody));
		VeniceProvider sut = new(
			new StubHttpClientProvider(handler),
			new StubCapabilityProber(),
			TimeProvider.System,
			Options(backendDefault: null, "venice"),
			new RequestTraceAccessor(),
			TestReasoningDetailsCache.CreateDefault(),
			NullLogger<VeniceProvider>.Instance);

		// Act: think:false resolves to None, Venice's documented off switch.
		JsonObject body = await PostAndCaptureAsync(sut, handler, Request(JsonValue.Create(false)));

		// Assert: the vendor extension carries the off flag and no flat reasoning_effort is emitted.
		Assert.True(body["venice_parameters"]!["disable_thinking"]!.GetValue<bool>());
		Assert.False(body.ContainsKey("reasoning_effort"));
	}

	/// <summary>
	/// Verifies that <see cref="VeniceProvider"/> forwards a positive effort — including its extended
	/// <c>max</c> level — as the standard flat <c>reasoning_effort</c> token, alongside the forced
	/// <c>venice_parameters.include_venice_system_prompt = false</c>.
	/// </summary>
	[Fact]
	public async Task CompleteChatAsync_VeniceProviderWithMax_WritesFlatReasoningEffort()
	{
		// Arrange
		ScriptedHandler handler = new(_ => Json(CompletionBody));
		VeniceProvider sut = new(
			new StubHttpClientProvider(handler),
			new StubCapabilityProber(),
			TimeProvider.System,
			Options(backendDefault: null, "venice"),
			new RequestTraceAccessor(),
			TestReasoningDetailsCache.CreateDefault(),
			NullLogger<VeniceProvider>.Instance);

		// Act
		JsonObject body = await PostAndCaptureAsync(sut, handler, Request(JsonValue.Create("max")));

		// Assert: max is within Venice's dialect ceiling, so it is forwarded verbatim; the forced vendor
		// flag is emitted alongside.
		Assert.Equal("max", body["reasoning_effort"]!.GetValue<string>());
		var veniceParameters = Assert.IsType<JsonObject>(body["venice_parameters"]);
		Assert.False(veniceParameters["include_venice_system_prompt"]!.GetValue<bool>());
	}

	// --- 2b. Venice: vendor system-prompt suppression (authoritative) ---

	/// <summary>
	/// Verifies that <see cref="VeniceProvider"/> writes <c>venice_parameters.include_venice_system_prompt = false</c>
	/// on every chat request, even when the client does not set it.
	/// </summary>
	[Fact]
	public async Task CompleteChatAsync_VeniceProvider_WritesIncludeVeniceSystemPromptFalse()
	{
		// Arrange
		ScriptedHandler handler = new(_ => Json(CompletionBody));
		VeniceProvider sut = new(
			new StubHttpClientProvider(handler),
			new StubCapabilityProber(),
			TimeProvider.System,
			Options(backendDefault: null, "venice"),
			new RequestTraceAccessor(),
			TestReasoningDetailsCache.CreateDefault(),
			NullLogger<VeniceProvider>.Instance);

		// Act: a plain request, no client-side vendor_parameters.
		JsonObject body = await PostAndCaptureAsync(sut, handler, Request(think: null));

		// Assert: the venice_parameters container carries the forced false.
		var veniceParameters = Assert.IsType<JsonObject>(body["venice_parameters"]);
		Assert.False(veniceParameters["include_venice_system_prompt"]!.GetValue<bool>());
	}

	/// <summary>
	/// Verifies that <see cref="VeniceProvider"/> overwrites a client-supplied <c>include_venice_system_prompt</c>
	/// with <see langword="false"/>, because the choice belongs to the operator who configured the
	/// backend, not to a client.
	/// </summary>
	[Fact]
	public async Task CompleteChatAsync_VeniceProvider_OverwritesClientIncludeVeniceSystemPrompt()
	{
		// Arrange: a client that asks Venice to inject its vendor system prompt.
		ScriptedHandler handler = new(_ => Json(CompletionBody));
		VeniceProvider sut = new(
			new StubHttpClientProvider(handler),
			new StubCapabilityProber(),
			TimeProvider.System,
			Options(backendDefault: null, "venice"),
			new RequestTraceAccessor(),
			TestReasoningDetailsCache.CreateDefault(),
			NullLogger<VeniceProvider>.Instance);

		OllamaChatRequest request = new("client-model", [new OllamaChatMessage("user", "hi")]);
		await sut.CompleteChatAsync(
			new BackendContext(BackendName),
			"m",
			request,
			pinnedEffort: null,
			CancellationToken.None);

		// Assert: the client's true has been overwritten to false.
		var body = (JsonNode.Parse(handler.LastRequestBody!) as JsonObject)!;
		var veniceParameters = Assert.IsType<JsonObject>(body["venice_parameters"]);
		Assert.False(veniceParameters["include_venice_system_prompt"]!.GetValue<bool>());
	}

	// --- 3. vLLM: dual reasoning_effort + chat_template_kwargs.enable_thinking ---

	/// <summary>
	/// Verifies that <see cref="VllmProvider"/> writes both the portable <c>reasoning_effort</c> token
	/// and the explicit <c>chat_template_kwargs.enable_thinking</c> flag for a positive effort.
	/// </summary>
	[Fact]
	public async Task CompleteChatAsync_VllmProviderWithThink_WritesEffortAndEnableThinkingTrue()
	{
		// Arrange
		ScriptedHandler handler = new(_ => Json(CompletionBody));
		VllmProvider sut = new(
			new StubHttpClientProvider(handler),
			new StubCapabilityProber(),
			TimeProvider.System,
			Options(backendDefault: null, "vllm"),
			new RequestTraceAccessor(),
			TestReasoningDetailsCache.CreateDefault(),
			NullLogger<VllmProvider>.Instance);

		// Act
		JsonObject body = await PostAndCaptureAsync(sut, handler, Request(JsonValue.Create("low")));

		// Assert: modern servers read reasoning_effort, older ones read the explicit kwarg.
		Assert.Equal("low", body["reasoning_effort"]!.GetValue<string>());
		Assert.True(body["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
	}

	/// <summary>
	/// Verifies that <see cref="VllmProvider"/> sets <c>enable_thinking</c> to <see langword="false"/>
	/// when reasoning is turned off, while still emitting the <c>reasoning_effort: none</c> token.
	/// </summary>
	[Fact]
	public async Task CompleteChatAsync_VllmProviderWithThinkFalse_WritesEnableThinkingFalse()
	{
		// Arrange
		ScriptedHandler handler = new(_ => Json(CompletionBody));
		VllmProvider sut = new(
			new StubHttpClientProvider(handler),
			new StubCapabilityProber(),
			TimeProvider.System,
			Options(backendDefault: null, "vllm"),
			new RequestTraceAccessor(),
			TestReasoningDetailsCache.CreateDefault(),
			NullLogger<VllmProvider>.Instance);

		// Act
		JsonObject body = await PostAndCaptureAsync(sut, handler, Request(JsonValue.Create(false)));

		// Assert
		Assert.Equal("none", body["reasoning_effort"]!.GetValue<string>());
		Assert.False(body["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
	}

	// --- 4. OpenRouter: nested reasoning.effort (recommended), full neutral vocabulary ---

	/// <summary>
	/// Verifies that <see cref="OpenRouterProvider"/> writes the full neutral vocabulary into its unified
	/// nested <c>reasoning.effort</c> object — OpenRouter accepts the OpenAI-style set (<c>minimal</c>
	/// through <c>xhigh</c>) unchanged there. OpenRouter would also accept the flat <c>reasoning_effort</c>
	/// field, but this adapter writes the recommended nested form by choice, so the flat field is not emitted.
	/// </summary>
	/// <param name="level">The inbound <c>think</c> level.</param>
	/// <param name="expected">The expected OpenRouter effort token (identical to the input).</param>
	[Theory]
	[InlineData("minimal", "minimal")]
	[InlineData("low", "low")]
	[InlineData("medium", "medium")]
	[InlineData("high", "high")]
	[InlineData("xhigh", "xhigh")]
	public async Task CompleteChatAsync_OpenRouterProviderWithThink_WritesNestedReasoningEffort(
		string level,
		string expected)
	{
		// Arrange
		ScriptedHandler handler = new(_ => Json(CompletionBody));
		OpenRouterProvider sut = new(
			new StubHttpClientProvider(handler),
			new StubCapabilityProber(),
			TimeProvider.System,
			Options(backendDefault: null, "openrouter"),
			new RequestTraceAccessor(),
			TestReasoningDetailsCache.CreateDefault(),
			NullLogger<OpenRouterProvider>.Instance);

		// Act
		JsonObject body = await PostAndCaptureAsync(sut, handler, Request(JsonValue.Create(level)));

		// Assert: the nested reasoning.effort carries the level; the flat field is not emitted.
		Assert.Equal(expected, body["reasoning"]!["effort"]!.GetValue<string>());
		Assert.False(body.ContainsKey("reasoning_effort"));
	}

	/// <summary>
	/// Verifies that <see cref="OpenRouterProvider"/> turns reasoning off through the nested
	/// <c>reasoning.effort: none</c> token, which OpenRouter supports alongside the positive levels.
	/// </summary>
	[Fact]
	public async Task CompleteChatAsync_OpenRouterProviderWithThinkFalse_WritesReasoningEffortNone()
	{
		// Arrange
		ScriptedHandler handler = new(_ => Json(CompletionBody));
		OpenRouterProvider sut = new(
			new StubHttpClientProvider(handler),
			new StubCapabilityProber(),
			TimeProvider.System,
			Options(backendDefault: null, "openrouter"),
			new RequestTraceAccessor(),
			TestReasoningDetailsCache.CreateDefault(),
			NullLogger<OpenRouterProvider>.Instance);

		// Act
		JsonObject body = await PostAndCaptureAsync(sut, handler, Request(JsonValue.Create(false)));

		// Assert
		Assert.Equal("none", body["reasoning"]!["effort"]!.GetValue<string>());
		Assert.False(body.ContainsKey("reasoning_effort"));
	}

	// --- 5. Backend default and the unspecified rule ---

	/// <summary>
	/// Verifies that the backend's configured default reasoning effort is applied when the request
	/// carries no <c>think</c> directive.
	/// </summary>
	[Fact]
	public async Task CompleteChatAsync_WhenRequestHasNoThink_AppliesBackendDefault()
	{
		// Arrange
		ScriptedHandler handler = new(_ => Json(CompletionBody));
		OpenAiProvider sut = new(
			new StubHttpClientProvider(handler),
			new StubCapabilityProber(),
			TimeProvider.System,
			Options(ReasoningEffort.High, "openai"),
			new RequestTraceAccessor(),
			TestReasoningDetailsCache.CreateDefault(),
			NullLogger<OpenAiProvider>.Instance);

		// Act
		JsonObject body = await PostAndCaptureAsync(sut, handler, Request(think: null));

		// Assert
		Assert.Equal("high", body["reasoning_effort"]!.GetValue<string>());
	}

	/// <summary>
	/// Verifies the shared "unspecified means nothing" rule: with neither a request directive nor a
	/// backend default, no reasoning field of any dialect is emitted.
	/// </summary>
	[Fact]
	public async Task CompleteChatAsync_WhenNoThinkAndNoDefault_OmitsAllReasoningFields()
	{
		// Arrange
		ScriptedHandler handler = new(_ => Json(CompletionBody));
		OpenAiProvider sut = new(
			new StubHttpClientProvider(handler),
			new StubCapabilityProber(),
			TimeProvider.System,
			Options(backendDefault: null, "openai"),
			new RequestTraceAccessor(),
			TestReasoningDetailsCache.CreateDefault(),
			NullLogger<OpenAiProvider>.Instance);

		// Act
		JsonObject body = await PostAndCaptureAsync(sut, handler, Request(think: null));

		// Assert: none of the dialect-specific reasoning keys are present.
		Assert.False(body.ContainsKey("reasoning_effort"));
		Assert.False(body.ContainsKey("reasoning"));
		Assert.False(body.ContainsKey("venice_parameters"));
		Assert.False(body.ContainsKey("chat_template_kwargs"));
	}

	// --- 6. Pinned per-model reasoning effort: authoritative over think and the backend default ---

	/// <summary>
	/// Verifies that a pinned per-model effort overrides the client's <c>think</c> directive: even when the
	/// client asks for <c>low</c>, the pinned <c>high</c> is what reaches the wire.
	/// </summary>
	[Fact]
	public async Task CompleteChatAsync_WhenPinnedEffortSet_OverridesClientThink()
	{
		// Arrange: the client asks for low, but the model is pinned to high.
		ScriptedHandler handler = new(_ => Json(CompletionBody));
		OpenAiProvider sut = new(
			new StubHttpClientProvider(handler),
			new StubCapabilityProber(),
			TimeProvider.System,
			Options(backendDefault: null, "openai"),
			new RequestTraceAccessor(),
			TestReasoningDetailsCache.CreateDefault(),
			NullLogger<OpenAiProvider>.Instance);

		// Act
		JsonObject body = await PostWithPinAsync(
			                  sut,
			                  handler,
			                  Request(JsonValue.Create("low")),
			                  ReasoningEffort.High);

		// Assert: the pinned level wins over the client's think directive.
		Assert.Equal("high", body["reasoning_effort"]!.GetValue<string>());
	}

	/// <summary>
	/// Verifies that a pinned per-model effort overrides the backend default when the request carries no
	/// <c>think</c> directive.
	/// </summary>
	[Fact]
	public async Task CompleteChatAsync_WhenPinnedEffortSet_OverridesBackendDefault()
	{
		// Arrange: the backend defaults to low and the client sends no think; the pin is high.
		ScriptedHandler handler = new(_ => Json(CompletionBody));
		OpenAiProvider sut = new(
			new StubHttpClientProvider(handler),
			new StubCapabilityProber(),
			TimeProvider.System,
			Options(backendDefault: ReasoningEffort.Low, "openai"),
			new RequestTraceAccessor(),
			TestReasoningDetailsCache.CreateDefault(),
			NullLogger<OpenAiProvider>.Instance);

		// Act
		JsonObject body = await PostWithPinAsync(sut, handler, Request(think: null), ReasoningEffort.High);

		// Assert: the pin wins over the backend default.
		Assert.Equal("high", body["reasoning_effort"]!.GetValue<string>());
	}

	/// <summary>
	/// Verifies that pinning <see cref="ReasoningEffort.None"/> turns reasoning hard off for the model, even
	/// when the client asks to think — on Venice this is the documented <c>disable_thinking</c> off switch.
	/// </summary>
	[Fact]
	public async Task CompleteChatAsync_WhenPinnedEffortNoneOnVenice_DisablesThinkingDespiteClientThink()
	{
		// Arrange: the client asks for high, but the model pins None (hard off).
		ScriptedHandler handler = new(_ => Json(CompletionBody));
		VeniceProvider sut = new(
			new StubHttpClientProvider(handler),
			new StubCapabilityProber(),
			TimeProvider.System,
			Options(backendDefault: null, "venice"),
			new RequestTraceAccessor(),
			TestReasoningDetailsCache.CreateDefault(),
			NullLogger<VeniceProvider>.Instance);

		// Act
		JsonObject body = await PostWithPinAsync(
			                  sut,
			                  handler,
			                  Request(JsonValue.Create("high")),
			                  ReasoningEffort.None);

		// Assert: the pinned None becomes Venice's off switch; no positive reasoning_effort is emitted.
		Assert.True(body["venice_parameters"]!["disable_thinking"]!.GetValue<bool>());
		Assert.False(body.ContainsKey("reasoning_effort"));
	}

	// --- 7. Per-provider dialect clamping (non-pinned) and pin-verbatim exemption ---

	/// <summary>
	/// Verifies that a non-pinned <c>max</c> request to the generic OpenAI dialect is clamped down to its
	/// <c>xhigh</c> ceiling, since the OpenAI API has no <c>max</c> token.
	/// </summary>
	[Fact]
	public async Task CompleteChatAsync_OpenAiProviderWithThinkMax_ClampsToXHigh()
	{
		// Arrange
		ScriptedHandler handler = new(_ => Json(CompletionBody));
		OpenAiProvider sut = new(
			new StubHttpClientProvider(handler),
			new StubCapabilityProber(),
			TimeProvider.System,
			Options(backendDefault: null, "openai"),
			new RequestTraceAccessor(),
			TestReasoningDetailsCache.CreateDefault(),
			NullLogger<OpenAiProvider>.Instance);

		// Act: the client asks for max, which the OpenAI dialect does not define.
		JsonObject body = await PostAndCaptureAsync(sut, handler, Request(JsonValue.Create("max")));

		// Assert: clamped to the dialect ceiling.
		Assert.Equal("xhigh", body["reasoning_effort"]!.GetValue<string>());
	}

	/// <summary>
	/// Verifies that a non-pinned <c>max</c> backend default on an OpenRouter backend is clamped down to
	/// <c>xhigh</c>, the inherited dialect ceiling. A live probe (2026, against <c>openai/gpt-5.2</c> and
	/// <c>anthropic/claude-opus-4.8</c>) showed OpenRouter's gateway rejects <c>reasoning.effort = "max"</c>
	/// with HTTP 400 for every model — <c>max</c> is not in its global enum — while <c>xhigh</c> is accepted
	/// and mapped down per model, so the clamp keeps the request valid.
	/// </summary>
	[Fact]
	public async Task CompleteChatAsync_OpenRouterBackendDefaultMax_ClampsToXHigh()
	{
		// Arrange: the backend defaults to max and the client sends no think.
		ScriptedHandler handler = new(_ => Json(CompletionBody));
		OpenRouterProvider sut = new(
			new StubHttpClientProvider(handler),
			new StubCapabilityProber(),
			TimeProvider.System,
			Options(backendDefault: ReasoningEffort.Max, "openrouter"),
			new RequestTraceAccessor(),
			TestReasoningDetailsCache.CreateDefault(),
			NullLogger<OpenRouterProvider>.Instance);

		// Act
		JsonObject body = await PostAndCaptureAsync(sut, handler, Request(think: null));

		// Assert: the non-pinned max default is clamped to OpenRouter's inherited xhigh ceiling.
		Assert.Equal("xhigh", body["reasoning"]!["effort"]!.GetValue<string>());
	}

	/// <summary>
	/// Verifies that a non-pinned <c>max</c> backend default on a generic OpenAI backend is clamped to
	/// <c>xhigh</c>, since the OpenAI dialect ceiling tops out there.
	/// </summary>
	[Fact]
	public async Task CompleteChatAsync_OpenAiBackendDefaultMax_ClampsToXHigh()
	{
		// Arrange: the backend defaults to max and the client sends no think.
		ScriptedHandler handler = new(_ => Json(CompletionBody));
		OpenAiProvider sut = new(
			new StubHttpClientProvider(handler),
			new StubCapabilityProber(),
			TimeProvider.System,
			Options(backendDefault: ReasoningEffort.Max, "openai"),
			new RequestTraceAccessor(),
			TestReasoningDetailsCache.CreateDefault(),
			NullLogger<OpenAiProvider>.Instance);

		// Act
		JsonObject body = await PostAndCaptureAsync(sut, handler, Request(think: null));

		// Assert: the non-pinned backend default is clamped to the OpenAI ceiling.
		Assert.Equal("xhigh", body["reasoning_effort"]!.GetValue<string>());
	}

	/// <summary>
	/// Verifies that a <em>pinned</em> <c>Max</c> on OpenRouter overrides the client's <c>think</c> and is
	/// written verbatim into the nested <c>reasoning.effort</c>, bypassing the inherited <c>xhigh</c> ceiling.
	/// A pin is operator-authoritative: the proxy honors it even though OpenRouter's gateway would reject
	/// <c>max</c> live (measured 2026), exactly as a pinned <c>Max</c> is sent verbatim on the generic OpenAI
	/// dialect — the operator owns that trade-off for the specific model they pin.
	/// </summary>
	[Fact]
	public async Task CompleteChatAsync_OpenRouterPinnedMax_OverridesClientAndWritesMax()
	{
		// Arrange
		ScriptedHandler handler = new(_ => Json(CompletionBody));
		OpenRouterProvider sut = new(
			new StubHttpClientProvider(handler),
			new StubCapabilityProber(),
			TimeProvider.System,
			Options(backendDefault: null, "openrouter"),
			new RequestTraceAccessor(),
			TestReasoningDetailsCache.CreateDefault(),
			NullLogger<OpenRouterProvider>.Instance);

		// Act: the pin is Max even though the client asks for low.
		JsonObject body = await PostWithPinAsync(
			                  sut,
			                  handler,
			                  Request(JsonValue.Create("low")),
			                  ReasoningEffort.Max);

		// Assert: the pin overrides the client's low.
		Assert.Equal("max", body["reasoning"]!["effort"]!.GetValue<string>());
	}

	/// <summary>
	/// Verifies that a <em>pinned</em> <c>Max</c> is sent verbatim on the generic OpenAI dialect — exempt
	/// from the <c>xhigh</c> ceiling that would clamp a non-pinned effort.
	/// </summary>
	[Fact]
	public async Task CompleteChatAsync_OpenAiPinnedMax_SendsVerbatimWithoutClamping()
	{
		// Arrange
		ScriptedHandler handler = new(_ => Json(CompletionBody));
		OpenAiProvider sut = new(
			new StubHttpClientProvider(handler),
			new StubCapabilityProber(),
			TimeProvider.System,
			Options(backendDefault: null, "openai"),
			new RequestTraceAccessor(),
			TestReasoningDetailsCache.CreateDefault(),
			NullLogger<OpenAiProvider>.Instance);

		// Act
		JsonObject body = await PostWithPinAsync(sut, handler, Request(think: null), ReasoningEffort.Max);

		// Assert: the pinned max is written verbatim, not clamped.
		Assert.Equal("max", body["reasoning_effort"]!.GetValue<string>());
	}

	private static async Task<JsonObject> PostWithPinAsync(
		OpenAiCompatibleProvider provider,
		ScriptedHandler          handler,
		OllamaChatRequest        request,
		ReasoningEffort          pinnedEffort)
	{
		await provider.CompleteChatAsync(
			new BackendContext(BackendName),
			"m",
			request,
			pinnedEffort,
			CancellationToken.None);

		return (JsonNode.Parse(handler.LastRequestBody!) as JsonObject)!;
	}

	/// <summary>Captures the request body and returns a scripted response.</summary>
	private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
	{
		// ReSharper disable once UnusedAutoPropertyAccessor.Local
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
	/// Reports every probe as inconclusive so the provider can be constructed without probing internals;
	/// these tests exercise reasoning-effort request shaping, not capability determination.
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
