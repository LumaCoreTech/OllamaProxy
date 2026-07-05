// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Admin.Reconciliation;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Tests.Admin.Reconciliation;

/// <summary>
/// Tests for <see cref="ReconciledModel"/> drift detection on a single reconciled row: when does a still-honored
/// pin count as "stale"?
/// </summary>
/// <remarks>
/// A pin is drifted when the values the operator recorded no longer match what the backend now reports. Drift is
/// only meaningful for an Available pin (one the snapshot still offers), because that is the only state that
/// carries the backend's reported values to compare against. These tests pin down the three derived properties
/// that express that idea:
/// <list type="number">
///     <item>
///         <description>
///         HasCapabilityDrift: the four functional flags are compared; ModelCapabilities.Source is deliberately
///         ignored (it records provenance, which always differs between a pin and a backend value), and the
///         comparison is gated on the Available state plus both sides being known. Every row pairs a Configured
///         pin against a ProviderMetadata backend value, so the "identical flags" row doubles as the proof that
///         differing provenance alone is not drift.
///         </description>
///     </item>
///     <item>
///         <description>
///         HasContextDrift: the effective context windows are compared, gated on the Available state plus both
///         windows being known.
///         </description>
///     </item>
///     <item>
///         <description>IsDrifted: the OR of the two — capability drift, context drift, or both.</description>
///     </item>
/// </list>
/// ReconciledModelState and ModelCapabilities are built in each row's body via the helpers rather than passed as
/// TheoryData columns, so every column stays a serializable primitive and xUnit can enumerate and re-run each
/// case on its own. Because the file covers several members of the same type, each is isolated in its own
/// #region.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class ReconciledModelTests
{
	#region HasCapabilityDrift

	/// <summary>
	/// The capability-drift scenarios. Each row fixes the pin at completion-only (Configured) and varies the
	/// backend-reported flags (ProviderMetadata), so a single differing flag isolates that flag's
	/// participation. Columns: scenario, available, pinned-unknown, discovered-unknown, the four discovered
	/// flags (completion, tools, vision, embeddings), and the expected drift outcome.
	/// </summary>
	public static TheoryData<string, bool, bool, bool, bool, bool, bool, bool, bool> CapabilityDriftCases => new()
	{
		// State guard: a non-Available row never drifts, even with every flag flipped.
		{
			"not available: state guard short-circuits despite differing flags", false, false, false, true, true, true,
			false, false
		},

		// Unknown guards: nothing to compare on one side.
		{ "available, pinned capabilities unknown", true, true, false, false, false, false, false, false },
		{ "available, discovered capabilities unknown", true, false, true, false, false, false, false, false },

		// One row per flag: each flips a single discovered flag over the completion-only pin baseline.
		{ "available, completion flag differs", true, false, false, false, false, false, false, true },
		{ "available, tools flag differs", true, false, false, true, true, false, false, true },
		{ "available, vision flag differs", true, false, false, true, false, true, false, true },
		{ "available, embeddings flag differs", true, false, false, true, false, false, true, true },

		// Identical flags: no drift even though pin is Configured and discovered is ProviderMetadata.
		{ "available, flags identical but provenance differs", true, false, false, true, false, false, false, false }
	};

	/// <summary>
	/// Verifies that <see cref="ReconciledModel.HasCapabilityDrift"/> compares the four functional capability
	/// flags while ignoring <see cref="ModelCapabilities.Source"/>, and is gated on the
	/// <see cref="ReconciledModelState.Available"/> state with both capability sets known.
	/// </summary>
	/// <param name="scenario">A human-readable description of the case under test.</param>
	/// <param name="available">Whether the row is <see cref="ReconciledModelState.Available"/>.</param>
	/// <param name="pinnedUnknown">Whether the pin's capabilities are <see langword="null"/>.</param>
	/// <param name="discoveredUnknown">Whether the backend's reported capabilities are <see langword="null"/>.</param>
	/// <param name="discoveredCompletion">The backend's reported completion flag.</param>
	/// <param name="discoveredTools">The backend's reported tools flag.</param>
	/// <param name="discoveredVision">The backend's reported vision flag.</param>
	/// <param name="discoveredEmbeddings">The backend's reported embeddings flag.</param>
	/// <param name="expected">The expected drift outcome.</param>
	[Theory]
	[MemberData(nameof(CapabilityDriftCases))]
	public void HasCapabilityDrift_AcrossStatesAndFlags_ReflectsFunctionalDifference(
		string scenario,
		bool   available,
		bool   pinnedUnknown,
		bool   discoveredUnknown,
		bool   discoveredCompletion,
		bool   discoveredTools,
		bool   discoveredVision,
		bool   discoveredEmbeddings,
		bool   expected)
	{
		_ = scenario;

		// Arrange: pin is completion-only (Configured); discovered carries the row's flags (ProviderMetadata).
		// Either side becomes null when its "unknown" flag is set. Context windows are held equal so only the
		// capability comparison is under test.
		ModelCapabilities? pinned = pinnedUnknown
			                            ? null
			                            : Caps(true, false, false, false, CapabilitySource.Configured);
		ModelCapabilities? discovered = discoveredUnknown
			                                ? null
			                                : Caps(
				                                discoveredCompletion,
				                                discoveredTools,
				                                discoveredVision,
				                                discoveredEmbeddings,
				                                CapabilitySource.ProviderMetadata);
		var sut = new ReconciledModel(
			"model",
			"model",
			"cloud",
			"model",
			pinned,
			4096,
			available ? ReconciledModelState.Available : ReconciledModelState.Unavailable,
			DiscoveredCapabilities: discovered,
			DiscoveredContextLength: 4096);

		// Act
		bool result = sut.HasCapabilityDrift;

		// Assert
		Assert.Equal(expected, result);
	}

	#endregion

	#region HasContextDrift

	/// <summary>
	/// The context-drift scenarios. Columns: scenario, available, pinned-unknown, discovered-unknown,
	/// explicit-override, the pinned window, the discovered window, and the expected drift outcome. A window of
	/// <c>0</c> is unused whenever its matching "unknown" flag is set.
	/// </summary>
	public static TheoryData<string, bool, bool, bool, bool, long, long, bool> ContextDriftCases => new()
	{
		// State guard: a non-Available row never drifts, even with differing windows.
		{
			"not available: state guard short-circuits despite differing windows", false, false, false, true, 4096,
			8192, false
		},

		// Unknown guards: nothing to compare on one side or the other.
		{ "available, pinned window unknown", true, true, false, true, 0, 8192, false },
		{ "available, discovered window unknown", true, false, true, true, 4096, 0, false },
		{ "available, both windows unknown", true, true, true, true, 0, 0, false },

		// Override guard: an inherited-context pin (no explicit override) never drifts, even with differing windows.
		{
			"available, no explicit override: inherited context never drifts", true, false, false, false, 4096, 8192,
			false
		},

		// Equal windows do not drift; either direction of inequality does (with an explicit override in play).
		{ "available, windows equal", true, false, false, true, 4096, 4096, false },
		{ "available, backend reports a wider window", true, false, false, true, 4096, 8192, true },
		{ "available, backend reports a narrower window", true, false, false, true, 8192, 4096, true }
	};

	/// <summary>
	/// Verifies that <see cref="ReconciledModel.HasContextDrift"/> compares the pinned and discovered context
	/// windows only when the pin has an explicit context override, gated on the
	/// <see cref="ReconciledModelState.Available"/> state with both windows known.
	/// </summary>
	/// <param name="scenario">A human-readable description of the case under test.</param>
	/// <param name="available">Whether the row is <see cref="ReconciledModelState.Available"/>.</param>
	/// <param name="pinnedUnknown">Whether the pin's context window is <see langword="null"/>.</param>
	/// <param name="discoveredUnknown">Whether the backend's reported context window is <see langword="null"/>.</param>
	/// <param name="explicitOverride">Whether the pin carries an explicit context override (the gate context drift requires).</param>
	/// <param name="pinnedContext">The pin's configured context window when known.</param>
	/// <param name="discoveredContext">The backend's reported context window when known.</param>
	/// <param name="expected">The expected drift outcome.</param>
	[Theory]
	[MemberData(nameof(ContextDriftCases))]
	public void HasContextDrift_AcrossStatesAndWindows_ReflectsWindowDifference(
		string scenario,
		bool   available,
		bool   pinnedUnknown,
		bool   discoveredUnknown,
		bool   explicitOverride,
		long   pinnedContext,
		long   discoveredContext,
		bool   expected)
	{
		_ = scenario;

		// Arrange: capabilities are held unknown so only the context comparison is under test. Either window
		// becomes null when its "unknown" flag is set. ExplicitContextOverride is driven by the row, since context
		// drift is reported only for a pin that carries an explicit override.
		var sut = new ReconciledModel(
			"model",
			"model",
			"cloud",
			"model",
			Capabilities: null,
			pinnedUnknown ? null : pinnedContext,
			available ? ReconciledModelState.Available : ReconciledModelState.Unavailable,
			ExplicitContextOverride: explicitOverride,
			DiscoveredCapabilities: null,
			DiscoveredContextLength: discoveredUnknown ? null : discoveredContext);

		// Act
		bool result = sut.HasContextDrift;

		// Assert
		Assert.Equal(expected, result);
	}

	#endregion

	#region IsDrifted

	/// <summary>
	/// The combined-drift scenarios over an <see cref="ReconciledModelState.Available"/> row. Columns:
	/// scenario, whether the backend adds tool support the pin lacks (capability drift), the pinned window,
	/// the discovered window, and the expected combined outcome.
	/// </summary>
	public static TheoryData<string, bool, long, long, bool> IsDriftedCases => new()
	{
		{ "neither capability nor context drift", false, 4096, 4096, false },
		{ "capability drift only", true, 4096, 4096, true },
		{ "context drift only", false, 4096, 8192, true },
		{ "both capability and context drift", true, 4096, 8192, true }
	};

	/// <summary>
	/// Verifies that <see cref="ReconciledModel.IsDrifted"/> is the logical OR of
	/// <see cref="ReconciledModel.HasCapabilityDrift"/> and <see cref="ReconciledModel.HasContextDrift"/> when the
	/// pin has an explicit context override.
	/// </summary>
	/// <param name="scenario">A human-readable description of the case under test.</param>
	/// <param name="discoveredAddsTools">Whether the backend reports tool support the completion-only pin lacks.</param>
	/// <param name="pinnedContext">The pin's configured context window.</param>
	/// <param name="discoveredContext">The backend's reported context window.</param>
	/// <param name="expected">The expected combined drift outcome.</param>
	[Theory]
	[MemberData(nameof(IsDriftedCases))]
	public void IsDrifted_WhenAvailable_CombinesCapabilityAndContextDrift(
		string scenario,
		bool   discoveredAddsTools,
		long   pinnedContext,
		long   discoveredContext,
		bool   expected)
	{
		_ = scenario;

		// Arrange: pin is completion-only (Configured) with an explicit context override; the backend optionally
		// adds tools, and the windows may differ — together exercising each side of the OR.
		var sut = new ReconciledModel(
			"model",
			"model",
			"cloud",
			"model",
			Caps(true, false, false, false, CapabilitySource.Configured),
			pinnedContext,
			ReconciledModelState.Available,
			ExplicitContextOverride: true,
			DiscoveredCapabilities: Caps(true, discoveredAddsTools, false, false, CapabilitySource.ProviderMetadata),
			DiscoveredContextLength: discoveredContext);

		// Act
		bool result = sut.IsDrifted;

		// Assert
		Assert.Equal(expected, result);
	}

	#endregion

	#region Test infrastructure

	/// <summary>
	/// Builds a <see cref="ModelCapabilities"/> from the four functional flags and a provenance source.
	/// </summary>
	/// <param name="completion">Whether completion is supported.</param>
	/// <param name="tools">Whether tool calling is supported.</param>
	/// <param name="vision">Whether vision input is supported.</param>
	/// <param name="embeddings">Whether embeddings are supported.</param>
	/// <param name="source">The provenance of the capability set.</param>
	/// <returns>The configured capabilities.</returns>
	private static ModelCapabilities Caps(
		bool             completion,
		bool             tools,
		bool             vision,
		bool             embeddings,
		CapabilitySource source) => new(completion, tools, vision, embeddings, source);

	#endregion
}
