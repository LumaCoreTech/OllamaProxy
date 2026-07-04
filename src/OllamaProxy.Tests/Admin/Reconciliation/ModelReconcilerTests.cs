// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Admin.Reconciliation;
using OllamaProxy.Configuration;
using OllamaProxy.Core;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Tests.Admin.Reconciliation;

// Reconciliation: from a freshly fetched snapshot back to a stable, operator-facing model list.
//
// These tests follow one backend's models as the engine compares the operator's existing pins against a
// just-fetched snapshot — the heart of the admin "Fetch" flow — and prove the invariants the UI relies on:
//
//   1. Pure cases: empty in → empty out; a snapshot with no pins is all Discovered; pins all present are
//      all Available (PinsAndSnapshotAreEmpty, NoPins, AllPinsPresentInSnapshot).
//
//   2. The point of the exercise: a pin the backend dropped becomes Unavailable but is never deleted; the
//      result interleaves Available pins and Discovered models in snapshot order, with Unavailable pins
//      appended at the end so a model stays in the same position when toggling between pinned and unpinned
//      (PinMissingFromSnapshot, PinsAndDiscoveriesMix).
//
//   3. Match semantics: the upstream id is the key — an alias still matches, case differences do not (ordinal),
//      every handed pin is reconciled because the caller passes only this backend's own registry, and several
//      pins may deliberately share one upstream id (the "+ variant" use case) without swallowing each other or
//      the candidate (PinUsesUpstreamAlias, UpstreamIdCaseDiffers, HandedPins, MultiplePinsShareUpstream).
//
//   4. Exposure-rule wiring: discovered and pinned names carry the backend prefix on their exposed name while
//      the bare name stays unprefixed, a Discovered row shows the backend's raw reported window unnarrowed by
//      the default, pin capabilities resolve as Configured, and provider metadata provenance is preserved
//      (BackendHasPrefix_DiscoveredNameIsPrefixed, BackendHasPrefix_PinnedNameIsPrefixed,
//      DiscoveredRowShowsRawReportedWindow, PinOverridesCapabilities, SnapshotCarriesCapabilities).
//
//   5. Result projection: the headline counts mirror the per-row states (ResultHasAllStates).
//
//   6. Drift carry-through: an available pin copies the backend's reported capabilities and context onto the
//      row so the surface can flag stale pins, while unavailable and discovered rows carry nothing; DriftCount
//      counts only the drifted pins (PinIsAvailable, PinIsUnavailable, RowIsDiscovered, ResultHasDrift).
//      The drift comparison itself lives on ReconciledModel and is exercised by ReconciledModelTests.
//
//   7. Context inheritance: pins without an explicit ContextLength override dynamically inherit the matched
//      candidate's reported context, falling back to the backend default only when none is reported (the
//      reported value wins and is never narrowed by the default), so they track the backend's honest value
//      rather than freezing it at pin creation time; only pins with explicit overrides participate in context
//      drift detection (PinHasNoExplicitContext, PinHasExplicitContext, FallsBackToBackendDefault,
//      ReportedExceedsDefault).
//
//   8. Invalid args: the four null/blank guards.
//
// Sections 1–8 above cover the pure Reconcile(name, backend, pins, snapshot) merge (#region Reconcile(), with the
// numbered dividers inside). The mode-aware ReconcileBackend(name, backend, effectiveMode, snapshot) wrapper —
// which decides whether the backend's registry participates at all — follows in #region ReconcileBackend():
// PlugAndPlay drops the registry exactly as the runtime catalog does, while Hybrid and Explicit honor it, plus
// its own guards.
[Trait("Category", "Unit")]
public sealed class ModelReconcilerTests
{
	#region Reconcile()

	// --- 1. Pure cases ---

	/// <summary>
	/// Verifies that reconciling no pins against an empty snapshot yields an empty result with all counts
	/// at zero — the degenerate base case.
	/// </summary>
	[Fact]
	public void Reconcile_WhenPinsAndSnapshotAreEmpty_ReturnsEmptyResult()
	{
		// Arrange + Act
		ReconciliationResult result = ModelReconciler.Reconcile("cloud", Backend(), [], []);

		// Assert
		Assert.Empty(result.Models);
		AssertCounts(result, available: 0, unavailable: 0, discovered: 0);
	}

	/// <summary>
	/// Verifies that when the operator has no pins, every snapshot model surfaces as a
	/// <see cref="ReconciledModelState.Discovered"/> candidate, in snapshot order.
	/// </summary>
	[Fact]
	public void Reconcile_WhenNoPins_AllSnapshotModelsBecomeDiscovered()
	{
		// Arrange: a bare snapshot of two models and an empty registry.
		IReadOnlyList<DiscoveryCandidate> snapshot =
			[Disc("alpha", contextLength: 4096), Disc("beta", contextLength: 8192)];

		// Act
		ReconciliationResult result = ModelReconciler.Reconcile("cloud", Backend(), [], snapshot);

		// Assert: both rows are Discovered, in snapshot order, with their snapshot windows.
		Assert.Equal(2, result.Models.Count);
		AssertModel(result.Models[0], "alpha", "cloud", "alpha", ReconciledModelState.Discovered, 4096);
		AssertModel(result.Models[1], "beta", "cloud", "beta", ReconciledModelState.Discovered, 8192);
		AssertCounts(result, available: 0, unavailable: 0, discovered: 2);
	}

	/// <summary>
	/// Verifies that when every pin's upstream model is still offered by the snapshot, all pins are
	/// <see cref="ReconciledModelState.Available"/> and no discovered candidates are produced.
	/// </summary>
	[Fact]
	public void Reconcile_WhenAllPinsPresentInSnapshot_AllPinsAreAvailable()
	{
		// Arrange: two pins whose upstream ids both appear in the snapshot, and nothing else in the snapshot.
		IReadOnlyList<ModelRegistrationOptions> pins =
			[Pin("alpha", contextLength: 4096), Pin("beta", contextLength: 8192)];
		IReadOnlyList<DiscoveryCandidate> snapshot =
			[Disc("alpha", contextLength: 4096), Disc("beta", contextLength: 8192)];

		// Act
		ReconciliationResult result = ModelReconciler.Reconcile("cloud", Backend(), pins, snapshot);

		// Assert: both pins are Available in registry order; the snapshot adds no new candidates.
		Assert.Equal(2, result.Models.Count);
		AssertModel(result.Models[0], "alpha", "cloud", "alpha", ReconciledModelState.Available, 4096);
		AssertModel(result.Models[1], "beta", "cloud", "beta", ReconciledModelState.Available, 8192);
		AssertCounts(result, available: 2, unavailable: 0, discovered: 0);
	}

