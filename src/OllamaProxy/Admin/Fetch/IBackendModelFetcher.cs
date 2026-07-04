// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Configuration;
using OllamaProxy.Core;

namespace OllamaProxy.Admin.Fetch;

/// <summary>
/// Fetches a single backend's models for the admin surface, capturing the outcome (success or a classified
/// failure) as a <see cref="BackendFetchResult"/>. It discovers against the <see cref="BackendOptions"/> handed
/// to it directly rather than any previously committed configuration. So the admin surface always sees the
/// current on-disk definition of a backend, including unsaved edits previewed before commit. The caller chooses
/// the <see cref="DiscoveryProbePolicy"/>. <see cref="DiscoveryProbePolicy.NeverProbe"/> gives a fast,
/// non-blocking page load that surfaces only the capabilities a provider already lists.
/// <see cref="DiscoveryProbePolicy.ProbeAll"/> runs an explicit, operator-triggered enrichment that probes every
/// model regardless of its context window.
/// </summary>
/// <remarks>
/// Both methods classify a failure only as far as it can be attributed honestly (see
/// <see cref="BackendFetchErrorKind"/>), but they report it differently. <see cref="FetchAsync"/> captures a
/// reachable-but-failing backend as data. Any upstream or transport failure becomes a
/// <see cref="BackendFetchResult"/> with <see cref="BackendFetchResult.Succeeded"/> cleared, so a caller
/// fetching several backends can render the ones that answered alongside the ones that did not.
/// <see cref="FetchStreamingAsync"/> cannot fold a failure into a result once it has begun yielding, so it
/// surfaces the same classified failure as a thrown <see cref="BackendFetchException"/> (see its own remarks).
/// Two conditions are never turned into a failure and <b>do</b> propagate from both methods: a cancellation
/// through the supplied token (surfaced as <see cref="OperationCanceledException"/> rather than swallowed) and an
/// argument-guard violation.
/// </remarks>
interface IBackendModelFetcher
{
	/// <summary>
	/// Fetches and resolves the models offered by one backend, returning the outcome as a
	/// <see cref="BackendFetchResult"/>.
	/// </summary>
	/// <param name="backendName">
	/// The configured backend name, used to attribute the result; carried through to both a success and a
	/// failure result so the admin surface can label it.
	/// </param>
	/// <param name="backend">
	/// The backend options to discover against. These are used directly (base address, credentials, and probing
	/// settings all come from here), so the fetch reflects exactly this configuration, committed or draft.
	/// </param>
	/// <param name="probePolicy">
	/// Whether, and for which models, the discovery actively probes capabilities.
	/// <see cref="DiscoveryProbePolicy.NeverProbe"/> issues no probe (the fast page-load default);
	/// <see cref="DiscoveryProbePolicy.ProbeAll"/> probes every model (the explicit enrichment action).
	/// </param>
	/// <param name="cancellationToken">A token to cancel the operation.</param>
	/// <returns>
	/// A task whose result is the fetch outcome: a success carrying the resolved models, or a failure carrying
	/// the classified error and its message. Never <see langword="null"/>.
	/// </returns>
	/// <exception cref="ArgumentNullException"><paramref name="backend"/> is <see langword="null"/>.</exception>
	/// <exception cref="ArgumentException">
	/// <paramref name="backendName"/> is <see langword="null"/>, empty, or white-space.
	/// </exception>
	Task<BackendFetchResult> FetchAsync(
		string               backendName,
		BackendOptions       backend,
		DiscoveryProbePolicy probePolicy,
		CancellationToken    cancellationToken);

	/// <summary>
	/// The streaming counterpart of <see cref="FetchAsync"/>: discovers and resolves one backend's models, but
	/// <em>yields</em> each resolved <see cref="DiscoveryCandidate"/> in client-name order (the same order the
	/// admin table sorts on) rather than buffering the whole batch. This lets the admin surface fill in the rows
	/// top-to-bottom with a live progress indicator. That is exactly what an operator-triggered
	/// <see cref="DiscoveryProbePolicy.ProbeAll"/> probe of a cold-loading backend needs. Models below the current
	/// row keep probing concurrently, so a slow one delays only the rows beneath it.
	/// </summary>
	/// <param name="backendName">
	/// The configured backend name, used only to attribute a failure; carried into the
	/// <see cref="BackendFetchException"/> a fault throws so the admin surface can label it.
	/// </param>
	/// <param name="backend">
	/// The backend options to discover against. These are used directly (base address, credentials, and probing
	/// settings all come from here), so the fetch reflects exactly this configuration, committed or draft.
	/// </param>
	/// <param name="probePolicy">
	/// Whether, and for which models, the discovery actively probes capabilities. The streaming path exists for
	/// the <see cref="DiscoveryProbePolicy.ProbeAll"/> enrichment, though any policy is accepted.
	/// </param>
	/// <param name="cancellationToken">A token to cancel discovery and probing mid-stream.</param>
	/// <returns>
	/// An asynchronous sequence of resolved candidates in client-name order (the model id with the backend prefix
	/// applied, compared case-insensitively), matching the admin surface's table sort.
	/// </returns>
	/// <exception cref="ArgumentNullException"><paramref name="backend"/> is <see langword="null"/>.</exception>
	/// <exception cref="ArgumentException">
	/// <paramref name="backendName"/> is <see langword="null"/>, empty, or white-space.
	/// </exception>
	/// <exception cref="BackendFetchException">
	/// The backend listing or a model's capability resolution failed; the exception carries the classified kind.
	/// </exception>
	/// <remarks>
	/// Unlike <see cref="FetchAsync"/>, a failure is <b>not</b> captured as a result: a stream that has already
	/// yielded candidates cannot retroactively become a failure value. Instead the enumeration throws a
	/// <see cref="BackendFetchException"/> carrying the same honest <see cref="BackendFetchErrorKind"/>
	/// classification, and candidates yielded before the fault stay valid. A caller-requested cancellation
	/// surfaces as <see cref="OperationCanceledException"/>, distinct from a backend failure, exactly as in
	/// <see cref="FetchAsync"/>.
	/// </remarks>
	IAsyncEnumerable<DiscoveryCandidate> FetchStreamingAsync(
		string               backendName,
		BackendOptions       backend,
		DiscoveryProbePolicy probePolicy,
		CancellationToken    cancellationToken);
}
