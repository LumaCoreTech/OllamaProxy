// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json.Nodes;

using Microsoft.Extensions.Options;

using OllamaProxy.Configuration;
using OllamaProxy.Contracts.Ollama;
using OllamaProxy.Diagnostics;
using OllamaProxy.Providers.Abstractions;
using OllamaProxy.Providers.Http;
using OllamaProxy.Providers.OpenAiProtocol;
using OllamaProxy.Providers.Venice.Contracts;

namespace OllamaProxy.Providers.Venice;

/// <summary>
/// The <see cref="OpenAiCompatibleProvider"/> specialized for Venice. Venice speaks the standard
/// OpenAI <c>reasoning_effort</c> dialect for positive efforts but disables reasoning through its own
/// <c>venice_parameters.disable_thinking</c> flag rather than a <c>reasoning_effort</c> of <c>none</c>,
/// so this adapter overrides the reasoning seam. It also overrides discovery: Venice publishes its
/// capabilities as a structured <c>model_spec</c> block (vision, function calling, available context
/// tokens) plus a top-level <c>type</c> discriminator, which this adapter translates into the neutral
/// modality/parameter metadata the shared capability detection already understands.
/// <para>
/// The adapter additionally <em>suppresses Venice's vendor system prompt</em> by authoritatively writing
/// <c>venice_parameters.include_venice_system_prompt = false</c> on every chat request (both the
/// Ollama-native path and the verbatim <c>/v1</c> passthrough), overwriting any value the client may have
/// supplied. The Ollama-Proxy chassis promises transparent request semantics, so a vendor-injected prompt
/// is undesirable by default.
/// </para>
/// </summary>
sealed class VeniceProvider : OpenAiCompatibleProvider, IProviderDescriptorSource
{
	/// <summary>
	/// Initializes a new instance of the <see cref="VeniceProvider"/> class.
	/// </summary>
	/// <param name="httpClientProvider">Supplies the pre-configured per-backend HTTP clients.</param>
	/// <param name="capabilityProber">Actively probes a backend when a model carries no metadata capabilities.</param>
	/// <param name="timeProvider">The clock used for response timestamps and duration measurement.</param>
	/// <param name="options">The validated proxy options carrying each backend's reasoning default.</param>
	/// <param name="traceAccessor">Provides the ambient request trace for provenance recording.</param>
	/// <param name="reasoningDetailsCache">Carries opaque <c>reasoning_details</c> blobs across a tool-call conversation.</param>
	/// <param name="logger">The logger used to surface capabilities that stayed inconclusive after probing.</param>
	public VeniceProvider(
		IBackendHttpClientProvider httpClientProvider,
		ICapabilityProber          capabilityProber,
		TimeProvider               timeProvider,
		IOptions<ProxyOptions>     options,
		IRequestTraceAccessor      traceAccessor,
		IReasoningDetailsCache     reasoningDetailsCache,
		ILogger<VeniceProvider>    logger)
		: base(
			httpClientProvider,
			capabilityProber,
			timeProvider,
			options,
			traceAccessor,
			reasoningDetailsCache,
			logger) { }

	/// <summary>
	/// The Venice provider's self-describing metadata. Venice publishes rich capability metadata in its model
	/// listing, so a freshly added backend defaults to <see cref="OperatingMode.PlugAndPlay"/>. It can publish a
	/// complete, accurate catalog without extra probing, and the UI prefills Venice's canonical public endpoint.
	/// </summary>
	public static ProviderDescriptor Descriptor { get; } = new(
		"venice",
		"Venice",
		OperatingMode.PlugAndPlay,
		"https://api.venice.ai/api/v1");

	/// <inheritdoc/>
	public override string ProviderType => Descriptor.ProviderType;

	/// <summary>
	/// Raises the dialect ceiling to <see cref="ReasoningEffort.Max"/> so a non-pinned <c>max</c> is forwarded
	/// rather than clamped down to <c>xhigh</c> as the base OpenAI dialect would.
	/// <para>
	/// <b>Unverified assumption:</b> that Venice's API accepts the extended <c>max</c> token (the rationale being
	/// the Claude models it serves) has <em>not</em> been measured against a live Venice backend. A 2026 live
	/// probe of the analogous OpenRouter assumption disproved it: OpenRouter's gateway rejects <c>max</c> with
	/// HTTP 400 for <em>every</em> model, including Claude Opus, because routing a Claude model through a gateway
	/// is not the same as Anthropic's native API. Venice may behave the same way. Until a live probe confirms it,
	/// treat this ceiling as an unverified assumption, not measured behavior; if it proves wrong, lower this
	/// to <see cref="ReasoningEffort.XHigh"/> exactly as OpenRouter was corrected.
	/// </para>
	/// </summary>
	protected override ReasoningEffort MaxDialectReasoningEffort => ReasoningEffort.Max;