	// --- 2. The point of the exercise: dropped pins survive; mixes order pins-first ---

	/// <summary>
	/// Verifies the headline behavior: a pin whose upstream model the snapshot no longer offers becomes
	/// <see cref="ReconciledModelState.Unavailable"/> yet is <em>retained</em> in the result — the engine
	/// never silently drops an operator's pin. The unavailable pin is appended at the end after the snapshot
	/// models, since it has no natural position in the snapshot ordering.
	/// </summary>
	[Fact]
	public void Reconcile_WhenPinMissingFromSnapshot_PinBecomesUnavailableAndIsRetained()
	{
		// Arrange: the operator pinned "ghost", but the backend's latest snapshot no longer lists it.
		IReadOnlyList<ModelRegistrationOptions> pins = [Pin("ghost", contextLength: 4096)];
		IReadOnlyList<DiscoveryCandidate> snapshot = [Disc("present", contextLength: 4096)];

		// Act
		ReconciliationResult result = ModelReconciler.Reconcile("cloud", Backend(), pins, snapshot);

		// Assert: the snapshot model (Discovered) comes first, then the unavailable pin is appended at the end.
		Assert.Equal(2, result.Models.Count);
		AssertModel(result.Models[0], "present", "cloud", "present", ReconciledModelState.Discovered, 4096);
		AssertModel(result.Models[1], "ghost", "cloud", "ghost", ReconciledModelState.Unavailable, 4096);
		AssertCounts(result, available: 0, unavailable: 1, discovered: 1);
	}

	/// <summary>
	/// Verifies that a realistic mix — one pin still present, one pin gone, and one brand-new snapshot model —
	/// interleaves Available pins and Discovered models in snapshot order, with Unavailable pins appended at the
	/// end. A model stays in the same position when toggling between pinned and unpinned, eliminating "jumping" on
	/// pin/unpin.
	/// </summary>
	[Fact]
	public void Reconcile_WhenPinsAndDiscoveriesMix_InterleavesBySnapshotOrder()
	{
		// Arrange: pins [kept, gone]; snapshot offers [fresh-1, kept, fresh-2] — so kept should appear in the
		// middle (snapshot position 1), not first (registry order). Gone is unavailable and goes last.
		IReadOnlyList<ModelRegistrationOptions> pins =
			[Pin("kept", contextLength: 4096), Pin("gone", contextLength: 4096)];
		IReadOnlyList<DiscoveryCandidate> snapshot =
		[
			Disc("fresh-1", contextLength: 2048), Disc("kept", contextLength: 4096),
			Disc("fresh-2", contextLength: 8192)
		];

		// Act
		ReconciliationResult result = ModelReconciler.Reconcile("cloud", Backend(), pins, snapshot);

		// Assert: snapshot order is preserved (fresh-1, kept, fresh-2), then the unavailable pin (gone) at the end.
		Assert.Equal(4, result.Models.Count);
		AssertModel(result.Models[0], "fresh-1", "cloud", "fresh-1", ReconciledModelState.Discovered, 2048);
		AssertModel(result.Models[1], "kept", "cloud", "kept", ReconciledModelState.Available, 4096);
		AssertModel(result.Models[2], "fresh-2", "cloud", "fresh-2", ReconciledModelState.Discovered, 8192);
		AssertModel(result.Models[3], "gone", "cloud", "gone", ReconciledModelState.Unavailable, 4096);
		AssertCounts(result, available: 1, unavailable: 1, discovered: 2);
	}

	// --- 3. Match semantics: upstream id is the key ---

	/// <summary>
	/// Verifies that a pin whose client-facing name differs from its upstream id (an alias via
	/// <see cref="ModelRegistrationOptions.UpstreamModel"/>) matches the snapshot on the <em>upstream</em> id,
	/// and the aliased upstream model is therefore not also offered as a discovery.
	/// </summary>
	[Fact]
	public void Reconcile_WhenPinUsesUpstreamAlias_MatchesSnapshotByUpstreamId()
	{
		// Arrange: the operator exposes "gpt4" but the backend's real id is "gpt-4-0613".
		IReadOnlyList<ModelRegistrationOptions> pins =
			[Pin("gpt4", upstream: "gpt-4-0613", contextLength: 8192)];
		IReadOnlyList<DiscoveryCandidate> snapshot = [Disc("gpt-4-0613", contextLength: 8192)];

		// Act
		ReconciliationResult result = ModelReconciler.Reconcile("cloud", Backend(), pins, snapshot);

		// Assert: the alias matched on the upstream id, so there is one Available row and no duplicate discovery.
		ReconciledModel model = Assert.Single(result.Models);
		AssertModel(model, "gpt4", "cloud", "gpt-4-0613", ReconciledModelState.Available, 8192);
		AssertCounts(result, available: 1, unavailable: 0, discovered: 0);
	}

	/// <summary>
	/// Verifies that the upstream-id match is ordinal (case-sensitive): a pin for <c>Llama-3</c> does not match
	/// a snapshot <c>llama-3</c>, so the pin is <see cref="ReconciledModelState.Unavailable"/> and the snapshot
	/// model surfaces as a distinct <see cref="ReconciledModelState.Discovered"/> candidate. The unavailable pin
	/// is appended at the end after the snapshot model.
	/// </summary>
	[Fact]
	public void Reconcile_WhenUpstreamIdCaseDiffers_TreatsAsUnavailableAndDiscovered()
	{
		// Arrange: same name modulo case — the upstream id is sent verbatim, so case must matter.
		IReadOnlyList<ModelRegistrationOptions> pins = [Pin("Llama-3", contextLength: 4096)];
		IReadOnlyList<DiscoveryCandidate> snapshot = [Disc("llama-3", contextLength: 4096)];

		// Act
		ReconciliationResult result = ModelReconciler.Reconcile("cloud", Backend(), pins, snapshot);

		// Assert: the snapshot model (Discovered) comes first, then the unavailable pin is appended at the end.
		Assert.Equal(2, result.Models.Count);
		AssertModel(result.Models[0], "llama-3", "cloud", "llama-3", ReconciledModelState.Discovered, 4096);
		AssertModel(result.Models[1], "Llama-3", "cloud", "Llama-3", ReconciledModelState.Unavailable, 4096);
		AssertCounts(result, available: 0, unavailable: 1, discovered: 1);
	}

