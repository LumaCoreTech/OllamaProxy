// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Globalization;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Options;

using OllamaProxy.Configuration;
using OllamaProxy.Contracts.Ollama;
using OllamaProxy.Diagnostics;
using OllamaProxy.Providers.Abstractions;
using OllamaProxy.Providers.Http;
using OllamaProxy.Providers.OpenAiProtocol;
using OllamaProxy.Providers.OpenRouter.Contracts;

namespace OllamaProxy.Providers.OpenRouter;

/// <summary>
/// The <see cref="OpenAiCompatibleProvider"/> specialized for OpenRouter. OpenRouter is OpenAI-compatible
/// and accepts the flat <c>reasoning_effort</c> field directly, but it also documents a richer unified
/// <c>reasoning</c> object (adding <c>max_tokens</c>, <c>enabled</c>, and <c>exclude</c>) and recommends it
/// for future compatibility; this adapter overrides the reasoning seam to write that <c>reasoning.effort</c>
/// form by choice, not out of necessity (the inherited flat field would also be valid). It keeps the base
/// <see cref="OpenAiCompatibleProvider.MaxDialectReasoningEffort"/> ceiling of <see cref="ReasoningEffort.XHigh"/>:
/// a live probe (2026, against <c>openai/gpt-5.2</c> and <c>anthropic/claude-opus-4.8</c>) showed OpenRouter's
/// gateway rejects <c>reasoning.effort = "max"</c> with HTTP 400 for <em>every</em> model (<c>max</c> is not in
/// its global enum (<c>xhigh, high, medium, low, minimal, none</c>) and is rejected before any model is consulted),
/// while an in-enum over-cap token such as <c>xhigh</c> is accepted and mapped down to a model's nearest level.
/// It additionally overrides discovery because OpenRouter annotates each model with rich metadata (a top-level
/// <c>context_length</c>, a nested <c>top_provider.context_length</c>, an <c>architecture</c> block of input/output
/// modalities, and a <c>supported_parameters</c> list) which it projects natively onto the neutral discovered model.
/// </summary>
sealed class OpenRouterProvider : OpenAiCompatibleProvider, IProviderDescriptorSource
{
	/// <summary>
	/// Initializes a new instance of the <see cref="OpenRouterProvider"/> class.
	/// </summary>
	/// <param name="httpClientProvider">Supplies the pre-configured per-backend HTTP clients.</param>
	/// <param name="capabilityProber">Actively probes a backend when a model carries no metadata capabilities.</param>
	/// <param name="timeProvider">The clock used for response timestamps and duration measurement.</param>
	/// <param name="options">The validated proxy options carrying each backend's reasoning default.</param>
	/// <param name="traceAccessor">Provides the ambient request trace for provenance recording.</param>
	/// <param name="reasoningDetailsCache">Carries opaque <c>reasoning_details</c> blobs across a tool-call conversation.</param>
	/// <param name="logger">The logger used to surface capabilities that stayed inconclusive after probing.</param>
	public OpenRouterProvider(
		IBackendHttpClientProvider  httpClientProvider,
		ICapabilityProber           capabilityProber,
		TimeProvider                timeProvider,
		IOptions<ProxyOptions>      options,
		IRequestTraceAccessor       traceAccessor,
		IReasoningDetailsCache      reasoningDetailsCache,
		ILogger<OpenRouterProvider> logger)
		: base(
			httpClientProvider,
			capabilityProber,
			timeProvider,
			options,
			traceAccessor,
			reasoningDetailsCache,
			logger) { }

	/// <summary>
	/// The OpenRouter provider's self-describing metadata. OpenRouter annotates each model with rich capability
	/// metadata, so a freshly added backend defaults to <see cref="OperatingMode.PlugAndPlay"/>. It can publish a
	/// complete, accurate catalog natively, and the UI prefills OpenRouter's canonical public endpoint.
	/// </summary>
	public static ProviderDescriptor Descriptor { get; } = new(
		"openrouter",
		"OpenRouter",
		OperatingMode.PlugAndPlay,
		"https://openrouter.ai/api/v1");

	/// <inheritdoc/>
	public override string ProviderType => Descriptor.ProviderType;

	/// <inheritdoc/>
	public override Task<IReadOnlyList<DiscoveredModel>> DiscoverModelsAsync(
		BackendContext    backend,
		CancellationToken cancellationToken) => DiscoverModelsCoreAsync<OpenRouterDiscoveryModel>(
		backend,
		ProjectDiscoveredModel,
		cancellationToken);

	/// <summary>
	/// Projects an OpenRouter model entry onto the neutral <see cref="DiscoveredModel"/>. The context
	/// window is taken from the top-level <c>context_length</c>, falling back to the underlying
	/// provider's <c>top_provider.context_length</c>. The reported input/output modalities and supported
	/// parameter list are translated into authoritative <see cref="ModelCapabilities"/> via
	/// <see cref="ModelMetadataCapabilities.FromMetadata"/>, so the base provider adopts them directly
	/// and skips probing. When OpenRouter reports no capability metadata, <see cref="ModelCapabilities"/>
	/// stays <see langword="null"/> and the model falls back to probing.
	/// </summary>
	/// <param name="model">The OpenRouter model entry to project.</param>
	/// <returns>The provider-neutral discovered model carrying OpenRouter's reported metadata.</returns>
	private static DiscoveredModel ProjectDiscoveredModel(OpenRouterDiscoveryModel model) => new(
		model.Id,
		model.Created is { } ts ? DateTimeOffset.FromUnixTimeSeconds(ts) : null,
		ModelMetadataCapabilities.FromMetadata(
			model.Architecture?.InputModalities,
			model.Architecture?.OutputModalities,
			model.SupportedParameters),
		model.ContextLength ?? model.TopProvider?.ContextLength,
		ProjectMetadata(model));

