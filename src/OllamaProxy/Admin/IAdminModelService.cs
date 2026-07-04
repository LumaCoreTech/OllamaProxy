// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Admin.Config;
using OllamaProxy.Admin.Editing;
using OllamaProxy.Core;

namespace OllamaProxy.Admin;

/// <summary>
/// The admin surface's editing service: loads the current proxy configuration as an editable draft, fetches
/// individual draft backends' model snapshots for local reconciliation, and applies the complete edited state as
/// a single transactional commit. That edited state covers backends added, removed, renamed, or reconfigured,
/// plus model registries and request-tracing diagnostics.
/// </summary>
interface IAdminModelService
{
	/// <summary>
	/// Fetches the raw model snapshot a single <em>draft</em> backend currently offers (the backend exactly as the
	/// operator is editing it, before it is committed), so the editor can reconcile it locally without a fetch per
	/// click. Returns the unreconciled candidates on success, capturing a fetch failure as a failure snapshot
	/// rather than throwing.
	/// </summary>
	/// <param name="draft">
	/// The draft backend to fetch. Its blank API key is resolved against the current snapshot by
	/// <see cref="Editing.DesiredBackend.OriginalName"/>.
	/// </param>
	/// <param name="probePolicy">
	/// Whether, and for which models, discovery actively probes capabilities.
	/// <see cref="Core.DiscoveryProbePolicy.NeverProbe"/> for a fast refresh;
	/// <see cref="Core.DiscoveryProbePolicy.ProbeAll"/> for an on-demand capability probe.
	/// </param>
	/// <param name="cancellationToken">A token to cancel the operation.</param>
	/// <returns>
	/// A task whose result is the draft's raw snapshot: a success carrying the resolved candidates, or a captured
	/// failure when the draft's settings could not fetch. Never <see langword="null"/>.
	/// </returns>
	/// <exception cref="ArgumentNullException"><paramref name="draft"/> is <see langword="null"/>.</exception>
	/// <remarks>
	///     <para>
	///     This is the editor's model-list data source: it discovers against the draft's own options (base
	///     address, provider, probing) so an operator sees the effect of an edit (a changed URL, a new provider)
	///     without committing first. It returns the <em>raw</em> snapshot rather than a reconciled view, because
	///     the editor reconciles locally through <see cref="Reconciliation.ModelReconciler.ReconcileBackend"/>:
	///     pinning, unpinning, or switching the draft's mode re-reconciles the same cached snapshot, so those are
	///     pure draft mutations the operator applies as a whole on commit.
	///     </para>
	///     <para>
	///     <b>The probe policy is the caller's choice.</b> A <em>refresh</em> uses
	///     <see cref="Core.DiscoveryProbePolicy.NeverProbe"/> for a fast, non-blocking fetch that surfaces only
	///     the capabilities the provider already lists; a <em>probe</em> uses
	///     <see cref="Core.DiscoveryProbePolicy.ProbeAll"/> to actively resolve every model's capabilities, which
	///     is the on-demand enrichment for capability-poor backends (for example a strict OpenAI endpoint that
	///     lists no capability metadata). Both run against the draft, so they reflect unsaved connection edits.
	///     </para>
	///     <para>
	///     <b>Write-only secret.</b> The draft's blank API key is resolved exactly as a commit would: recovered
	///     from the live configuration snapshot by the backend's <see cref="Editing.DesiredBackend.OriginalName"/>,
	///     so the fetch authenticates with the saved secret without the browser ever holding it. A newly added
	///     backend left blank fetches with no key and typically surfaces an authentication failure, the honest
	///     preview of a missing secret.
	///     </para>
	/// </remarks>
	Task<DraftModelSnapshot> FetchDraftSnapshotAsync(
		DesiredBackend       draft,
		DiscoveryProbePolicy probePolicy,
		CancellationToken    cancellationToken);

