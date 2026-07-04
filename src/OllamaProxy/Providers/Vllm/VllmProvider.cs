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
using OllamaProxy.Providers.Vllm.Contracts;

namespace OllamaProxy.Providers.Vllm;

/// <summary>
/// The <see cref="OpenAiCompatibleProvider"/> specialized for vLLM. Modern vLLM understands the
/// standard <c>reasoning_effort</c> field and auto-injects the model's <c>enable_thinking</c> template
/// flag from it, but older deployments and many chat templates only honor an explicit
/// <c>chat_template_kwargs.enable_thinking</c> boolean. This adapter therefore writes <em>both</em>:
/// the portable <c>reasoning_effort</c> token for newer servers and the explicit template flag for
/// older ones, so reasoning works across the full range of vLLM versions. It applies no vLLM-specific
/// dialect ceiling, so it inherits the base <see cref="OpenAiCompatibleProvider.MaxDialectReasoningEffort"/>
/// of <see cref="ReasoningEffort.XHigh"/>; which reasoning-effort tokens a given vLLM build accepts
/// depends on the served model and its chat template, which this adapter does not attempt to enumerate.
/// </summary>
sealed class VllmProvider : OpenAiCompatibleProvider, IProviderDescriptorSource
{
	/// <summary>
	/// Initializes a new instance of the <see cref="VllmProvider"/> class.
	/// </summary>
	/// <param name="httpClientProvider">Supplies the pre-configured per-backend HTTP clients.</param>
	/// <param name="capabilityProber">Actively probes a backend when a model carries no metadata capabilities.</param>
	/// <param name="timeProvider">The clock used for response timestamps and duration measurement.</param>
	/// <param name="options">The validated proxy options carrying each backend's reasoning default.</param>
	/// <param name="traceAccessor">Provides the ambient request trace for provenance recording.</param>
	/// <param name="reasoningDetailsCache">Carries opaque <c>reasoning_details</c> blobs across a tool-call conversation.</param>
	/// <param name="logger">The logger used to surface capabilities that stayed inconclusive after probing.</param>
	public VllmProvider(
		IBackendHttpClientProvider httpClientProvider,
		ICapabilityProber          capabilityProber,
		TimeProvider               timeProvider,
		IOptions<ProxyOptions>     options,
		IRequestTraceAccessor      traceAccessor,
		IReasoningDetailsCache     reasoningDetailsCache,
		ILogger<VllmProvider>      logger)
		: base(
			httpClientProvider,
			capabilityProber,
			timeProvider,
			options,
			traceAccessor,
			reasoningDetailsCache,
			logger) { }

	/// <summary>
	/// The vLLM provider's self-describing metadata. vLLM advertises no capability metadata, so a freshly added
	/// backend defaults to the conservative <see cref="OperatingMode.Explicit"/>; it is always self-hosted, so the
	/// UI suggests no canonical URL (an empty default base URL).
	/// </summary>
	public static ProviderDescriptor Descriptor { get; } = new(
		"vllm",
		"vLLM",
		OperatingMode.Explicit,
		string.Empty);

	/// <inheritdoc/>
	public override string ProviderType => Descriptor.ProviderType;

	/// <inheritdoc/>
	public override Task<IReadOnlyList<DiscoveredModel>> DiscoverModelsAsync(
		BackendContext    backend,
		CancellationToken cancellationToken)
	{
		return DiscoverModelsCoreAsync<VllmDiscoveryModel>(
			backend,
			ProjectDiscoveredModel,
			cancellationToken);
	}

	/// <summary>
	/// Projects a vLLM model entry onto the neutral <see cref="DiscoveredModel"/>, reading the context
	/// window from vLLM's <c>max_model_len</c>. vLLM does not advertise capability metadata, so input
	/// modalities, output modalities, and supported parameters are left unset for the later detection
	/// stages to resolve.
	/// </summary>
	/// <param name="model">The vLLM model entry to project.</param>
	/// <returns>The provider-neutral discovered model carrying the context length when reported.</returns>
	private static DiscoveredModel ProjectDiscoveredModel(VllmDiscoveryModel model)
	{
		return new DiscoveredModel(
			model.Id,
			model.Created is { } ts ? DateTimeOffset.FromUnixTimeSeconds(ts) : null,
			ContextLength: model.MaxModelLen);
	}

