// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Microsoft.Extensions.Options;

using OllamaProxy.Configuration;
using OllamaProxy.Diagnostics;
using OllamaProxy.Providers.Abstractions;
using OllamaProxy.Providers.Http;
using OllamaProxy.Providers.OpenAi.Contracts;
using OllamaProxy.Providers.OpenAiProtocol;

namespace OllamaProxy.Providers.OpenAi;

/// <summary>
/// The <see cref="OpenAiCompatibleProvider"/> for the official OpenAI API and any generic
/// OpenAI-compatible backend that does not warrant a specialized adapter. It is the default selected by
/// the <c>openai</c> provider type (see <see cref="Descriptor"/>) and inherits the shared protocol surface
/// unchanged, including the base reasoning behavior which writes the standard <c>reasoning_effort</c> field.
/// </summary>
/// <remarks>
/// Discovery is intentionally strict: the official OpenAI <c>GET /v1/models</c> response carries only
/// <c>id</c>, <c>created</c>, and <c>owned_by</c>, with no context length and no capability metadata. This
/// adapter therefore reports neither, deferring context length to operator configuration and capability
/// detection to the later probing. Backends that advertise richer metadata under vendor-specific fields
/// are served by their own specialized adapters rather than by widening this one.
/// </remarks>
sealed class OpenAiProvider : OpenAiCompatibleProvider, IProviderDescriptorSource
{
	/// <summary>
	/// Initializes a new instance of the <see cref="OpenAiProvider"/> class.
	/// </summary>
	/// <param name="httpClientProvider">Supplies the pre-configured per-backend HTTP clients.</param>
	/// <param name="capabilityProber">Actively probes a backend when a model carries no metadata capabilities.</param>
	/// <param name="timeProvider">The clock used for response timestamps and duration measurement.</param>
	/// <param name="options">The validated proxy options carrying each backend's reasoning default.</param>
	/// <param name="traceAccessor">Provides the ambient request trace for provenance recording.</param>
	/// <param name="reasoningDetailsCache">Carries opaque <c>reasoning_details</c> blobs across a tool-call conversation.</param>
	/// <param name="logger">The logger used to surface capabilities that stayed inconclusive after probing.</param>
	public OpenAiProvider(
		IBackendHttpClientProvider httpClientProvider,
		ICapabilityProber          capabilityProber,
		TimeProvider               timeProvider,
		IOptions<ProxyOptions>     options,
		IRequestTraceAccessor      traceAccessor,
		IReasoningDetailsCache     reasoningDetailsCache,
		ILogger<OpenAiProvider>    logger)
		: base(
			httpClientProvider,
			capabilityProber,
			timeProvider,
			options,
			traceAccessor,
			reasoningDetailsCache,
			logger) { }

	/// <summary>
	/// The OpenAI provider's self-describing metadata. OpenAI's <c>GET /v1/models</c> reports no capability
	/// metadata, so a freshly added backend defaults to the conservative <see cref="OperatingMode.Explicit"/>
	/// (the operator pins the models they want rather than auto-exposing an unreliable surface) and the UI
	/// prefills the canonical public endpoint.
	/// </summary>
	public static ProviderDescriptor Descriptor { get; } = new(
		"openai",
		"OpenAI",
		OperatingMode.Explicit,
		"https://api.openai.com/v1");

	/// <inheritdoc/>
	public override string ProviderType => Descriptor.ProviderType;

	/// <inheritdoc/>
	public override Task<IReadOnlyList<DiscoveredModel>> DiscoverModelsAsync(
		BackendContext    backend,
		CancellationToken cancellationToken) => DiscoverModelsCoreAsync<OpenAiDiscoveryModel>(
		backend,
		ProjectDiscoveredModel,
		cancellationToken);

	/// <summary>
	/// Projects an official-OpenAI model entry onto the neutral <see cref="DiscoveredModel"/>. Only the
	/// stable OpenAI fields are read; context length and capability metadata are deliberately left
	/// unset because the official schema does not expose them.
	/// </summary>
	/// <param name="model">The OpenAI model entry to project.</param>
	/// <returns>The provider-neutral discovered model, with no context length or capability metadata.</returns>
	private static DiscoveredModel ProjectDiscoveredModel(OpenAiDiscoveryModel model) => new(
		model.Id,
		model.Created is { } ts ? DateTimeOffset.FromUnixTimeSeconds(ts) : null);
}