	/// <summary>
	/// The streaming counterpart of <see cref="FetchDraftSnapshotAsync"/>: discovers a single <em>draft</em>
	/// backend's models and <em>yields</em> each resolved candidate in client-name order (the same order the
	/// editor's table sorts on) rather than buffering the whole snapshot. It exists for the on-demand
	/// <see cref="Core.DiscoveryProbePolicy.ProbeAll"/> probe, where a backend with several slow-loading models
	/// would otherwise leave the editor blocked on the whole batch. Here the rows fill in top-to-bottom as each
	/// model's probe answers (later models keep probing concurrently), and the editor can show live progress and
	/// a running count.
	/// </summary>
	/// <param name="draft">
	/// The draft backend to probe. Its blank API key is resolved against the current snapshot by
	/// <see cref="Editing.DesiredBackend.OriginalName"/>.
	/// </param>
	/// <param name="probePolicy">
	/// Whether, and for which models, discovery actively probes capabilities. The streaming path exists for
	/// <see cref="Core.DiscoveryProbePolicy.ProbeAll"/>, though any policy is accepted.
	/// </param>
	/// <param name="cancellationToken">A token to cancel the operation mid-stream.</param>
	/// <returns>An asynchronous sequence of resolved candidates in client-name order.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="draft"/> is <see langword="null"/>.</exception>
	/// <exception cref="Fetch.BackendFetchException">
	/// The backend listing or a model's capability resolution failed; the exception carries the classified kind.
	/// </exception>
	/// <remarks>
	///     <para>
	///     Materialization is identical to <see cref="FetchDraftSnapshotAsync"/> (write-only key resolved by
	///     <see cref="Editing.DesiredBackend.OriginalName"/>, discovery against the draft's unsaved settings).
	///     Candidates are yielded in client-name order (the model id with the backend prefix applied, compared
	///     case-insensitively), matching the editor's table sort.
	///     </para>
	///     <para>
	///     <b>Failures throw rather than capture.</b> Unlike <see cref="FetchDraftSnapshotAsync"/>, which returns
	///     a failure snapshot, a streaming probe that has already yielded rows cannot become a failure value, so
	///     a fault surfaces as a <see cref="Fetch.BackendFetchException"/> carrying the honest
	///     <see cref="Fetch.BackendFetchErrorKind"/>. Candidates yielded before the fault stay valid; a
	///     caller-requested cancellation surfaces as <see cref="OperationCanceledException"/>, distinct from a
	///     backend failure.
	///     </para>
	/// </remarks>
	IAsyncEnumerable<DiscoveryCandidate> ProbeDraftStreamingAsync(
		DesiredBackend       draft,
		DiscoveryProbePolicy probePolicy,
		CancellationToken    cancellationToken);

