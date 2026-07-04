// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Core;

namespace OllamaProxy.Admin.Ui.Components.Backends;

/// <summary>
/// The model-list state cached per backend while its card is open: the raw fetched snapshot, the in-flight
/// flags (IsFetching, IsStreaming), the running probe count, the last error message, and the per-probe
/// cancellation source. Lifted out of <c>Backends.razor</c> so the <see cref="BackendModels"/> component can
/// take it as a parameter without depending on the page's nested private type.
/// </summary>
/// <param name="Snapshot">
/// The raw discovered candidates the backend last reported, or <see langword="null"/> before the first
/// successful fetch (or after a failed one). Cached raw so every draft mutation re-reconciles it locally.
/// </param>
/// <param name="IsFetching">Whether a fetch (refresh or probe) is currently running for this backend.</param>
/// <param name="Error">
/// The last fetch's failure message, or <see langword="null"/> when the last fetch succeeded or none has run.
/// </param>
public sealed record ModelListState(
	IReadOnlyList<DiscoveryCandidate>? Snapshot,
	bool                               IsFetching,
	string?                            Error)
{
	/// <summary>
	/// Gets whether a streaming capability probe is currently filling this backend's snapshot model-by-model.
	/// It is set for the duration of a <see cref="DiscoveryProbePolicy.ProbeAll"/> probe (never for a plain
	/// refresh) and drives the progress banner, the Cancel button, and the "Probing…" button label.
	/// Distinct from <see cref="IsFetching"/>, which is true for both a refresh and a probe.
	/// </summary>
	public bool IsStreaming { get; init; }

	/// <summary>
	/// Gets the running count of candidates a streaming probe has resolved so far, shown in the progress
	/// banner. It equals the <see cref="Snapshot"/> count during a stream and is meaningless once
	/// <see cref="IsStreaming"/> clears, so the banner that reads it renders only while streaming.
	/// </summary>
	public int ProbedCount { get; init; }

	/// <summary>
	/// Gets the cancellation source for an in-flight streaming probe, or <see langword="null"/> when none is
	/// running. The Cancel button signals it; it is linked to the page's load scope so a circuit teardown
	/// cancels it too. The owning fetch disposes it when the stream ends.
	/// </summary>
	public CancellationTokenSource? ProbeCts { get; init; }

	/// <summary>
	/// Gets the initial state for a backend whose card has not been expanded yet: no snapshot, not fetching,
	/// no error. It renders as "No models loaded yet." until the first fetch populates it.
	/// </summary>
	public static ModelListState Empty { get; } = new(Snapshot: null, IsFetching: false, Error: null);
}