	/// <summary>
	/// Projects OpenRouter's descriptive metadata onto the neutral <see cref="ProviderModelMetadata"/>: the
	/// display name, description, tokenizer family, an upper bound on generated tokens, and pricing. OpenRouter
	/// reports pricing per <em>single</em> token, so the parsed values are scaled to a per-million-token figure
	/// to match the neutral contract. An all-empty result collapses to <see langword="null"/> so a listing that
	/// carried no descriptive metadata is represented as "none" rather than an empty shell.
	/// </summary>
	/// <param name="model">The OpenRouter model entry to read metadata from.</param>
	/// <returns>The neutral metadata, or <see langword="null"/> when the entry reported none.</returns>
	private static ProviderModelMetadata? ProjectMetadata(OpenRouterDiscoveryModel model)
	{
		ProviderModelMetadata metadata = new(
			model.Name,
			model.Description,
			model.Architecture?.Tokenizer,
			Quantization: null,
			model.TopProvider?.MaxCompletionTokens,
			SourceUrl: null,
			ScalePerTokenToPerMillion(model.Pricing?.Prompt),
			ScalePerTokenToPerMillion(model.Pricing?.Completion));

		return metadata.HasAny ? metadata : null;
	}

	/// <summary>
	/// Parses an OpenRouter per-single-token USD price string and scales it to USD per one million tokens. A
	/// <see langword="null"/>, blank, or unparseable value yields <see langword="null"/> so a malformed price
	/// degrades to "no price" rather than throwing; pricing is descriptive metadata, never a routing input.
	/// </summary>
	/// <param name="perTokenUsd">The per-single-token price string OpenRouter reported, or <see langword="null"/>.</param>
	/// <returns>The price in USD per one million tokens, or <see langword="null"/> when absent or unparseable.</returns>
	private static decimal? ScalePerTokenToPerMillion(string? perTokenUsd) => decimal.TryParse(
		                                                                          perTokenUsd,
		                                                                          NumberStyles.Float,
		                                                                          CultureInfo.InvariantCulture,
		                                                                          out decimal perToken)
		                                                                          ? perToken * 1_000_000m
		                                                                          : null;

	/// <summary>
	/// Forwards the de-facto <c>top_k</c> and <c>min_p</c> sampling extensions, which OpenRouter accepts
	/// as documented sampling parameters alongside the standard OpenAI sampling fields.
	/// </summary>
	/// <param name="payload">The chat request body to augment in place.</param>
	/// <param name="request">The inbound Ollama chat request whose options carry the extension values.</param>
	protected override void ApplySamplingExtensions(JsonObject payload, OllamaChatRequest request) =>
		WriteTopKAndMinP(payload, request);

	/// <summary>
	/// Applies the reasoning effort using OpenRouter's unified <c>reasoning</c> object, writing the resolved
	/// level to <c>reasoning.effort</c>. OpenRouter also accepts the flat OpenAI <c>reasoning_effort</c> field
	/// (it is OpenAI-compatible), so this nested form is the documented, recommended encoding rather than a
	/// required one. The effort reaching this seam is already bounded by the inherited
	/// <see cref="OpenAiCompatibleProvider.MaxDialectReasoningEffort"/> ceiling (<see cref="ReasoningEffort.XHigh"/>)
	/// for non-pinned efforts, so a token within OpenRouter's gateway enum is written; OpenRouter then maps a
	/// level a given model does not support down to that model's nearest one on its own side. A <em>pinned</em>
	/// effort bypasses the clamp and is written verbatim, so a pinned <c>max</c> reaches this seam unbounded and
	/// would be rejected with HTTP 400, since <c>max</c> is above OpenRouter's gateway enum (measured 2026).
	/// </summary>
	/// <param name="payload">The chat request body to augment in place.</param>
	/// <param name="effort">The resolved reasoning effort to encode.</param>
	/// <returns>A short description of the wire field(s) written, for provenance tracing.</returns>
	protected override string ApplyReasoning(JsonObject payload, ReasoningEffort effort)
	{
		JsonObject reasoning = payload["reasoning"] as JsonObject ?? new JsonObject();
		reasoning["effort"] = effort.ToWireValue();
		payload["reasoning"] = reasoning;

		return "reasoning.effort";
	}

	/// <summary>
	/// Recognizes a client reasoning directive in OpenRouter's dialect on a verbatim passthrough payload:
	/// the unified <c>reasoning</c> object (with an <c>effort</c>, <c>max_tokens</c>, <c>enabled</c>, or
	/// <c>exclude</c> field) <em>or</em> the flat OpenAI-style <c>reasoning_effort</c> token the base provider
	/// recognizes. Either means the client already chose, so the backend default must not override it.
	/// </summary>
	/// <param name="payload">The inbound passthrough request body to inspect.</param>
	/// <returns><see langword="true"/> when the client already specified a reasoning directive.</returns>
	protected override bool HasClientReasoningDirective(JsonObject payload) =>
		base.HasClientReasoningDirective(payload) || payload["reasoning"] is JsonObject;

	/// <summary>
	/// Strips the flat OpenAI-style <c>reasoning_effort</c> field <em>and</em> the unified nested
	/// <c>reasoning</c> object from a passthrough payload, so a pinned effort can be written cleanly without
	/// colliding with a client-supplied directive in either shape.
	/// </summary>
	/// <param name="payload">The passthrough request body to strip in place.</param>
	protected override void StripClientReasoningDirectives(JsonObject payload)
	{
		base.StripClientReasoningDirectives(payload);
		payload.Remove("reasoning");
	}
}
