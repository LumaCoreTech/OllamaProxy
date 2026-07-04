// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Runtime.CompilerServices;

using Microsoft.Extensions.Options;

using OllamaProxy.Admin.Config;
using OllamaProxy.Admin.Editing;
using OllamaProxy.Admin.Fetch;
using OllamaProxy.Configuration;
using OllamaProxy.Core;

namespace OllamaProxy.Admin;

/// <summary>
/// The default <see cref="IAdminModelService"/>. On each call it reads the current <see cref="ProxyOptions"/>
/// snapshot through <see cref="IOptionsMonitor{TOptions}"/>, so every operation reflects the live on-disk
/// configuration the chassis watches for changes. It then does one of three things: fetch a single draft
/// backend's models through <see cref="IBackendModelFetcher"/> for local reconciliation, project the
/// configuration into an editable draft, or materialize an edited draft and recycle the inner host onto it
/// through <see cref="IProxyConfigApplier"/>.
/// </summary>
/// <remarks>
/// Failure isolation governs the fetch path: a draft backend whose fetch fails is captured as a failure snapshot
/// rather than allowed to throw, so an unreachable backend never blanks the editor's model list. The only thing
/// that aborts a fetch is a cancellation through the supplied token, which the fetcher propagates and this service
/// lets surface. The fetch's probe policy is the caller's choice (<see cref="DiscoveryProbePolicy.NeverProbe"/>
/// for a fast refresh, <see cref="DiscoveryProbePolicy.ProbeAll"/> for an on-demand capability probe). The apply
/// path is whole-section authoritative and transactional: the materialized desired state replaces the entire
/// section, and a rejected or failed apply leaves the in-memory options exactly as they were. The type holds no
/// state beyond its injected collaborators and is safe to share as a singleton.
/// </remarks>
sealed class AdminModelService : IAdminModelService
{
	private readonly IOptionsMonitor<ProxyOptions> mOptions;
	private readonly IBackendModelFetcher          mFetcher;
	private readonly IProxyConfigApplier           mApplier;

	/// <summary>
	/// Initializes a new instance of the <see cref="AdminModelService"/> class.
	/// </summary>
	/// <param name="options">Supplies the current proxy options snapshot on each view build.</param>
	/// <param name="fetcher">Fetches and resolves one backend's models, isolating its failures.</param>
	/// <param name="applier">Persists a desired configuration and recycles the inner host onto it.</param>
	/// <exception cref="ArgumentNullException">
	/// <paramref name="options"/>, <paramref name="fetcher"/>, or <paramref name="applier"/> is
	/// <see langword="null"/>.
	/// </exception>
	public AdminModelService(
		IOptionsMonitor<ProxyOptions> options,
		IBackendModelFetcher          fetcher,
		IProxyConfigApplier           applier)
	{
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(fetcher);
		ArgumentNullException.ThrowIfNull(applier);

		mOptions = options;
		mFetcher = fetcher;
		mApplier = applier;
	}

	/// <inheritdoc/>
	public async Task<DraftModelSnapshot> FetchDraftSnapshotAsync(
		DesiredBackend       draft,
		DiscoveryProbePolicy probePolicy,
		CancellationToken    cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(draft);

		// Materialize this backend's options and its cosmetic fetch name exactly as a streaming probe would, so
		// the two fetch paths resolve the write-only key and attribute failures identically.
		(BackendOptions backend, string fetchName) = MaterializeForFetch(draft);

		// The probe policy is the caller's choice: NeverProbe for a fast refresh that surfaces only the
		// capabilities the provider already lists, or ProbeAll for an on-demand enrichment that probes every
		// model. ProbeAll is the path a capability-poor backend (e.g. strict OpenAI) needs to resolve its
		// capabilities.
		BackendFetchResult fetch = await mFetcher
			                           .FetchAsync(fetchName, backend, probePolicy, cancellationToken)
			                           .ConfigureAwait(false);

		// Hand back the raw snapshot for local reconciliation rather than reconciling here: the editor re-runs
		// ModelReconciler.ReconcileBackend against the live draft as the operator pins, unpins, or switches mode,
		// so a single fetch serves every subsequent mutation without another round-trip.
		return fetch.Succeeded
			       ? DraftModelSnapshot.Success(fetch.Models!)
			       : DraftModelSnapshot.FromFailedFetch(fetch);
	}