	/// <inheritdoc/>
	public override Task<IReadOnlyList<DiscoveredModel>> DiscoverModelsAsync(
		BackendContext    backend,
		CancellationToken cancellationToken) => DiscoverModelsCoreAsync<VeniceDiscoveryModel>(
		backend,
		ProjectDiscoveredModel,
		cancellationToken);

	/// <summary>
	/// Projects a Venice model entry onto the neutral <see cref="DiscoveredModel"/> by translating
	/// Venice's structured capability metadata into authoritative <see cref="ModelCapabilities"/>, so the
	/// base provider adopts them directly and skips probing.
	/// </summary>
	/// <remarks>
	/// The context window is taken from <c>model_spec.availableContextTokens</c>. The top-level
	/// <c>type</c> field (<c>text</c> / <c>image</c>) is translated into output modalities so the shared
	/// completion guard filters generation-only image models. Vision and function-calling flags from
	/// <c>model_spec.capabilities</c> become neutral input modalities and supported parameters, which
	/// <see cref="ModelMetadataCapabilities.FromMetadata"/> then folds into the capability flags.
	/// </remarks>
	/// <param name="model">The Venice model entry to project.</param>
	/// <returns>The provider-neutral discovered model carrying Venice's translated capabilities.</returns>
	private static DiscoveredModel ProjectDiscoveredModel(VeniceDiscoveryModel model) => new(
		model.Id,
		model.Created is { } ts ? DateTimeOffset.FromUnixTimeSeconds(ts) : null,
		ModelMetadataCapabilities.FromMetadata(
			VeniceInputModalities(model.ModelSpec),
			VeniceOutputModalities(model.Type),
			VeniceSupportedParameters(model.ModelSpec)),
		model.ModelSpec?.AvailableContextTokens,
		ProjectMetadata(model.ModelSpec));

	/// <summary>
	/// Projects Venice's <c>model_spec</c> descriptive fields onto the neutral <see cref="ProviderModelMetadata"/>:
	/// the display name, description, source link, an upper bound on generated tokens, the weight quantization
	/// (the one field that maps onto Ollama's <c>quantization_level</c>), and pricing. Venice already reports
	/// prices per one million tokens, so the USD figures are adopted directly. An all-empty result collapses to
	/// <see langword="null"/> so a spec that carried no descriptive metadata is represented as "none".
	/// </summary>
	/// <param name="spec">The Venice model spec to read metadata from, or <see langword="null"/> when absent.</param>
	/// <returns>The neutral metadata, or <see langword="null"/> when the spec reported none.</returns>
	private static ProviderModelMetadata? ProjectMetadata(VeniceModelSpec? spec)
	{
		if (spec is null) return null;

		ProviderModelMetadata metadata = new(
			spec.Name,
			spec.Description,
			Tokenizer: null,
			spec.Capabilities?.Quantization,
			spec.MaxCompletionTokens,
			spec.ModelSource,
			spec.Pricing?.Input?.Usd,
			spec.Pricing?.Output?.Usd);

		return metadata.HasAny ? metadata : null;
	}

	/// <summary>
	/// Synthesizes neutral output-modality metadata from the Venice top-level <c>type</c> field. Venice
	/// reports <c>"image"</c> for generation-only models and <c>"text"</c> for language models; both
	/// translate into the neutral output-modalities shape the shared completion guard understands, so it
	/// filters image models without a Venice-specific code path. Returns <see langword="null"/> when the
	/// field is absent so the conservative completion assumption holds.
	/// </summary>
	/// <param name="type">The Venice model type string, or <see langword="null"/> when not reported.</param>
	/// <returns>The synthesized output modalities, or <see langword="null"/> when the field is absent.</returns>
	private static IReadOnlyList<string>? VeniceOutputModalities(string? type) => type switch
	{
		"image" => ["image"],
		"text"  => ["text"],
		var _   => null
	};

	/// <summary>
	/// Synthesizes neutral input-modality metadata from a Venice <c>model_spec</c>, emitting
	/// <c>image</c> when the spec advertises vision support so the shared detection infers it.
	/// </summary>
	/// <param name="spec">The Venice model specification, or <see langword="null"/> when not reported.</param>
	/// <returns>
	/// The synthesized input modalities, or <see langword="null"/> when the spec carries no usable
	/// capability information.
	/// </returns>
	private static IReadOnlyList<string>? VeniceInputModalities(VeniceModelSpec? spec)
	{
		if (spec?.Capabilities is not { } capabilities) return null;

		// Always include text (every chat model accepts it); add image only when vision is advertised.
		return capabilities.SupportsVision == true ? ["text", "image"] : ["text"];
	}

