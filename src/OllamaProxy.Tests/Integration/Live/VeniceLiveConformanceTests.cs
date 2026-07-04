// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Microsoft.Extensions.Logging.Abstractions;

using OllamaProxy.Providers.Venice;

namespace OllamaProxy.Tests.Integration.Live;

/// <summary>
/// Live provider-conformance tests that drive the real <see cref="VeniceProvider"/> against the live Venice
/// backend through its public adapter API — the same surface production routes through. The story walks every
/// "knob" a client exercises: a plain chat completion, a streamed completion, a tool-advertising turn, a
/// multimodal vision turn, embeddings, and the server-side <c>reasoning_details</c> round-trip. Each test
/// asserts only contract and shape invariants — never the model's non-deterministic wording.
/// <para>
/// Venice's specialization (the <c>venice_parameters</c> vendor block and its reasoning dialect) rides along
/// transparently through the real adapter, so these tests also prove that specialization does not break the
/// inherited chat, tool, and round-trip behavior against the live backend. The class is gated on environment
/// variables through <see cref="LiveBackendFactAttribute"/>: with no API key set, every test reports
/// <em>Skipped</em>; vision, embeddings, and reasoning additionally require their own model variable.
/// </para>
/// </summary>
[Trait("Category", "Live")]
public sealed class VeniceLiveConformanceTests
{
	private const string ApiKeyEnv         = "OLLAMAPROXY_LIVE_VENICE_API_KEY";
	private const string BaseUrlEnv        = "OLLAMAPROXY_LIVE_VENICE_BASE_URL";
	private const string ChatModelEnv      = "OLLAMAPROXY_LIVE_VENICE_CHAT_MODEL";
	private const string VisionModelEnv    = "OLLAMAPROXY_LIVE_VENICE_VISION_MODEL";
	private const string EmbeddingModelEnv = "OLLAMAPROXY_LIVE_VENICE_EMBED_MODEL";
	private const string ReasoningModelEnv = "OLLAMAPROXY_LIVE_VENICE_REASONING_MODEL";

	private const string DefaultBaseUrl   = "https://api.venice.ai/api/v1";
	private const string DefaultChatModel = "qwen3-235b";

	private static readonly LiveBackendDescriptor Descriptor = new(
		BackendName: "venice-live",
		ProviderType: "venice",
		ApiKeyEnv: ApiKeyEnv,
		BaseUrlEnv: BaseUrlEnv,
		DefaultBaseUrl: DefaultBaseUrl,
		ChatModelEnv: ChatModelEnv,
		DefaultChatModel: DefaultChatModel,
		VisionModelEnv: VisionModelEnv,
		EmbeddingModelEnv: EmbeddingModelEnv,
		ReasoningModelEnv: ReasoningModelEnv);

	private static LiveProviderHarness<VeniceProvider> CreateHarness() => LiveProviderHarness.Create(
		LiveBackendConfig.Create(Descriptor),
		dependencies => new VeniceProvider(
			dependencies.HttpClientProvider,
			dependencies.CapabilityProber,
			dependencies.TimeProvider,
			dependencies.Options,
			dependencies.TraceAccessor,
			dependencies.ReasoningDetailsCache,
			NullLogger<VeniceProvider>.Instance));

	/// <summary>
	/// Verifies a non-streaming chat completion conforms: the request is accepted and the proxy maps a
	/// terminal Ollama response with non-empty assistant content.
	/// </summary>
	[LiveBackendFact(ApiKeyEnv)]
	public async Task CompleteChatAsync_AgainstLiveVenice_Conforms()
	{
		// Arrange
		using LiveProviderHarness<VeniceProvider> harness = CreateHarness();

		// Act + Assert
		await LiveConformance.AssertChatCompletesAsync(harness.Provider, harness.Config, harness.Token);
	}

	/// <summary>
	/// Verifies a streamed chat completion conforms: the SSE pipeline yields chunks that aggregate to
	/// non-empty content and terminate with exactly one done chunk.
	/// </summary>
	[LiveBackendFact(ApiKeyEnv)]
	public async Task StreamChatAsync_AgainstLiveVenice_Conforms()
	{
		// Arrange
		using LiveProviderHarness<VeniceProvider> harness = CreateHarness();

		// Act + Assert
		await LiveConformance.AssertStreamCompletesAsync(harness.Provider, harness.Config, harness.Token);
	}

	/// <summary>
	/// Verifies tool use conforms: the tool-advertising request is accepted, and any surfaced tool call
	/// carries a structured JSON-object argument payload (Ollama's contract, not OpenAI's string).
	/// </summary>
	[LiveBackendFact(ApiKeyEnv)]
	public async Task CompleteChatAsync_WithTool_AgainstLiveVenice_Conforms()
	{
		// Arrange
		using LiveProviderHarness<VeniceProvider> harness = CreateHarness();

		// Act + Assert
		await LiveConformance.AssertToolUseConformsAsync(harness.Provider, harness.Config, harness.Token);
	}

	/// <summary>
	/// Verifies multimodal input conforms: a tiny inline image is accepted and the proxy maps a terminal
	/// answer back, proving the image-part translation works against the live backend.
	/// </summary>
	[LiveBackendFact(ApiKeyEnv, VisionModelEnv)]
	public async Task CompleteChatAsync_WithImage_AgainstLiveVenice_Conforms()
	{
		// Arrange
		using LiveProviderHarness<VeniceProvider> harness = CreateHarness();

		// Act + Assert
		await LiveConformance.AssertVisionConformsAsync(harness.Provider, harness.Config, harness.Token);
	}

	/// <summary>
	/// Verifies embeddings conform: the request is accepted and the proxy projects a single non-empty vector
	/// of finite components.
	/// </summary>
	[LiveBackendFact(ApiKeyEnv, EmbeddingModelEnv)]
	public async Task CreateEmbeddingsAsync_AgainstLiveVenice_Conforms()
	{
		// Arrange
		using LiveProviderHarness<VeniceProvider> harness = CreateHarness();

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
	public async Task ReasoningRoundTrip_AgainstLiveVenice_Conforms()
	{
		// Arrange
		using LiveProviderHarness<VeniceProvider> harness = CreateHarness();

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
