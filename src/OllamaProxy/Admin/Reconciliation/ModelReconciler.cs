// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Configuration;
using OllamaProxy.Core;

namespace OllamaProxy.Admin.Reconciliation;

/// <summary>
/// Reconciles one backend's existing registry pins against a freshly fetched model snapshot, producing the
/// <see cref="ReconciliationResult"/> the admin surface renders. Reconciliation is a pure function: no I/O, no
/// probing, and no mutation of the inputs. That keeps fetch and reconciliation separable. A caller fetches and
/// resolves a snapshot once (the costly, fallible part; see <see cref="IBackendModelDiscovery"/>) and
/// reconciles it as often as needed.
/// </summary>
/// <remarks>
///     <para>
///     The pins are the backend's <em>own</em> registry (<see cref="BackendOptions.Models"/>). Each entry already
///     belongs to this backend by position, so no cross-backend filtering is needed. The match key is the
///     <em>upstream</em> model identifier: a pin's <see cref="ModelRegistrationOptions.ResolveUpstreamModel"/>
///     matched ordinally (case-sensitive, since it is sent to the backend verbatim) against a snapshot
///     candidate's <see cref="DiscoveryCandidate.UpstreamModel"/>.
///     </para>
///     <para>
///     A pin is <see cref="ReconciledModelState.Available"/> when the snapshot still offers its upstream model, and
///     <see cref="ReconciledModelState.Unavailable"/> when it does not; pins are never dropped. Snapshot candidates
///     that no pin references become <see cref="ReconciledModelState.Discovered"/> rows.
///     </para>
///     <para>
///     The snapshot candidates arrive already named, sized, and capability-resolved by
///     <see cref="IBackendModelDiscovery"/>, using the same rules the runtime catalog applies, so their capabilities
///     and context window pass through verbatim. The candidate's unprefixed upstream id becomes the bare
///     <see cref="ReconciledModel.Name"/>, and the row's <see cref="ReconciledModel.ExposedName"/> is recomputed
///     from the backend's <em>current</em> <see cref="BackendOptions.ModelPrefix"/> applied to that id. Recomputing
///     it means a prefix edit updates discovered rows live on the next reconcile (exactly as it does for pins),
///     rather than freezing the candidate's snapshot-time name.
///     </para>
///     <para>
///     A <em>pin</em> resolves its own capabilities, context window, and exposed name (the bare
///     <see cref="ModelRegistrationOptions.Name"/> with the backend prefix applied) here, through
///     <see cref="ModelExposureRules"/>. For an available pin, the matching candidate's reported capabilities and
///     context window are also carried onto the row (<see cref="ReconciledModel.DiscoveredCapabilities"/>,
///     <see cref="ReconciledModel.DiscoveredContextLength"/>) so the presentation layer can surface
///     <see cref="ReconciledModel.IsDrifted">drift</see> without re-matching.
///     </para>
/// </remarks>
static class ModelReconciler
{
	/// <summary>
	/// Reconciles a backend against a snapshot, using a caller-supplied <em>effective operating mode</em> to decide
	/// whether its registry pins participate. This is the mode-aware entry point the admin surface uses. It is the
	/// single place that mirrors the runtime's PlugAndPlay rule, so the preview and the runtime catalog cannot
	/// drift in how they treat a PlugAndPlay backend's registry.
	/// </summary>
	/// <param name="backendName">The logical backend being reconciled.</param>
	/// <param name="backend">
	/// The backend's options, supplying the registry pins (when the mode honors them), the model prefix, and the
	/// context-length default.
	/// </param>
	/// <param name="effectiveMode">
	/// The backend's resolved operating mode, as produced by <c>IProviderCatalog.ResolveMode</c>. It decides
	/// whether the backend's registry pins participate (Hybrid/Explicit) or are ignored (PlugAndPlay).
	/// </param>
	/// <param name="snapshot">
	/// The freshly fetched and resolved candidates the backend currently offers, as produced by
	/// <see cref="IBackendModelDiscovery"/>.
	/// </param>
	/// <returns>The reconciled models and headline counts for the backend under its effective mode.</returns>
	/// <exception cref="ArgumentNullException">
	/// Any of <paramref name="backendName"/>, <paramref name="backend"/>, or <paramref name="snapshot"/> is
	/// <see langword="null"/>.
	/// </exception>
	/// <exception cref="ArgumentException"><paramref name="backendName"/> is empty or whitespace.</exception>
	/// <remarks>
	///     <para>
	///     In <see cref="OperatingMode.PlugAndPlay"/> the runtime catalog ignores the registry entirely (see
	///     <see cref="ModelCatalogBuilder"/>), so this reconciles against <em>no</em> pins. Every snapshot model
	///     becomes a <see cref="ReconciledModelState.Discovered"/> row, and the backend's stored pins are neither
	///     shown as <see cref="ReconciledModelState.Available"/>/<see cref="ReconciledModelState.Unavailable"/>
	///     nor dropped from the configuration. They simply have no effect, exactly as at runtime. In
	///     <see cref="OperatingMode.Hybrid"/> and <see cref="OperatingMode.Explicit"/> the backend's own
	///     <see cref="BackendOptions.Models"/> are reconciled normally. The caller resolves the effective mode
	///     through <c>IProviderCatalog.ResolveMode</c> (a backend that leaves it unset follows the provider-aware
	///     default) and passes it here, so that default lives with the providers rather than being recomputed in
	///     this pure helper.
	///     </para>
	///     <para>
	///     <see cref="OperatingMode.Explicit"/> additionally mirrors the runtime catalog's exposure rule. That
	///     mode exposes the registry alone: the catalog runs metadata-only discovery and never auto-exposes an
	///     Explicit backend's unpinned candidates. Every <see cref="ReconciledModelState.Discovered"/> row is
	///     therefore flagged <see cref="ReconciledModel.IsExposed">not-exposed</see>: it stays listed for the
	///     operator to promote, but the surface knows not to advertise an exposed name the proxy never serves.
	///     Hybrid and PlugAndPlay auto-expose their discovered models, so those rows stay exposed.
	///     </para>
	///     <para>
	///     The result interleaves Available pins and Discovered models in snapshot order, with Unavailable pins
	///     appended at the end. This ordering keeps a model in the same table position when toggling between
	///     pinned and unpinned, eliminating "jumping" on pin/unpin.
	///     </para>
	/// </remarks>
	public static ReconciliationResult ReconcileBackend(
		string                            backendName,
		BackendOptions                    backend,
		OperatingMode                     effectiveMode,
		IReadOnlyList<DiscoveryCandidate> snapshot)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(backendName);
		ArgumentNullException.ThrowIfNull(backend);
		ArgumentNullException.ThrowIfNull(snapshot);