	/// <summary>
	/// Verifies that the reconciler reconciles every pin it is handed without any backend-based filtering: the
	/// caller passes only the backend's own <see cref="BackendOptions.Models"/>, so each entry already belongs to
	/// this backend by position and must participate in the result.
	/// </summary>
	[Fact]
	public void Reconcile_WhenHandedPins_ReconcilesEveryPin()
	{
		// Arrange: two pins, both belonging to this backend by position; the snapshot offers one of them.
		IReadOnlyList<ModelRegistrationOptions> pins =
			[Pin("mine", contextLength: 4096), Pin("dropped", contextLength: 4096)];
		IReadOnlyList<DiscoveryCandidate> snapshot = [Disc("mine", contextLength: 4096)];

		// Act
		ReconciliationResult result = ModelReconciler.Reconcile("cloud", Backend(), pins, snapshot);

		// Assert: both handed pins participate — the offered one is Available, the dropped one Unavailable — and
		// no pin is silently filtered out.
		Assert.Equal(2, result.Models.Count);
		AssertModel(result.Models[0], "mine", "cloud", "mine", ReconciledModelState.Available, 4096);
		AssertModel(result.Models[1], "dropped", "cloud", "dropped", ReconciledModelState.Unavailable, 4096);
	}

	/// <summary>
	/// Verifies that several pins may deliberately share one upstream model: the upstream id is a match key, not a
	/// per-pin identity, so two pins aliased to the same upstream id both reconcile as
	/// <see cref="ReconciledModelState.Available"/> (under their own distinct client-facing names) and the shared
	/// upstream model is not also offered as a discovery. This backs the admin "+ variant" flow — exposing one
	/// model several times, e.g. at distinct fixed reasoning efforts — so neither pin may swallow or hide the other.
	/// </summary>
	[Fact]
	public void Reconcile_WhenMultiplePinsShareUpstream_AllAreAvailableWithoutDuplicateDiscovery()
	{
		// Arrange: two pins exposing the same upstream "gpt-5" under different names (the reasoning-variant use
		// case), and a snapshot that offers that upstream model exactly once.
		IReadOnlyList<ModelRegistrationOptions> pins =
		[
			Pin("gpt5-low", upstream: "gpt-5", contextLength: 8192),
			Pin("gpt5-high", upstream: "gpt-5", contextLength: 8192)
		];
		IReadOnlyList<DiscoveryCandidate> snapshot = [Disc("gpt-5", contextLength: 8192)];

		// Act
		ReconciliationResult result = ModelReconciler.Reconcile("cloud", Backend(), pins, snapshot);

		// Assert: both pins are Available (registry order at the shared snapshot position) and the single upstream
		// model is fully claimed, so it surfaces no extra Discovered row.
		Assert.Equal(2, result.Models.Count);
		AssertModel(result.Models[0], "gpt5-low", "cloud", "gpt-5", ReconciledModelState.Available, 8192);
		AssertModel(result.Models[1], "gpt5-high", "cloud", "gpt-5", ReconciledModelState.Available, 8192);
		AssertCounts(result, available: 2, unavailable: 0, discovered: 0);
	}

	// --- 4. Exposure-rule wiring ---

	/// <summary>
	/// Verifies that a discovered row splits its names correctly: the bare <see cref="ReconciledModel.Name"/> is
	/// the unprefixed upstream id (the identity a pin would store), while the
	/// <see cref="ReconciledModel.ExposedName"/> is recomputed from the backend's <em>current</em> model prefix
	/// applied to that upstream id — not copied from the candidate's snapshot-time client name. A deliberately
	/// stale candidate client name proves the recompute: the row reflects the live prefix, not the frozen value.
	/// </summary>
	[Fact]
	public void Reconcile_WhenBackendHasPrefix_DiscoveredNameIsPrefixed()
	{
		// Arrange: the candidate carries a stale client name from an earlier prefix ("old/gemma2"); the backend's
		// current prefix is "vllm", so the reconciled exposed name must be recomputed as "vllm/gemma2".
		IReadOnlyList<DiscoveryCandidate> snapshot = [Disc("gemma2", clientName: "old/gemma2", contextLength: 8192)];

		// Act
		ReconciliationResult result = ModelReconciler.Reconcile("cloud", Backend(prefix: "vllm"), [], snapshot);

		// Assert: the bare name and upstream id stay unprefixed; the exposed name carries the current prefix, not
		// the candidate's stale client name.
		ReconciledModel model = Assert.Single(result.Models);
		AssertModel(
			model,
			"gemma2",
			"cloud",
			"gemma2",
			ReconciledModelState.Discovered,
			8192,
			exposedName: "vllm/gemma2");
	}

	/// <summary>
	/// Verifies that a pinned row resolves its own exposed name through the shared exposure rules: the bare
	/// <see cref="ReconciledModel.Name"/> stays the registry entry's unprefixed name while the
	/// <see cref="ReconciledModel.ExposedName"/> applies the backend prefix, matching how the runtime catalog
	/// names the same pin. Unlike a discovered row (whose exposed name arrives pre-resolved on the candidate), a
	/// pin derives its exposed name here rather than reading it off the matching candidate.
	/// </summary>
	[Fact]
	public void Reconcile_WhenBackendHasPrefix_PinnedNameIsPrefixed()
	{
		// Arrange: a pin the backend still offers, under a prefixed backend. The pin stores the bare name; the
		// reconciler applies the prefix to derive the exposed name (it does not copy it from the candidate).
		IReadOnlyList<ModelRegistrationOptions> pins = [Pin("gemma2", contextLength: 8192)];
		IReadOnlyList<DiscoveryCandidate> snapshot = [Disc("gemma2", clientName: "vllm/gemma2", contextLength: 8192)];

		// Act
		ReconciliationResult result = ModelReconciler.Reconcile("cloud", Backend(prefix: "vllm"), pins, snapshot);

		// Assert: the bare name and upstream id stay unprefixed; the exposed name carries the prefix.
		ReconciledModel model = Assert.Single(result.Models);
		AssertModel(
			model,
			"gemma2",
			"cloud",
			"gemma2",
			ReconciledModelState.Available,
			8192,
			exposedName: "vllm/gemma2");
	}

