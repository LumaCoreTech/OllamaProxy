// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Configuration;

namespace OllamaProxy.Core;

/// <summary>
/// Discovers a backend's models and resolves them into <see cref="DiscoveryCandidate"/> values: the single
/// shared seam between raw provider discovery and the proxy's two consumers: the startup catalog builder and
/// the admin reconciliation surface. Centralizing the discover-then-resolve orchestration here keeps the
/// runtime catalog and the admin preview from drifting in how they name models, resolve context windows, and
/// determine capabilities.
/// </summary>
interface IBackendModelDiscovery
{
	/// <summary>
	/// Discovers the models a backend offers and resolves each one into a <see cref="DiscoveryCandidate"/>,
	/// applying the proxy's naming and context-window rules and resolving capabilities (metadata first, then
	/// optional probing), returning them in the backend's reported order. The per-model capability resolution
	/// runs concurrently, bounded by the backend's <see cref="CapabilityProbingOptions.MaxConcurrentProbes"/>,
	/// so a backend reporting many models does not flood itself with simultaneous probe requests.
	/// </summary>
	/// <param name="resolved">The resolved adapter and context to discover against (committed or draft).</param>
	/// <param name="backend">
	/// The backend options supplying the model prefix, the context-length default, and the concurrent-probe
	/// bound. For a committed backend this is the configured entry; for a draft it is the inline draft options.
	/// </param>
	/// <param name="probePolicy">
	/// Whether to probe a model whose effective context window could not be resolved.
	/// </param>
	/// <param name="cancellationToken">A token to cancel discovery and probing.</param>
	/// <returns>The resolved candidates in the backend's reported order.</returns>
	/// <exception cref="ArgumentNullException">
	/// <paramref name="resolved"/> or <paramref name="backend"/> is <see langword="null"/>.
	/// </exception>
	/// <remarks>
	/// Unlike the startup catalog path, this method does <b>not</b> absorb discovery failures: any exception
	/// from the backend listing or capability resolution propagates to the caller, which decides whether to
	/// skip the backend (the catalog logs and continues) or surface the error (the admin fetch renders it). The
	/// same <see cref="ResolvedBackend"/> shape serves a committed backend and a draft one, so the admin
	/// preview-before-commit flow reuses this exact path.
	/// </remarks>
	Task<IReadOnlyList<DiscoveryCandidate>> DiscoverAsync(
		ResolvedBackend      resolved,
		BackendOptions       backend,
		DiscoveryProbePolicy probePolicy,
		CancellationToken    cancellationToken);

	/// <summary>
	/// The streaming counterpart to <see cref="DiscoverAsync"/>: discovers the models a backend offers and
	/// yields each resolved <see cref="DiscoveryCandidate"/> in client-name order, the same order the admin
	/// table sorts on. Models are still resolved concurrently, bounded by the backend's
	/// <see cref="CapabilityProbingOptions.MaxConcurrentProbes"/>: every model's resolution starts eagerly and
	/// they are awaited in order, so a slow-loading model delays only the rows beneath it. This lets the admin
	/// surface fill in each model's capabilities incrementally with a live progress indicator instead of blocking
	/// on the whole batch.
	/// </summary>
	/// <param name="resolved">The resolved adapter and context to discover against (committed or draft).</param>
	/// <param name="backend">
	/// The backend options supplying the model prefix, the context-length default, and the concurrent-probe
	/// bound. For a committed backend this is the configured entry; for a draft it is the inline draft options.
	/// </param>
	/// <param name="probePolicy">
	/// Whether, and for which models, the discovery actively probes capabilities. The on-demand admin probe
	/// uses <see cref="DiscoveryProbePolicy.ProbeAll"/> so every model is resolved.
	/// </param>
	/// <param name="cancellationToken">A token to cancel discovery and probing mid-stream.</param>
	/// <returns>
	/// An asynchronous sequence of resolved candidates in client-name order (the model id with the backend
	/// prefix applied, compared case-insensitively), matching the admin surface's table sort.
	/// </returns>
	/// <exception cref="ArgumentNullException">
	/// <paramref name="resolved"/> or <paramref name="backend"/> is <see langword="null"/>.
	/// </exception>
	/// <remarks>
	/// Like <see cref="DiscoverAsync"/>, this does <b>not</b> absorb discovery failures: a fault from the backend
	/// listing or any model's capability resolution surfaces from the enumeration (the first awaited resolution
	/// that faults completes the stream with that exception), so the admin fetch can render it. Candidates already
	/// yielded before the fault remain valid. When the enumeration ends early (by that fault or by the consumer
	/// breaking out of the loop) any still-running probes are cancelled so they stop hitting the backend.
	/// </remarks>
	IAsyncEnumerable<DiscoveryCandidate> DiscoverStreamingAsync(
		ResolvedBackend      resolved,
		BackendOptions       backend,
		DiscoveryProbePolicy probePolicy,
		CancellationToken    cancellationToken);
}