	/// <summary>
	/// Reads the current proxy configuration freshly and projects it into an editable draft (one backend per
	/// configured entry plus the request-tracing diagnostics), ready for the editor to bind to and modify. This
	/// is the load counterpart to <see cref="ApplyDesiredStateAsync"/>: load a draft here, edit it, then apply it
	/// back there.
	/// </summary>
	/// <param name="cancellationToken">A token to cancel the operation.</param>
	/// <returns>
	/// A task whose result is an editable draft mirroring the current configuration, with every API key blanked.
	/// Never <see langword="null"/>; its <see cref="Editing.DesiredProxyState.Backends"/> is empty only when no
	/// backends are configured.
	/// </returns>
	/// <remarks>
	///     <para>
	///     Reading freshly on each call, the draft reflects the live on-disk configuration (and the admin
	///     surface's own rewrites) without a proxy restart. Each backend's draft carries its current name as both
	///     its editable name and its <see cref="Editing.DesiredBackend.OriginalName"/>, so a rename in the editor
	///     can still recover the saved secret on apply.
	///     </para>
	///     <para>
	///     <b>Write-only secrets.</b> The returned draft never carries an API key: every backend's
	///     <see cref="Configuration.BackendOptions.ApiKey"/> is blank, the editor's "keep the saved secret"
	///     sentinel, so the secret never reaches the browser. Overwriting the field replaces the key on apply;
	///     leaving it blank keeps the existing one (recovered by <see cref="Editing.DesiredBackend.OriginalName"/>).
	///     </para>
	///     <para>
	///     <b>The draft is a standalone copy.</b> Every mutable member is deep-copied (each backend's probing
	///     settings and model registry, and the request tracing), so the editor's two-way binding mutates only
	///     the draft and never reaches back into the running proxy's in-memory configuration.
	///     </para>
	/// </remarks>
	Task<DesiredProxyState> GetEditableStateAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Applies a complete edited proxy configuration by materializing the editor draft into the authoritative
	/// <c>OllamaProxy</c> section and recycling the inner host onto it, returning the combined outcome. The edited
	/// configuration covers backends added, removed, renamed, or reconfigured, plus the model registries and
	/// request-tracing diagnostics.
	/// </summary>
	/// <param name="desiredState">
	/// The complete edited configuration to materialize and apply. Each backend's blank API key is resolved
	/// against the current snapshot by its <see cref="Editing.DesiredBackend.OriginalName"/>.
	/// </param>
	/// <param name="cancellationToken">A token to cancel the operation.</param>
	/// <returns>
	/// A task whose result describes whether the change went live, was rejected and rolled back, or could not be
	/// written. On any non-success outcome the previously active configuration remains live and on disk.
	/// </returns>
	/// <exception cref="ArgumentNullException"><paramref name="desiredState"/> is <see langword="null"/>.</exception>
	/// <exception cref="ArgumentException">
	/// A backend in <paramref name="desiredState"/> has a blank name, or two backends share a name (compared
	/// case-insensitively, as the routing layer keys them).
	/// </exception>
	/// <remarks>
	///     <para>
	///     The write is whole-section authoritative (see <see cref="Config.IProxyConfigWriter"/>): the entire
	///     section is replaced by the materialized desired state, so a backend the operator removed is genuinely
	///     gone rather than lingering from a previous version, while every sibling file section is preserved.
	///     </para>
	///     <para>
	///     <b>Write-only secrets.</b> The editor never receives a saved API key, so a backend whose
	///     <see cref="Editing.DesiredBackend.Options"/> carries a blank <see cref="Configuration.BackendOptions.ApiKey"/>
	///     keeps its existing secret: it is recovered from the current configuration snapshot by the backend's
	///     <see cref="Editing.DesiredBackend.OriginalName"/>, so a rename does not drop the key. A non-blank key
	///     replaces it; a newly added backend left blank stays blank and is rejected by the recycle's dry-run as
	///     a missing required secret.
	///     </para>
	///     <para>
	///     <b>API-key persistence is a deployment setting.</b> The active
	///     <see cref="Config.ApiKeyPersistencePolicy"/> is read from <see cref="Hosting.AdminOptions"/>, so every
	///     admin page that applies configuration uses the same behavior. It is not a per-apply operator choice.
	///     </para>
	///     <para>
	///     <b>Validation lives in the recycle.</b> Only two structural guards run here, both catching what the
	///     dry-run cannot: a blank or duplicate backend name (which would silently collide as a map key). Every
	///     domain rule (URL shape, provider support, key length, per-model rules) is enforced by the recycle's
	///     dry-run, and the outcome is transactional: either the new configuration is live and on disk
	///     (<see cref="ApplyOutcome.Applied"/>), or the previous one is (<see cref="ApplyOutcome.ValidationRejected"/>
	///     or <see cref="ApplyOutcome.WriteFailed"/>). The current options snapshot is never mutated.
	///     </para>
	/// </remarks>
	Task<ApplyResult> ApplyDesiredStateAsync(
		DesiredProxyState desiredState,
		CancellationToken cancellationToken);
}