	/// <summary>
	/// Verifies that re-reconciling the <em>same</em> snapshot after the backend's model prefix changes updates a
	/// discovered row's <see cref="ReconciledModel.ExposedName"/> to track the new prefix — the operator's
	/// scenario of editing the prefix and expecting the table to follow without a refetch. The bare name and
	/// upstream id are unaffected, since only the client-facing prefix changed.
	/// </summary>
	[Fact]
	public void Reconcile_WhenPrefixChangesBetweenReconciles_DiscoveredExposedNameTracksCurrentPrefix()
	{
		// Arrange: one fetched snapshot, reconciled twice against the same backend options under two prefixes —
		// exactly what the editor does as the operator types into the prefix field (no refetch in between).
		IReadOnlyList<DiscoveryCandidate> snapshot = [Disc("gemma2", clientName: "old/gemma2", contextLength: 8192)];

		// Act: reconcile under the first prefix, then under a changed prefix, reusing the identical snapshot.
		ReconciledModel first =
			Assert.Single(ModelReconciler.Reconcile("cloud", Backend(prefix: "vllm"), [], snapshot).Models);
		ReconciledModel second =
			Assert.Single(ModelReconciler.Reconcile("cloud", Backend(prefix: "edge"), [], snapshot).Models);

		// Assert: the exposed name follows the live prefix each time; the identity columns never move.
		AssertModel(
			first,
			"gemma2",
			"cloud",
			"gemma2",
			ReconciledModelState.Discovered,
			8192,
			exposedName: "vllm/gemma2");
		AssertModel(
			second,
			"gemma2",
			"cloud",
			"gemma2",
			ReconciledModelState.Discovered,
			8192,
			exposedName: "edge/gemma2");
	}

	/// <summary>
	/// Verifies that a Discovered row shows the backend's raw reported context window unchanged and is never
	/// narrowed by the backend default — the default is a fallback applied only to exposed and pinned models, not
	/// to the honest discovered view.
	/// </summary>
	[Fact]
	public void Reconcile_WhenBackendDefaultNarrowerThanReported_DiscoveredRowShowsRawReportedWindow()
	{
		// Arrange: the candidate reports an 8192 window while the backend configures a narrower 2048 default.
		IReadOnlyList<DiscoveryCandidate> snapshot = [Disc("model", contextLength: 8192)];

		// Act
		ReconciliationResult result =
			ModelReconciler.Reconcile("cloud", Backend(contextLength: 2048), [], snapshot);

		// Assert: the discovered row shows the raw reported 8192, not the narrower configured default.
		ReconciledModel model = Assert.Single(result.Models);
		AssertModel(model, "model", "cloud", "model", ReconciledModelState.Discovered, 8192);
	}

	/// <summary>
	/// Verifies that a pin's capabilities resolve through the shared exposure rules: an explicit override marks
	/// the source <see cref="CapabilitySource.Configured"/> and carries the pinned flags, with completion
	/// defaulting on and the unset additive flags off.
	/// </summary>
	[Fact]
	public void Reconcile_WhenPinOverridesCapabilities_DiscoveredCapabilitiesAreConfigured()
	{
		// Arrange: a pin that opts into tools but leaves the other flags unset.
		IReadOnlyList<ModelRegistrationOptions> pins =
			[Pin("tooled", contextLength: 4096, supportsTools: true)];
		IReadOnlyList<DiscoveryCandidate> snapshot = [Disc("tooled", contextLength: 4096)];

		// Act
		ReconciliationResult result = ModelReconciler.Reconcile("cloud", Backend(), pins, snapshot);

		// Assert: the pin's resolved capabilities reflect the override with a Configured provenance.
		ReconciledModel model = Assert.Single(result.Models);
		Assert.NotNull(model.Capabilities);
		Assert.Equal(
			new ModelCapabilities(
				SupportsCompletion: true,
				SupportsTools: true,
				SupportsVision: false,
				SupportsEmbeddings: false,
				CapabilitySource.Configured),
			model.Capabilities);
	}

	/// <summary>
	/// Verifies that discovered rows preserve their resolved capability provenance: a candidate that carried
	/// metadata keeps it verbatim, while a candidate that discovery deliberately left unresolved keeps
	/// <see cref="ReconciledModel.Capabilities"/> <see langword="null"/>.
	/// </summary>
	[Fact]
	public void Reconcile_WhenSnapshotCarriesCapabilities_DiscoveredRowsCarryProvenance()
	{
		// Arrange: one candidate with listed capabilities, one deliberately unresolved by the discovery policy.
		ModelCapabilities listed = new(
			SupportsCompletion: true,
			SupportsTools: true,
			SupportsVision: true,
			SupportsEmbeddings: false,
			CapabilitySource.ProviderMetadata);
		IReadOnlyList<DiscoveryCandidate> snapshot =
		[
			Disc("rich", contextLength: 8192, capabilities: listed),
			Disc("poor", contextLength: 8192)
		];

		// Act
		ReconciliationResult result = ModelReconciler.Reconcile("cloud", Backend(), [], snapshot);

		// Assert: the metadata-rich row keeps the exact listed capabilities; the metadata-poor row stays null.
		Assert.Equal(2, result.Models.Count);
		Assert.Same(listed, result.Models[0].Capabilities);
		Assert.Null(result.Models[1].Capabilities);
	}

	// --- 5. Result projection ---

	/// <summary>
	/// Verifies that the headline counts on <see cref="ReconciliationResult"/> mirror the per-row states across
	/// a result containing one of each state.
	/// </summary>
	[Fact]
	public void Reconcile_WhenResultHasAllStates_CountsReflectEachState()
	{
		// Arrange: pins [stays, leaves]; snapshot offers [stays, newcomer] — yields one of each state.
		IReadOnlyList<ModelRegistrationOptions> pins =
			[Pin("stays", contextLength: 4096), Pin("leaves", contextLength: 4096)];
		IReadOnlyList<DiscoveryCandidate> snapshot =
			[Disc("stays", contextLength: 4096), Disc("newcomer", contextLength: 4096)];

		// Act
		ReconciliationResult result = ModelReconciler.Reconcile("cloud", Backend(), pins, snapshot);

		// Assert: exactly one Available, one Unavailable, one Discovered.
		Assert.Equal(3, result.Models.Count);
		AssertCounts(result, available: 1, unavailable: 1, discovered: 1);
	}

	// --- 6. Drift carry-through: available pins capture the backend's reported values ---

