// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Core;

/// <summary>
/// A discovered model after the proxy's naming and capability-resolution rules have been applied: the
/// resolved counterpart to the raw <see cref="DiscoveredModel"/> a provider returns. Produced by
/// <see cref="IBackendModelDiscovery"/> and consumed both by the startup <see cref="ModelCatalogBuilder"/>,
/// which merges it into the runtime catalog, and the admin reconciliation surface, which previews it against
/// existing registry pins. The context window is carried <em>raw</em> (see <see cref="ReportedContextLength"/>):
/// naming and capabilities are resolved here, but the effective window is left to each consumer so the admin
/// surface can show the honest backend value while the catalog applies the configured fallback. Carrying the
/// resolved shape rather than the raw <see cref="DiscoveredModel"/> is what keeps the runtime catalog and
/// the admin preview from drifting in how a model is named and described.
/// </summary>
/// <param name="ClientName">The client-facing model name, after any backend prefix was applied.</param>
/// <param name="UpstreamModel">The upstream model identifier to request from the backend.</param>
/// <param name="ReportedContextLength">
/// The context window in tokens exactly as the backend reported it during discovery, or
/// <see langword="null"/> when the backend advertised none. This is the <em>raw</em> backend value. It is
/// deliberately not combined with any configured backend default, so a consumer that needs the honest backend
/// view (the admin backend table) sees the truth while a consumer that needs the client-facing effective
/// window (the <see cref="ModelCatalogBuilder"/>) applies the fallback itself via
/// <see cref="ModelExposureRules.ResolveEffectiveContextWindow"/>.
/// </param>
/// <param name="Capabilities">
/// The resolved capabilities (metadata first, then optional probing, then the conservative default), or
/// <see langword="null"/> when capability resolution was deliberately skipped. Whether a model with a
/// <see langword="null"/> <see cref="ReportedContextLength"/> still carries capabilities depends on the
/// <see cref="DiscoveryProbePolicy"/> the discovery ran under: under
/// <see cref="DiscoveryProbePolicy.SkipContextless"/> such a model is left unprobed, so this is also
/// <see langword="null"/>; under <see cref="DiscoveryProbePolicy.ProbeAll"/> it is probed regardless, so the
/// capabilities are present even though the context window is not; under
/// <see cref="DiscoveryProbePolicy.NeverProbe"/> no probe runs at all, so this carries the provider listing's
/// own metadata when present and is <see langword="null"/> otherwise. Capabilities and context length are
/// independent facts about a model, so the two <see langword="null"/> states are not coupled.
/// </param>
/// <param name="CreatedAtUtc">
/// The UTC timestamp when the backend listed this model (Unix epoch seconds converted to
/// <see cref="DateTimeOffset"/>), carried verbatim from the raw <see cref="DiscoveredModel"/>, or
/// <see langword="null"/> when the backend reported no creation time. This is the backend's listing
/// date, not necessarily the model's original release date.
/// </param>
/// <param name="Metadata">
/// Optional descriptive metadata (display name, description, tokenizer, quantization, pricing, …) the backend
/// published for the model, carried verbatim from the raw <see cref="DiscoveredModel"/>, or
/// <see langword="null"/> when the backend reported none. It never affects naming, capability resolution, or
/// the context window; it flows through purely so the admin surface can show the richest honest picture.
/// </param>
public sealed record DiscoveryCandidate(
	string                 ClientName,
	string                 UpstreamModel,
	long?                  ReportedContextLength,
	ModelCapabilities?     Capabilities,
	DateTimeOffset?        CreatedAtUtc = null,
	ProviderModelMetadata? Metadata     = null);