	/// <summary>
	/// Synthesizes neutral supported-parameter metadata from a Venice <c>model_spec</c>, emitting
	/// <c>tools</c> when the spec advertises function calling so the shared detection infers tool support.
	/// </summary>
	/// <param name="spec">The Venice model specification, or <see langword="null"/> when not reported.</param>
	/// <returns>
	/// The synthesized supported parameters, or <see langword="null"/> when the spec carries no usable
	/// capability information.
	/// </returns>
	private static IReadOnlyList<string>? VeniceSupportedParameters(VeniceModelSpec? spec)
	{
		if (spec?.Capabilities is not { } capabilities) return null;

		return capabilities.SupportsFunctionCalling == true ? ["tools"] : [];
	}

	/// <summary>
	/// Applies the reasoning effort using Venice's dialect: a <see cref="ReasoningEffort.None"/>
	/// becomes <c>venice_parameters.disable_thinking = true</c> (Venice's documented off switch), while
	/// every positive effort is written as the standard <c>reasoning_effort</c> token Venice shares
	/// with OpenAI, including the extended <c>max</c> level (which Venice accepts).
	/// </summary>
	/// <param name="payload">The chat request body to augment in place.</param>
	/// <param name="effort">The resolved reasoning effort to encode.</param>
	/// <returns>A short description of the wire field(s) written, for provenance tracing.</returns>
	protected override string ApplyReasoning(JsonObject payload, ReasoningEffort effort)
	{
		if (effort == ReasoningEffort.None)
		{
			// Venice turns reasoning off via its vendor extension rather than reasoning_effort: "none".
			JsonObject veniceParameters = payload["venice_parameters"] as JsonObject ?? new JsonObject();
			veniceParameters["disable_thinking"] = true;
			payload["venice_parameters"] = veniceParameters;
			return "venice_parameters.disable_thinking";
		}

		payload["reasoning_effort"] = effort.ToWireValue();
		return "reasoning_effort";
	}

	/// <summary>
	/// Forwards the de-facto <c>top_k</c> and <c>min_p</c> sampling extensions, which Venice honors
	/// alongside the standard OpenAI sampling fields.
	/// </summary>
	/// <param name="payload">The chat request body to augment in place.</param>
	/// <param name="request">The inbound Ollama chat request whose options carry the extension values.</param>
	protected override void ApplySamplingExtensions(JsonObject payload, OllamaChatRequest request) =>
		WriteTopKAndMinP(payload, request);

	/// <summary>
	/// Recognizes a client reasoning directive in Venice's dialect on a verbatim passthrough payload: the
	/// standard <c>reasoning_effort</c> token <em>or</em> Venice's <c>venice_parameters.disable_thinking</c>
	/// off switch. Either means the client already chose, so the backend default must not override it.
	/// </summary>
	/// <param name="payload">The inbound passthrough request body to inspect.</param>
	/// <returns><see langword="true"/> when the client already specified a reasoning directive.</returns>
	protected override bool HasClientReasoningDirective(JsonObject payload)
	{
		return base.HasClientReasoningDirective(payload) ||
		       (payload["venice_parameters"] is JsonObject veniceParameters &&
		        veniceParameters.ContainsKey("disable_thinking"));
	}

	/// <summary>
	/// Authoritatively suppresses Venice's vendor-injected system prompt for every chat request to this
	/// backend, by writing <c>venice_parameters.include_venice_system_prompt = false</c>. The value is
	/// forced unconditionally (overwriting any client-supplied value) because the choice belongs to the
	/// operator who configured the backend, not to a client that has no notion of Venice's vendor prompt.
	/// An empty <c>venice_parameters</c> container is never sent: if writing this is the only key, it is
	/// created on demand, and if it already carried keys, they are preserved alongside the forced false.
	/// </summary>
	/// <param name="backend">The backend the request targets (unused; the override is identical for all Venice backends).</param>
	/// <param name="payload">The chat request body to augment in place.</param>
	protected override void ApplyVendorParameters(BackendContext backend, JsonObject payload)
	{
		JsonObject veniceParameters = payload["venice_parameters"] as JsonObject ?? new JsonObject();
		veniceParameters["include_venice_system_prompt"] = false;
		payload["venice_parameters"] = veniceParameters;
	}

	/// <summary>
	/// Strips the standard <c>reasoning_effort</c> field <em>and</em> Venice's
	/// <c>venice_parameters.disable_thinking</c> switch from a passthrough payload, so a pinned effort can be
	/// written without leaving a conflicting client directive behind. The empty <c>venice_parameters</c> object
	/// is removed when stripping its only key, to avoid sending an empty container upstream.
	/// </summary>
	/// <param name="payload">The passthrough request body to strip in place.</param>
	protected override void StripClientReasoningDirectives(JsonObject payload)
	{
		base.StripClientReasoningDirectives(payload);

		if (payload["venice_parameters"] is not JsonObject veniceParameters) return;

		veniceParameters.Remove("disable_thinking");
		if (veniceParameters.Count == 0) payload.Remove("venice_parameters");
	}
}