	/// <summary>
	/// Verifies that an <see cref="ReconciledModelState.Available"/> pin captures the matching snapshot
	/// candidate's reported capabilities and context window onto the row, so the surface can compare them
	/// against the pin's configured values without re-matching. Here the backend reports a wider window and
	/// tool support the pin does not, so the row is drifted.
	/// </summary>
	[Fact]
	public void Reconcile_WhenPinIsAvailable_CarriesDiscoveredValuesForDrift()
	{
		// Arrange: the pin is completion-only at 4096; the backend now reports tools and a wider 8192 window.
		ModelCapabilities reported = new(
			SupportsCompletion: true,
			SupportsTools: true,
			SupportsVision: false,
			SupportsEmbeddings: false,
			CapabilitySource.ProviderMetadata);
		IReadOnlyList<ModelRegistrationOptions> pins = [Pin("model", contextLength: 4096)];
		IReadOnlyList<DiscoveryCandidate> snapshot = [Disc("model", contextLength: 8192, capabilities: reported)];

		// Act
		ReconciliationResult result = ModelReconciler.Reconcile("cloud", Backend(), pins, snapshot);

		// Assert: the backend's reported values are carried verbatim, and the divergence surfaces as drift.
		ReconciledModel model = Assert.Single(result.Models);
		Assert.Same(reported, model.DiscoveredCapabilities);
		Assert.Equal(8192, model.DiscoveredContextLength);
		Assert.True(model.IsDrifted);
		AssertCounts(result, available: 1, unavailable: 0, discovered: 0);
		Assert.Equal(1, result.DriftCount);
	}

	/// <summary>
	/// Verifies that an <see cref="ReconciledModelState.Unavailable"/> pin carries no discovered values — there
	/// is no matching snapshot candidate to copy from — so it can never be flagged as drifted.
	/// </summary>
	[Fact]
	public void Reconcile_WhenPinIsUnavailable_CarriesNoDiscoveredValues()
	{
		// Arrange: the pin's upstream id is absent from the snapshot, so there is nothing to compare against.
		IReadOnlyList<ModelRegistrationOptions> pins = [Pin("ghost", contextLength: 4096)];
		IReadOnlyList<DiscoveryCandidate> snapshot = [Disc("present", contextLength: 8192)];

		// Act
		ReconciledModel model = Assert.Single(
			ModelReconciler.Reconcile("cloud", Backend(), pins, snapshot).Models,
			m => m.State == ReconciledModelState.Unavailable);

		// Assert: no discovered values, hence no drift.
		Assert.Null(model.DiscoveredCapabilities);
		Assert.Null(model.DiscoveredContextLength);
		Assert.False(model.IsDrifted);
	}

	/// <summary>
	/// Verifies that a <see cref="ReconciledModelState.Discovered"/> row carries no discovered-comparison
	/// values: drift is a pin-only concept, and an unpinned candidate has no configured baseline to diverge
	/// from. Its own resolved capabilities/context live on <see cref="ReconciledModel.Capabilities"/> and
	/// <see cref="ReconciledModel.ContextLength"/>, not the discovered-comparison fields.
	/// </summary>
	[Fact]
	public void Reconcile_WhenRowIsDiscovered_CarriesNoDiscoveredComparisonValues()
	{
		// Arrange: a snapshot candidate with no pin claiming it.
		IReadOnlyList<DiscoveryCandidate> snapshot = [Disc("fresh", contextLength: 8192)];

		// Act
		ReconciledModel model = Assert.Single(ModelReconciler.Reconcile("cloud", Backend(), [], snapshot).Models);

		// Assert: the comparison fields stay null even though the row's own context is populated.
		Assert.Equal(ReconciledModelState.Discovered, model.State);
		Assert.Equal(8192, model.ContextLength);
		Assert.Null(model.DiscoveredCapabilities);
		Assert.Null(model.DiscoveredContextLength);
		Assert.False(model.IsDrifted);
	}

	/// <summary>
	/// Verifies that <see cref="ReconciliationResult.DriftCount"/> counts only the available pins that actually
	/// drifted: across a drifted pin, an aligned pin, an unavailable pin, and a discovered row, exactly one is
	/// counted.
	/// </summary>
	[Fact]
	public void Reconcile_WhenResultHasDrift_DriftCountCountsOnlyDriftedPins()
	{
		// Arrange: "drift" reports a wider window than pinned; "aligned" matches exactly; "gone" is dropped;
		// "fresh" is a new discovery. Only "drift" should be counted.
		IReadOnlyList<ModelRegistrationOptions> pins =
		[
			Pin("drift", contextLength: 4096),
			Pin("aligned", contextLength: 4096),
			Pin("gone", contextLength: 4096)
		];
		IReadOnlyList<DiscoveryCandidate> snapshot =
		[
			Disc("drift", contextLength: 8192),
			Disc("aligned", contextLength: 4096),
			Disc("fresh", contextLength: 2048)
		];

		// Act
		ReconciliationResult result = ModelReconciler.Reconcile("cloud", Backend(), pins, snapshot);

		// Assert: only the diverging available pin is counted as drift; the other rows are not.
		Assert.Equal(1, result.DriftCount);
		Assert.True(result.Models.Single(m => m.Name == "drift").IsDrifted);
		Assert.False(result.Models.Single(m => m.Name == "aligned").IsDrifted);
		Assert.False(result.Models.Single(m => m.Name == "gone").IsDrifted);
		Assert.False(result.Models.Single(m => m.Name == "fresh").IsDrifted);
	}

	// --- 7. Context inheritance: pins without explicit overrides track the backend dynamically ---

	/// <summary>
	/// Verifies that a pin without an explicit <see cref="ModelRegistrationOptions.ContextLength"/> override
	/// dynamically inherits the matched candidate's context window, so it tracks the backend's reported value
	/// rather than freezing it at pin creation time. Such a pin is not marked
	/// <see cref="ReconciledModel.ExplicitContextOverride"/> and never drifts on context changes.
	/// </summary>
	[Fact]
	public void Reconcile_WhenPinHasNoExplicitContext_InheritsDiscoveredContext()
	{
		// Arrange: a pin with no explicit context override; the matched candidate reports 8192.
		IReadOnlyList<ModelRegistrationOptions> pins = [Pin("model")];
		IReadOnlyList<DiscoveryCandidate> snapshot = [Disc("model", contextLength: 8192)];

		// Act
		ReconciliationResult result = ModelReconciler.Reconcile("cloud", Backend(), pins, snapshot);

		// Assert: the pin inherits the candidate's context; ExplicitContextOverride is false, no drift.
		ReconciledModel model = Assert.Single(result.Models);
		Assert.Equal(8192, model.ContextLength);
		Assert.False(model.ExplicitContextOverride);
		Assert.Equal(8192, model.DiscoveredContextLength);
		Assert.False(model.HasContextDrift);
		Assert.False(model.IsDrifted);
	}