	/// <summary>
	/// Applies the reasoning effort for vLLM by writing the portable <c>reasoning_effort</c> token
	/// (honored by modern vLLM, which derives <c>enable_thinking</c> from it) <em>and</em> the explicit
	/// <c>chat_template_kwargs.enable_thinking</c> boolean (honored by older vLLM and templates that
	/// only read the kwarg). A <see cref="ReasoningEffort.None"/> sets the flag to <see langword="false"/>;
	/// any positive effort sets it to <see langword="true"/>. Templates that do not declare the kwarg
	/// ignore it harmlessly.
	/// </summary>
	/// <param name="payload">The chat request body to augment in place.</param>
	/// <param name="effort">The resolved reasoning effort to encode.</param>
	/// <returns>A short description of the wire field(s) written, for provenance tracing.</returns>
	protected override string ApplyReasoning(JsonObject payload, ReasoningEffort effort)
	{
		payload["reasoning_effort"] = effort.ToWireValue();

		// Older vLLM versions and many chat templates only honor the explicit kwarg, so set it too; modern
		// servers and templates that do not declare it simply ignore the extra flag.
		JsonObject templateKwargs = payload["chat_template_kwargs"] as JsonObject ?? new JsonObject();
		templateKwargs["enable_thinking"] = effort != ReasoningEffort.None;
		payload["chat_template_kwargs"] = templateKwargs;

		return "reasoning_effort + chat_template_kwargs.enable_thinking";
	}

	/// <summary>
	/// Forwards the de-facto <c>top_k</c> and <c>min_p</c> sampling extensions, which vLLM honors
	/// natively alongside the standard OpenAI sampling fields.
	/// </summary>
	/// <param name="payload">The chat request body to augment in place.</param>
	/// <param name="request">The inbound Ollama chat request whose options carry the extension values.</param>
	protected override void ApplySamplingExtensions(JsonObject payload, OllamaChatRequest request)
	{
		WriteTopKAndMinP(payload, request);
	}

	/// <summary>
	/// Recognizes a client reasoning directive in vLLM's dialect on a verbatim passthrough payload: the
	/// standard <c>reasoning_effort</c> token <em>or</em> the explicit
	/// <c>chat_template_kwargs.enable_thinking</c> kwarg. Either means the client already chose, so the
	/// backend default must not override it.
	/// </summary>
	/// <param name="payload">The inbound passthrough request body to inspect.</param>
	/// <returns><see langword="true"/> when the client already specified a reasoning directive.</returns>
	protected override bool HasClientReasoningDirective(JsonObject payload)
	{
		return base.HasClientReasoningDirective(payload) ||
		       (payload["chat_template_kwargs"] is JsonObject templateKwargs &&
		        templateKwargs.ContainsKey("enable_thinking"));
	}

	/// <summary>
	/// Strips the standard <c>reasoning_effort</c> field <em>and</em> vLLM's
	/// <c>chat_template_kwargs.enable_thinking</c> switch from a passthrough payload, so a pinned effort can be
	/// written without leaving a conflicting client directive behind. The empty <c>chat_template_kwargs</c>
	/// object is removed when stripping its only key, to avoid sending an empty container upstream.
	/// </summary>
	/// <param name="payload">The passthrough request body to strip in place.</param>
	protected override void StripClientReasoningDirectives(JsonObject payload)
	{
		base.StripClientReasoningDirectives(payload);

		if (payload["chat_template_kwargs"] is not JsonObject templateKwargs) return;

		templateKwargs.Remove("enable_thinking");
		if (templateKwargs.Count == 0) payload.Remove("chat_template_kwargs");
	}
}