		// PlugAndPlay ignores the registry at runtime (ModelCatalogBuilder skips it entirely), so the preview
		// must mirror that. Passing no pins makes every model reconcile as Discovered (exactly like the runtime
		// catalog) instead of showing registry entries as Available/Unavailable rows the proxy never honors.
		// Hybrid and Explicit reconcile against the backend's own registry.
		IReadOnlyList<ModelRegistrationOptions> pins = effectiveMode == OperatingMode.PlugAndPlay
			                                               ? []
			                                               : [.. backend.Models];

		ReconciliationResult result = Reconcile(backendName, backend, pins, snapshot);

		// Explicit mode exposes the registry alone: at runtime the catalog builder runs metadata-only discovery
		// for an Explicit backend and never auto-exposes its unpinned candidates. The preview must tell the same
		// truth, so a Discovered row under Explicit is flagged not-exposed: it stays listed for the operator to
		// promote, but the surface won't advertise an exposed name the proxy never serves. Hybrid and PlugAndPlay
		// auto-expose discovered models, and pins are always registered (Available or Unavailable). Every other
		// row therefore keeps the default IsExposed = true and the snapshot passes through untouched.
		if (effectiveMode != OperatingMode.Explicit)
		{
			return result;
		}

		List<ReconciledModel> exposureAdjusted = new(result.Models.Count);
		foreach (ReconciledModel model in result.Models)
		{
			exposureAdjusted.Add(
				model.State == ReconciledModelState.Discovered
					? model with { IsExposed = false }
					: model);
		}