	/// <summary>
	/// Verifies that a pin with an explicit <see cref="ModelRegistrationOptions.ContextLength"/> override is
	/// marked <see cref="ReconciledModel.ExplicitContextOverride"/> and participates in context drift detection.
	/// When the pin's override differs from the backend's reported value, the pin is flagged as drifted.
	/// </summary>
	[Fact]
	public void Reconcile_WhenPinHasExplicitContext_ParticipatesInDriftDetection()
	{
		// Arrange: a pin with an explicit 4096 context override; the backend now reports 8192.
		IReadOnlyList<ModelRegistrationOptions> pins = [Pin("model", contextLength: 4096)];
		IReadOnlyList<DiscoveryCandidate> snapshot = [Disc("model", contextLength: 8192)];

		// Act
		ReconciliationResult result = ModelReconciler.Reconcile("cloud", Backend(), pins, snapshot);

		// Assert: the pin uses its explicit override; ExplicitContextOverride is true, drift is detected.
		ReconciledModel model = Assert.Single(result.Models);
		Assert.Equal(4096, model.ContextLength);
		Assert.True(model.ExplicitContextOverride);
		Assert.Equal(8192, model.DiscoveredContextLength);
		Assert.True(model.HasContextDrift);
		Assert.True(model.IsDrifted);
	}

	/// <summary>
	/// Verifies that a pin without an explicit context override falls back to the backend default when the matched
	/// candidate reports no context, so the pin still resolves a usable window dynamically rather than staying
	/// <see langword="null"/>.
	/// </summary>
	[Fact]
	public void Reconcile_WhenPinHasNoExplicitContext_FallsBackToBackendDefault()
	{
		// Arrange: a pin with no explicit context; the candidate also reports none, but the backend has a default.
		IReadOnlyList<ModelRegistrationOptions> pins = [Pin("model")];
		IReadOnlyList<DiscoveryCandidate> snapshot = [Disc("model", contextLength: null)];

		// Act
		ReconciliationResult result = ModelReconciler.Reconcile("cloud", Backend(contextLength: 4096), pins, snapshot);

		// Assert: the pin inherits the backend default (4096); ExplicitContextOverride is false, no drift.
		ReconciledModel model = Assert.Single(result.Models);
		Assert.Equal(4096, model.ContextLength);
		Assert.False(model.ExplicitContextOverride);
		Assert.Null(model.DiscoveredContextLength);
		Assert.False(model.HasContextDrift);
		Assert.False(model.IsDrifted);
	}

	/// <summary>
	/// Verifies that a pin without an explicit context override keeps the backend's reported window even when it
	/// exceeds the configured backend default — the default is a fallback, not a narrowing clamp, so the pin is
	/// never silently shrunk below what the backend advertises. This is the regression that previously capped
	/// every model at the backend default.
	/// </summary>
	[Fact]
	public void Reconcile_WhenPinHasNoExplicitContextAndReportedExceedsDefault_ReportedWins()
	{
		// Arrange: a pin with no explicit override; the candidate reports a 128k window far wider than the backend's
		// 32k default. Under the former narrowing rule the pin would be capped at 32k; under the fallback rule the
		// reported 128k wins and the default is ignored.
		IReadOnlyList<ModelRegistrationOptions> pins = [Pin("model")];
		IReadOnlyList<DiscoveryCandidate> snapshot = [Disc("model", contextLength: 131072)];

		// Act
		ReconciliationResult result =
			ModelReconciler.Reconcile("cloud", Backend(contextLength: 32768), pins, snapshot);

		// Assert: the reported 128k wins over the narrower default; no explicit override, so no drift.
		ReconciledModel model = Assert.Single(result.Models);
		Assert.Equal(131072, model.ContextLength);
		Assert.False(model.ExplicitContextOverride);
		Assert.Equal(131072, model.DiscoveredContextLength);
		Assert.False(model.HasContextDrift);
		Assert.False(model.IsDrifted);
	}

	// --- 8. Invalid args ---