	/// <inheritdoc/>
	public async IAsyncEnumerable<DiscoveryCandidate> ProbeDraftStreamingAsync(
		DesiredBackend                             draft,
		DiscoveryProbePolicy                       probePolicy,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(draft);

		// Materialize identically to the buffered fetch (same write-only key recovery, same cosmetic name), so a
		// streaming probe and a refresh authenticate and attribute failures the same way; only the shape differs.
		(BackendOptions backend, string fetchName) = MaterializeForFetch(draft);

		// Forward the stream verbatim: each candidate flows to the editor the instant its probe settles, so a
		// slow-loading model never holds back the fast ones. A fault surfaces as a BackendFetchException and a
		// caller cancellation as OperationCanceledException, both straight from the fetcher, unwrapped here.
		await foreach (DiscoveryCandidate candidate in mFetcher
			               .FetchStreamingAsync(fetchName, backend, probePolicy, cancellationToken)
			               .ConfigureAwait(false))
		{
			yield return candidate;
		}
	}

	/// <summary>
	/// Materializes a draft backend into the concrete <see cref="BackendOptions"/> a fetch discovers against, plus
	/// the cosmetic name a fetch failure is attributed to. Shared by the buffered and streaming fetch paths so
	/// both resolve the draft's write-only API key (recovered from the live snapshot by
	/// <see cref="DesiredBackend.OriginalName"/>) and derive the fetch name identically.
	/// </summary>
	/// <param name="draft">The draft backend to materialize.</param>
	/// <returns>The materialized options and the cosmetic fetch name.</returns>
	private (BackendOptions Backend, string FetchName) MaterializeForFetch(DesiredBackend draft)
	{
		// Snapshot the live configuration once so the draft's write-only key resolves exactly as a commit would:
		// a blank key is recovered from the snapshot by OriginalName, so the fetch authenticates with the saved
		// secret without the browser ever holding it. The snapshot is read-only here.
		ProxyOptions options = mOptions.CurrentValue;

		// Materialize just this backend: it copies every edited option forward and resolves the secret. The
		// draft's name plays no part in materialization (only the options and key matter), so an unnamed or
		// renamed draft materializes the same way.
		BackendOptions backend = DesiredStateMaterializer.MaterializeBackend(draft, options.Backends);

		// The backend name is cosmetic for a snapshot fetch: it only attributes the fetcher's failure result and
		// satisfies the fetcher's non-blank-name guard. A new backend may not be named yet, so fall back to its
		// pre-rename identity and then to a placeholder. The editor reconciles the returned snapshot against the
		// draft locally, so this name never reaches the rendered rows.
		string fetchName = FirstNonBlank(draft.Name, draft.OriginalName) ?? "(unnamed backend)";

		return (backend, fetchName);
	}

	/// <inheritdoc/>
	public Task<DesiredProxyState> GetEditableStateAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		// Snapshot the live configuration once and project it into an editable draft. Dematerialize deep-copies
		// every mutable member and blanks each API key, so the returned draft is a standalone working set the
		// editor can bind to without ever mutating the running proxy's in-memory options. The projection is pure
		// CPU work with no I/O, so it completes synchronously. The Task-returning signature keeps the load path
		// symmetric with ApplyDesiredStateAsync and free to grow an awaited step later.
		ProxyOptions options = mOptions.CurrentValue;

		return Task.FromResult(DesiredStateMaterializer.Dematerialize(options));
	}

	/// <inheritdoc/>
	public Task<ApplyResult> ApplyDesiredStateAsync(
		DesiredProxyState desiredState,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(desiredState);

		// Snapshot the live configuration once so the write-only key recovery sees a single, consistent read: a
		// backend whose draft left its key blank keeps its saved secret, recovered by OriginalName so a rename
		// does not drop it. The snapshot is read-only here (the materializer builds a fresh ProxyOptions), so a
		// rejected or failed apply leaves the in-memory options exactly as they were.
		ProxyOptions options = mOptions.CurrentValue;

		// Materialize the editor draft into the authoritative desired state. The only validation this performs is
		// structural (blank or duplicate backend name); every domain rule is left to the recycle's dry-run.
		ProxyOptions materialized = DesiredStateMaterializer.Materialize(desiredState, options.Backends);

		return mApplier.ApplyAsync(materialized, cancellationToken);
	}

	/// <summary>
	/// Returns the first of <paramref name="primary"/> and <paramref name="fallback"/> that is neither
	/// <see langword="null"/> nor white-space, or <see langword="null"/> when both are blank. Used to derive a
	/// cosmetic display name for a draft backend that may not be named yet.
	/// </summary>
	/// <param name="primary">The preferred value (the draft's current name).</param>
	/// <param name="fallback">The fallback value (the draft's pre-rename identity).</param>
	/// <returns>The first non-blank value, or <see langword="null"/> when both are blank.</returns>
	private static string? FirstNonBlank(string? primary, string? fallback)
	{
		if (!string.IsNullOrWhiteSpace(primary)) return primary;
		if (!string.IsNullOrWhiteSpace(fallback)) return fallback;

		return null;
	}
}
