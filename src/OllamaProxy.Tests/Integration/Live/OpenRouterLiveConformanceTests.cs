// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Microsoft.Extensions.Logging.Abstractions;

using OllamaProxy.Providers.OpenRouter;

namespace OllamaProxy.Tests.Integration.Live;

/// <summary>
/// Live provider-conformance tests that drive the real <see cref="OpenRouterProvider"/> against the live
/// OpenRouter backend through its public adapter API — the same surface production routes through. The story
/// walks every "knob" a client exercises: a plain chat completion, a streamed completion, a tool-advertising
/// turn, a multimodal vision turn, embeddings, and the server-side <c>reasoning_details</c> round-trip. Each
/// test asserts only contract and shape invariants (the call conformed, the response mapped cleanly, tool
/// arguments are a JSON object, the stream terminates) — never the model's non-deterministic wording.
/// <para>
/// The whole class is gated on environment variables through <see cref="LiveBackendFactAttribute"/>: with no
/// API key set, every test reports <em>Skipped</em>, so the suite is safe to leave in the default run and
/// never flakes in unattended CI. Vision, embeddings, and reasoning additionally require their own model
/// variable; absent it, that single test skips while the rest still run.
/// </para>
/// </summary>
[Trait("Category", "Live")]
public sealed class OpenRouterLiveConformanceTests
{
	private const string ApiKeyEnv         = "OLLAMAPROXY_LIVE_OPENROUTER_API_KEY";
	private const string BaseUrlEnv        = "OLLAMAPROXY_LIVE_OPENROUTER_BASE_URL";
	private const string ChatModelEnv      = "OLLAMAPROXY_LIVE_OPENROUTER_CHAT_MODEL";
	private const string VisionModelEnv    = "OLLAMAPROXY_LIVE_OPENROUTER_VISION_MODEL";
	private const string EmbeddingModelEnv = "OLLAMAPROXY_LIVE_OPENROUTER_EMBED_MODEL";
	private const string ReasoningModelEnv = "OLLAMAPROXY_LIVE_OPENROUTER_REASONING_MODEL";

	private const string DefaultBaseUrl   = "https://openrouter.ai/api/v1";
	private const string DefaultChatModel = "openai/gpt-5.2";

	private static readonly LiveBackendDescriptor Descriptor = new(
		BackendName: "openrouter-live",
		ProviderType: "openrouter",
		ApiKeyEnv: ApiKeyEnv,
		BaseUrlEnv: BaseUrlEnv,
		DefaultBaseUrl: DefaultBaseUrl,
		ChatModelEnv: ChatModelEnv,
		DefaultChatModel: DefaultChatModel,
		VisionModelEnv: VisionModelEnv,
		EmbeddingModelEnv: EmbeddingModelEnv,
		ReasoningModelEnv: ReasoningModelEnv);

	private static LiveProviderHarness<OpenRouterProvider> CreateHarness() => LiveProviderHarness.Create(
		LiveBackendConfig.Create(Descriptor),
		dependencies => new OpenRouterProvider(
			dependencies.HttpClientProvider,
			dependencies.CapabilityProber,
			dependencies.TimeProvider,
			dependencies.Options,
			dependencies.TraceAccessor,
			dependencies.ReasoningDetailsCache,
			NullLogger<OpenRouterProvider>.Instance));

	/// <summary>
	/// Verifies a non-streaming chat completion conforms: the request is accepted and the proxy maps a
	/// terminal Ollama response with non-empty assistant content.
	/// </summary>
	[LiveBackendFact(ApiKeyEnv)]
	public async Task CompleteChatAsync_AgainstLiveOpenRouter_Conforms()
	{
		// Arrange
		using LiveProviderHarness<OpenRouterProvider> harness = CreateHarness();

		// Act + Assert
		await LiveConformance.AssertChatCompletesAsync(harness.Provider, harness.Config, harness.Token);
	}

	/// <summary>
	/// Verifies a streamed chat completion conforms: the SSE pipeline yields chunks that aggregate to
	/// non-empty content and terminate with exactly one done chunk.
	/// </summary>
	[LiveBackendFact(ApiKeyEnv)]
	public async Task StreamChatAsync_AgainstLiveOpenRouter_Conforms()
	{
		// Arrange
		using LiveProviderHarness<OpenRouterProvider> harness = CreateHarness();

		// Act + Assert
		await LiveConformance.AssertStreamCompletesAsync(harness.Provider, harness.Config, harness.Token);
	}

	/// <summary>
	/// Verifies tool use conforms: the tool-advertising request is accepted, and any surfaced tool call
	/// carries a structured JSON-object argument payload (Ollama's contract, not OpenAI's string).
	/// </summary>
	[LiveBackendFact(ApiKeyEnv)]
	public async Task CompleteChatAsync_WithTool_AgainstLiveOpenRouter_Conforms()
	{
		// Arrange
		using LiveProviderHarness<OpenRouterProvider> harness = CreateHarness();

		// Act + Assert
		await LiveConformance.AssertToolUseConformsAsync(harness.Provider, harness.Config, harness.Token);
	}

	/// <summary>
	/// Verifies multimodal input conforms: a tiny inline image is accepted and the proxy maps a terminal
	/// answer back, proving the image-part translation works against the live backend.
	/// </summary>
	[LiveBackendFact(ApiKeyEnv, VisionModelEnv)]
	public async Task CompleteChatAsync_WithImage_AgainstLiveOpenRouter_Conforms()
	{
		// Arrange
		using LiveProviderHarness<OpenRouterProvider> harness = CreateHarness();

		// Act + Assert
		await LiveConformance.AssertVisionConformsAsync(harness.Provider, harness.Config, harness.Token);
	}

	/// <summary>
	/// Verifies embeddings conform: the request is accepted and the proxy projects a single non-empty vector
	/// of finite components.
	/// </summary>
	[LiveBackendFact(ApiKeyEnv, EmbeddingModelEnv)]
	public async Task CreateEmbeddingsAsync_AgainstLiveOpenRouter_Conforms()
	{
		// Arrange
		using LiveProviderHarness<OpenRouterProvider> harness = CreateHarness();

		// Act + Assert
		await LiveConformance.AssertEmbeddingsConformAsync(harness.Provider, harness.Config, harness.Token);
	}

	/// <summary>
	/// Verifies the server-side <c>reasoning_details</c> round-trip conforms: when the live backend emits a
	/// blob on a tool-calling turn, the follow-up upstream body re-attaches the exact captured blob. A turn
	/// where the model declines the tool or emits no blob is a legitimate non-deterministic outcome and does
	/// not fail the test.
	/// </summary>
	[LiveBackendFact(ApiKeyEnv, ReasoningModelEnv)]
	public async Task ReasoningRoundTrip_AgainstLiveOpenRouter_Conforms()
	{
		// Arrange
		using LiveProviderHarness<OpenRouterProvider> harness = CreateHarness();

		// Act
		ReasoningRoundTripOutcome outcome = await LiveConformance.AssertReasoningRoundTripConformsAsync(
			                                    harness.Provider,
			                                    harness.Recorder,
			                                    harness.Cache,
			                                    harness.Config,
			                                    harness.Token);

		// Assert: the strong re-attachment assertion lives inside the helper; here we only record that the
		// call path conformed regardless of whether the non-deterministic blob was emitted on this run.
		Assert.True(Enum.IsDefined(outcome));
	}
}