	/// <summary>
	/// Verifies that a <see langword="null"/> backend name is rejected.
	/// </summary>
	[Fact]
	public void Reconcile_WhenBackendNameIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() => ModelReconciler.Reconcile(null!, Backend(), [], []));
		Assert.Equal("backendName", exception.ParamName);
	}

	/// <summary>
	/// Verifies that an empty or whitespace backend name is rejected.
	/// </summary>
	/// <param name="backendName">The invalid backend name under test.</param>
	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void Reconcile_WhenBackendNameIsEmptyOrWhiteSpace_ThrowsArgumentException(string backendName)
	{
		// Act + Assert
		var exception =
			Assert.Throws<ArgumentException>(() => ModelReconciler.Reconcile(backendName, Backend(), [], []));
		Assert.Equal("backendName", exception.ParamName);
	}

	/// <summary>
	/// Verifies that a <see langword="null"/> backend options argument is rejected.
	/// </summary>
	[Fact]
	public void Reconcile_WhenBackendIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() => ModelReconciler.Reconcile("cloud", null!, [], []));
		Assert.Equal("backend", exception.ParamName);
	}

	/// <summary>
	/// Verifies that a <see langword="null"/> pins argument is rejected.
	/// </summary>
	[Fact]
	public void Reconcile_WhenPinsIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception =
			Assert.Throws<ArgumentNullException>(() => ModelReconciler.Reconcile("cloud", Backend(), null!, []));
		Assert.Equal("pins", exception.ParamName);
	}

	/// <summary>
	/// Verifies that a <see langword="null"/> snapshot argument is rejected.
	/// </summary>
	[Fact]
	public void Reconcile_WhenSnapshotIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception =
			Assert.Throws<ArgumentNullException>(() => ModelReconciler.Reconcile("cloud", Backend(), [], null!));
		Assert.Equal("snapshot", exception.ParamName);
	}

	#endregion

	#region ReconcileBackend()

	// --- Mode rule: PlugAndPlay drops the registry; Hybrid and Explicit honor it ---

	/// <summary>
	/// Verifies that <see cref="ModelReconciler.ReconcileBackend"/> reconciles a
	/// <see cref="OperatingMode.PlugAndPlay"/> backend against no pins — exactly as the runtime catalog ignores
	/// the registry in that mode — so a configured pin never becomes available and every snapshot model surfaces
	/// as discovered.
	/// </summary>
	[Fact]
	public void ReconcileBackend_WhenModeIsPlugAndPlay_IgnoresRegistryPins()
	{
		// Arrange: a pin that, under a registry-honoring mode, would claim the fetched a1 as Available. Under
		// PlugAndPlay the registry is dropped, so a1 must surface as Discovered and the pin must add no row. The
		// candidate's snapshot-time client name is ignored — a discovered row's exposed name is recomputed from
		// the backend's current (here absent) prefix, so it is the bare upstream id.
		BackendOptions backend = BackendWith(OperatingMode.PlugAndPlay, Pin("alpha", upstream: "a1"));
		IReadOnlyList<DiscoveryCandidate> snapshot = [Disc("a1", clientName: "alpha")];

		// Act
		ReconciliationResult result =
			ModelReconciler.ReconcileBackend("cloud", backend, OperatingMode.PlugAndPlay, snapshot);

		// Assert: the pin was ignored — the single model is Discovered, nothing Available/Unavailable. PlugAndPlay
		// auto-exposes discovered models, so the row stays exposed under its prefix-derived name.
		ReconciledModel model = Assert.Single(result.Models);
		AssertModel(model, "a1", "cloud", "a1", ReconciledModelState.Discovered, null, exposedName: "a1");
		AssertCounts(result, available: 0, unavailable: 0, discovered: 1);
	}

	/// <summary>
	/// Verifies that <see cref="ModelReconciler.ReconcileBackend"/> reconciles a registry-honoring backend
	/// against its own <see cref="BackendOptions.Models"/>, so a pin the snapshot still offers is available and
	/// an unpinned snapshot model is discovered. <see cref="OperatingMode.Hybrid"/> stands in for every
	/// non-PlugAndPlay mode, as the production code branches solely on PlugAndPlay and treats all others alike.
	/// </summary>
	[Fact]
	public void ReconcileBackend_WhenModeHonorsRegistry_ReconcilesAgainstBackendPins()
	{
		// Arrange: a Hybrid backend pinning "alpha" (a1) the snapshot still offers, plus an unpinned b1. Hybrid
		// is the representative registry-honoring mode; Explicit follows the identical branch.
		BackendOptions backend = BackendWith(OperatingMode.Hybrid, Pin("alpha", upstream: "a1"));
		IReadOnlyList<DiscoveryCandidate> snapshot = [Disc("a1"), Disc("b1")];

		// Act
		ReconciliationResult result =
			ModelReconciler.ReconcileBackend("cloud", backend, OperatingMode.Hybrid, snapshot);

		// Assert: the pin is honored (Available) in registry order, then the unpinned model is Discovered.
		Assert.Equal(2, result.Models.Count);
		AssertModel(result.Models[0], "alpha", "cloud", "a1", ReconciledModelState.Available, null);
		AssertModel(result.Models[1], "b1", "cloud", "b1", ReconciledModelState.Discovered, null);
		AssertCounts(result, available: 1, unavailable: 0, discovered: 1);
	}

	/// <summary>
	/// Verifies that under <see cref="OperatingMode.Explicit"/> a discovered (unpinned) row is flagged
	/// <see cref="ReconciledModel.IsExposed"/> = <see langword="false"/> — the runtime catalog exposes the
	/// registry alone in that mode, so an unpinned model is listed for promotion but never auto-exposed — while a
	/// pinned row stays exposed. The row is still emitted (the operator must see it to pin it); only its exposure
	/// flag reflects that the proxy does not serve it yet.
	/// </summary>
	[Fact]
	public void ReconcileBackend_WhenModeIsExplicit_FlagsDiscoveredRowsNotExposed()
	{
		// Arrange: an Explicit backend pinning "alpha" (a1) the snapshot offers, plus an unpinned b1. Only the
		// pinned model is exposed at runtime; b1 is listed but not auto-exposed.
		BackendOptions backend = BackendWith(OperatingMode.Explicit, Pin("alpha", upstream: "a1"));
		IReadOnlyList<DiscoveryCandidate> snapshot = [Disc("a1"), Disc("b1")];

		// Act
		ReconciliationResult result =
			ModelReconciler.ReconcileBackend("cloud", backend, OperatingMode.Explicit, snapshot);

		// Assert: the pin is Available and exposed; the discovered row is present but flagged not-exposed.
		Assert.Equal(2, result.Models.Count);
		AssertModel(result.Models[0], "alpha", "cloud", "a1", ReconciledModelState.Available, null);
		AssertModel(result.Models[1], "b1", "cloud", "b1", ReconciledModelState.Discovered, null, isExposed: false);
		AssertCounts(result, available: 1, unavailable: 0, discovered: 1);
	}

	/// <summary>
	/// Verifies that under <see cref="OperatingMode.Hybrid"/> a discovered (unpinned) row stays
	/// <see cref="ReconciledModel.IsExposed"/> = <see langword="true"/> — Hybrid auto-exposes discovered models
	/// alongside its pins, so the not-exposed flag is specific to Explicit and must not bleed into other
	/// registry-honoring modes.
	/// </summary>
	[Fact]
	public void ReconcileBackend_WhenModeIsHybrid_KeepsDiscoveredRowsExposed()
	{
		// Arrange: a Hybrid backend with an unpinned discovered model the catalog would auto-expose at runtime.
		BackendOptions backend = BackendWith(OperatingMode.Hybrid);
		IReadOnlyList<DiscoveryCandidate> snapshot = [Disc("b1")];

		// Act
		ReconciliationResult result =
			ModelReconciler.ReconcileBackend("cloud", backend, OperatingMode.Hybrid, snapshot);

		// Assert: the discovered row is exposed, since Hybrid auto-exposes it.
		ReconciledModel model = Assert.Single(result.Models);
		AssertModel(model, "b1", "cloud", "b1", ReconciledModelState.Discovered, null, isExposed: true);
	}

	// --- Invalid args ---

	/// <summary>
	/// Verifies that a <see langword="null"/> backend name is rejected.
	/// </summary>
	[Fact]
	public void ReconcileBackend_WhenBackendNameIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception =
			Assert.Throws<ArgumentNullException>(() =>
				ModelReconciler.ReconcileBackend(null!, Backend(), OperatingMode.Explicit, []));
		Assert.Equal("backendName", exception.ParamName);
	}

	/// <summary>
	/// Verifies that an empty or whitespace backend name is rejected.
	/// </summary>
	/// <param name="backendName">The invalid backend name under test.</param>
	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void ReconcileBackend_WhenBackendNameIsEmptyOrWhiteSpace_ThrowsArgumentException(string backendName)
	{
		// Act + Assert
		var exception =
			Assert.Throws<ArgumentException>(() =>
				ModelReconciler.ReconcileBackend(backendName, Backend(), OperatingMode.Explicit, []));
		Assert.Equal("backendName", exception.ParamName);
	}

	/// <summary>
	/// Verifies that a <see langword="null"/> backend options argument is rejected before the mode is resolved.
	/// </summary>
	[Fact]
	public void ReconcileBackend_WhenBackendIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception =
			Assert.Throws<ArgumentNullException>(() =>
				ModelReconciler.ReconcileBackend("cloud", null!, OperatingMode.Explicit, []));
		Assert.Equal("backend", exception.ParamName);
	}

	/// <summary>
	/// Verifies that a <see langword="null"/> snapshot argument is rejected.
	/// </summary>
	[Fact]
	public void ReconcileBackend_WhenSnapshotIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception =
			Assert.Throws<ArgumentNullException>(() =>
				ModelReconciler.ReconcileBackend("cloud", Backend(), OperatingMode.Explicit, null!));
		Assert.Equal("snapshot", exception.ParamName);
	}

	#endregion

	#region Test infrastructure

	/// <summary>
	/// Builds a <see cref="BackendOptions"/> with an optional model prefix and context-length default; the URL
	/// and key are placeholders since reconciliation performs no I/O.
	/// </summary>
	/// <param name="prefix">The optional client-facing model prefix.</param>
	/// <param name="contextLength">The optional backend context-length default.</param>
	/// <returns>The configured backend options.</returns>
	private static BackendOptions Backend(string? prefix = null, long? contextLength = null) => new()
	{
		BaseUrl = "https://x/v1",
		ProviderType = "openai",
		ApiKey = "placeholder-key",
		ModelPrefix = prefix,
		ContextLength = contextLength is { } value ? (int)value : null
	};

	/// <summary>
	/// Builds a <see cref="BackendOptions"/> declaring the given operating mode and registry pins, for the
	/// mode-aware <see cref="ModelReconciler.ReconcileBackend"/> tests; the URL and key are placeholders since
	/// reconciliation performs no I/O.
	/// </summary>
	/// <param name="mode">The operating mode the backend declares.</param>
	/// <param name="pins">The registry pins to add, in order.</param>
	/// <returns>The configured backend options.</returns>
	private static BackendOptions BackendWith(OperatingMode mode, params ModelRegistrationOptions[] pins)
	{
		var backend = new BackendOptions
		{
			BaseUrl = "https://x/v1",
			ProviderType = "openai",
			ApiKey = "placeholder-key",
			Mode = mode
		};

		foreach (ModelRegistrationOptions pin in pins)
		{
			backend.Models.Add(pin);
		}

		return backend;
	}

	/// <summary>
	/// Builds a registry pin for the given client-facing name, with an optional upstream alias, context length,
	/// and tool-support override. The pin carries no backend reference — each entry already belongs to its
	/// backend by position in <see cref="BackendOptions.Models"/>, and the reconciler reconciles every pin it is
	/// handed.
	/// </summary>
	/// <param name="name">The client-facing model name.</param>
	/// <param name="upstream">The optional upstream alias; when omitted the name is used upstream.</param>
	/// <param name="contextLength">The optional pinned context length.</param>
	/// <param name="supportsTools">The optional tool-support override.</param>
	/// <returns>The configured registry entry.</returns>
	private static ModelRegistrationOptions Pin(
		string  name,
		string? upstream      = null,
		long?   contextLength = null,
		bool?   supportsTools = null) => new()
	{
		Name = name,
		UpstreamModel = upstream,
		ContextLength = contextLength is { } value ? (int)value : null,
		SupportsTools = supportsTools
	};

	/// <summary>
	/// Builds a resolved snapshot candidate with an optional client-facing alias, context length, and capabilities.
	/// </summary>
	/// <param name="id">The upstream model identifier.</param>
	/// <param name="clientName">The optional client-facing model name; when omitted, <paramref name="id"/> is used.</param>
	/// <param name="contextLength">The optional context length the backend reported.</param>
	/// <param name="capabilities">The optional listed capabilities (metadata-rich providers).</param>
	/// <returns>The resolved discovery candidate.</returns>
	private static DiscoveryCandidate Disc(
		string             id,
		string?            clientName    = null,
		long?              contextLength = null,
		ModelCapabilities? capabilities  = null) => new(clientName ?? id, id, contextLength, capabilities);

	/// <summary>
	/// Asserts a reconciled row's complete scalar state, including that <see cref="ReconciledModel.IsPinned"/>
	/// agrees with the row's <paramref name="state"/>. The expected <see cref="ReconciledModel.ExposedName"/>
	/// defaults to <paramref name="name"/> — the no-prefix case where the exposed name equals the bare name — so
	/// only prefixed scenarios pass <paramref name="exposedName"/> explicitly.
	/// </summary>
	/// <param name="actual">The reconciled model to verify.</param>
	/// <param name="name">The expected bare model name — the registry/upstream identity, never prefixed.</param>
	/// <param name="backendName">The expected backend name.</param>
	/// <param name="upstreamModel">The expected upstream model identifier.</param>
	/// <param name="state">The expected reconciled state.</param>
	/// <param name="contextLength">The expected context window to display (raw reported for discovered rows).</param>
	/// <param name="exposedName">
	/// The expected client-facing name; when <see langword="null"/> it defaults to <paramref name="name"/>, the
	/// no-prefix case where the exposed name equals the bare name.
	/// </param>
	/// <param name="isExposed">
	/// The expected <see cref="ReconciledModel.IsExposed"/>; defaults to <see langword="true"/>, the common case
	/// (every pinned row and every discovered row under an auto-exposing mode). Only an Explicit-mode discovered
	/// row is <see langword="false"/>.
	/// </param>
	private static void AssertModel(
		ReconciledModel      actual,
		string               name,
		string               backendName,
		string               upstreamModel,
		ReconciledModelState state,
		long?                contextLength,
		string?              exposedName = null,
		bool                 isExposed   = true)
	{
		Assert.Equal(name, actual.Name);
		Assert.Equal(exposedName ?? name, actual.ExposedName);
		Assert.Equal(backendName, actual.BackendName);
		Assert.Equal(upstreamModel, actual.UpstreamModel);
		Assert.Equal(state, actual.State);
		Assert.Equal(contextLength, actual.ContextLength);
		Assert.Equal(state is ReconciledModelState.Available or ReconciledModelState.Unavailable, actual.IsPinned);
		Assert.Equal(isExposed, actual.IsExposed);
	}

	/// <summary>
	/// Asserts the three headline counts on a <see cref="ReconciliationResult"/>.
	/// </summary>
	/// <param name="result">The result to verify.</param>
	/// <param name="available">The expected available count.</param>
	/// <param name="unavailable">The expected unavailable count.</param>
	/// <param name="discovered">The expected discovered count.</param>
	private static void AssertCounts(
		ReconciliationResult result,
		int                  available,
		int                  unavailable,
		int                  discovered)
	{
		Assert.Equal(available, result.AvailableCount);
		Assert.Equal(unavailable, result.UnavailableCount);
		Assert.Equal(discovered, result.DiscoveredCount);
	}

	#endregion
}
