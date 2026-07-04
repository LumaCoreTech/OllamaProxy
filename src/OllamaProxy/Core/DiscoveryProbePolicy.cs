// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Core;

/// <summary>
/// Controls whether <see cref="IBackendModelDiscovery"/> probes the capabilities of a discovered model. A
/// capability probe is an active round trip against the backend, used only when a provider's model listing
/// carries no capability metadata. Capabilities and context length are independent facts about a model, so this
/// policy lets each consumer trade probe latency against completeness for its own scenario.
/// </summary>
public enum DiscoveryProbePolicy
{
	/// <summary>
	/// Skip capability probing for a model with no resolvable context window, but still probe models that have
	/// one. Used by the startup <see cref="ModelCatalogBuilder"/>, which drops a window-less model during the
	/// merge anyway, so probing it would waste upstream round trips. A skipped model's
	/// <see cref="DiscoveryCandidate"/> carries <see langword="null"/> capabilities.
	/// </summary>
	SkipContextless,

	/// <summary>
	/// Probe every model regardless of its context window. Used by the manual admin "probe capabilities"
	/// action, which the operator triggers on demand and accepts the latency for, to report the true
	/// capabilities of every model the backend offers, even ones still missing a context length.
	/// </summary>
	ProbeAll,

	/// <summary>
	/// Never probe. Capabilities are taken verbatim from the provider's model listing when it supplies them
	/// (metadata-rich providers such as Venice and OpenRouter), and left <see langword="null"/> otherwise
	/// (metadata-poor providers such as OpenAI and vLLM). Used as the default admin fetch so the page loads
	/// without blocking on any upstream probe; the operator can then trigger <see cref="ProbeAll"/> explicitly
	/// for the backends whose capabilities are still unknown.
	/// </summary>
	NeverProbe
}