		return new ReconciliationResult(exposureAdjusted);
	}

	/// <summary>
	/// Reconciles the pins and snapshot for a single backend. Available pins and discovered models are interleaved
	/// in snapshot order to keep positions stable: a model stays in the same table position when toggling between
	/// pinned and unpinned, eliminating "jumping" on pin/unpin. Unavailable pins (those the snapshot does not
	/// offer) are appended at the end, since they have no natural position in the snapshot ordering.
	/// </summary>
	/// <param name="backendName">The logical backend being reconciled.</param>
	/// <param name="backend">The backend's options, supplying the model prefix and context-length default.</param>
	/// <param name="pins">
	/// The backend's own registry entries (<see cref="BackendOptions.Models"/>); every entry is reconciled, as
	/// each already belongs to this backend by position.
	/// </param>
	/// <param name="snapshot">
	/// The freshly fetched and resolved candidates the backend currently offers, as produced by
	/// <see cref="IBackendModelDiscovery"/>.
	/// </param>
	/// <returns>The reconciled models and headline counts for the backend.</returns>
	/// <exception cref="ArgumentNullException">
	/// Any of <paramref name="backendName"/>, <paramref name="backend"/>, <paramref name="pins"/>, or
	/// <paramref name="snapshot"/> is <see langword="null"/>.
	/// </exception>
	/// <exception cref="ArgumentException"><paramref name="backendName"/> is empty or whitespace.</exception>
	/// <remarks>
	///     <para>
	///     <b>Honest backend view:</b> a Discovered (unpinned) row shows the backend's <em>raw</em> reported
	///     context window, never the configured default, so the table never overstates what the backend offers.
	///     When the backend reports none, the row shows none. The configured default applies only to pinned
	///     models.
	///     </para>
	///     <para>
	///     <b>Pin context inheritance:</b> a pin without an explicit
	///     <see cref="ModelRegistrationOptions.ContextLength"/> override inherits the matched candidate's reported
	///     context dynamically, falling back to the backend default only when the candidate reports none. It
	///     therefore tracks the backend's honest value rather than freezing a stale snapshot or being narrowed by
	///     the default. Only pins with an explicit override are marked
	///     <see cref="ReconciledModel.ExplicitContextOverride"/> and participate in context drift detection.
	///     </para>
	/// </remarks>
	public static ReconciliationResult Reconcile(
		string                                  backendName,
		BackendOptions                          backend,
		IReadOnlyList<ModelRegistrationOptions> pins,
		IReadOnlyList<DiscoveryCandidate>       snapshot)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(backendName);
		ArgumentNullException.ThrowIfNull(backend);
		ArgumentNullException.ThrowIfNull(pins);
		ArgumentNullException.ThrowIfNull(snapshot);

		// The upstream id is sent to the backend verbatim, so it is matched ordinally (case-sensitive). The
		// snapshot is indexed by upstream id for two reasons: an available pin can recover the backend's
		// currently-reported values (capabilities, context) for drift detection, and pin upstream ids can mark
		// which snapshot candidates are already claimed (so they are not offered again as new discoveries). The
		// first candidate wins on the rare duplicate id; discovery already de-duplicates by client-facing name,
		// so a collision here would only be a backend listing the same upstream id twice. A position map
		// preserves snapshot ordering for the interleaved result: Available pins and Discovered rows are emitted
		// in snapshot order, while Unavailable pins (no snapshot position) are appended at the end.
		Dictionary<string, DiscoveryCandidate> snapshotById = new(StringComparer.Ordinal);
		Dictionary<string, int> snapshotPosition = new(StringComparer.Ordinal);
		for (int i = 0; i < snapshot.Count; i++)
		{
			DiscoveryCandidate candidate = snapshot[i];
			if (snapshotById.TryAdd(candidate.UpstreamModel, candidate))
			{
				snapshotPosition[candidate.UpstreamModel] = i;
			}
		}

		HashSet<string> pinnedUpstreamIds = new(StringComparer.Ordinal);
		List<ReconciledModel> availablePins = [];
		List<ReconciledModel> unavailablePins = [];

		// 1. Process the backend's own pins. Each is retained regardless of availability; the snapshot only
		//    decides whether the backend currently honors it (Available) or not (Unavailable). When the snapshot
		//    still offers the pin, the backend's reported values are carried alongside the pin's own configured
		//    values so the surface can flag drift: a pin whose capabilities or explicit context override no
		//    longer match what the backend reports. A pin without an explicit context override inherits the
		//    matched candidate's context dynamically (or the backend default when none is reported), so it tracks
		//    the backend's value rather than freezing a stale snapshot.
		foreach (ModelRegistrationOptions pin in pins)
		{
			string upstreamModel = pin.ResolveUpstreamModel();
			pinnedUpstreamIds.Add(upstreamModel);

			bool available = snapshotById.TryGetValue(upstreamModel, out DiscoveryCandidate? match);
			ReconciledModelState state = available
				                             ? ReconciledModelState.Available
				                             : ReconciledModelState.Unavailable;

			// A pin with no explicit ContextLength inherits the matched candidate's reported context (when
			// available), or the backend default (when the candidate reports none or the pin is unavailable).
			// This is the shared three-tier rule: explicit override wins, else the reported value, else the
			// default. The pin therefore tracks the backend's honest value rather than freezing it at pin
			// creation time or being narrowed by the default. Only pins with an explicit override participate in
			// context drift detection.
			bool explicitContextOverride = pin.ContextLength.HasValue;
			long? resolvedContext = ModelExposureRules.ResolveEffectiveContextWindow(
				explicitOverride: pin.ContextLength,
				reported: match?.ReportedContextLength,
				backendDefault: backend.ContextLength);

			ReconciledModel model = new(
				pin.Name,
				ModelExposureRules.ApplyClientFacingPrefix(backend.ModelPrefix, pin.Name),
				backendName,
				upstreamModel,
				ModelExposureRules.ResolveRegisteredCapabilities(pin),
				resolvedContext,
				state,
				ExplicitContextOverride: explicitContextOverride,
				DiscoveredCapabilities: match?.Capabilities,
				DiscoveredContextLength: match?.ReportedContextLength,
				Metadata: match?.Metadata);

			if (available)
			{
				availablePins.Add(model);
			}
			else
			{
				unavailablePins.Add(model);
			}
		}

		// 2. Interleave Available pins and Discovered candidates in snapshot order, then append Unavailable pins.
		//    A model's table position is anchored to its snapshot position, so toggling pin/unpin leaves it in
		//    place rather than jumping between a "pins first" and "discovered last" grouping.
		List<ReconciledModel> reconciled = [];

		for (int i = 0; i < snapshot.Count; i++)
		{
			DiscoveryCandidate candidate = snapshot[i];

			// Available pins at this snapshot position are emitted in registry order. Several pins may share one
			// upstream id on purpose: one upstream model exposed under distinct client-facing names, each with its
			// own fixed reasoning effort or other overrides. Every matching pin is therefore emitted here, not just
			// the first: the upstream id is a match key, never a per-pin identity.
			foreach (ReconciledModel pin in availablePins)
			{
				if (snapshotPosition.TryGetValue(pin.UpstreamModel, out int pos) && pos == i)
				{
					reconciled.Add(pin);
				}
			}

			// If no pin already claimed this candidate, emit it as a Discovered row. The row shows the backend's
			// raw reported window, never the configured default, so an unpinned model never "lies" about the
			// backend. When the backend reports nothing, the row shows no window and the operator pins the model
			// to supply one. The configured default takes effect only for exposed/pinned models, not here.
			if (!pinnedUpstreamIds.Contains(candidate.UpstreamModel))
			{
				// The exposed name is recomputed from the current draft prefix rather than read off the
				// candidate's snapshot-time ClientName. Editing the backend's ModelPrefix therefore updates
				// discovered rows live on the next reconcile (exactly as pins already track it), without a
				// refetch. The upstream id is the unprefixed bare name, so this reproduces the discovery-time
				// client name when the prefix is unchanged and follows the edit when it is not.
				reconciled.Add(
					new ReconciledModel(
						candidate.UpstreamModel,
						ModelExposureRules.ApplyClientFacingPrefix(backend.ModelPrefix, candidate.UpstreamModel),
						backendName,
						candidate.UpstreamModel,
						candidate.Capabilities,
						candidate.ReportedContextLength,
						ReconciledModelState.Discovered,
						Metadata: candidate.Metadata));
			}
		}

		// 3. Append Unavailable pins at the end: they have no snapshot position, so they go last.
		reconciled.AddRange(unavailablePins);

		return new ReconciliationResult(reconciled);
	}
}
