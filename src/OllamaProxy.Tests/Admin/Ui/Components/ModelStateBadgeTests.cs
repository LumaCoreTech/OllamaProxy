// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using AngleSharp.Dom;

using Bunit;

using OllamaProxy.Admin.Reconciliation;
using OllamaProxy.Admin.Ui.Components;
using OllamaProxy.Configuration;

namespace OllamaProxy.Tests.Admin.Ui.Components;

/// <summary>
/// Tests for <see cref="ModelStateBadge"/>: a reconciled model's state as a coloured pill — state -> colour +
/// label, plus a tooltip that, for a discovered model, depends on the backend's operating mode.
/// </summary>
/// <remarks>
/// <see cref="ModelStateBadge"/> is a thin mapping over the shared <see cref="Badge"/> primitive, so these tests
/// verify the translation it owns end-to-end through the rendered &lt;span&gt; the inner Badge emits:
/// <list type="number">
///     <item>
///         <description>
///         State -> variant + label: Available is a green "Available" pill, Unavailable a red "Unavailable" pill,
///         Discovered a blue "Discovered" pill (Render_WithState_AppliesVariantAndLabel).
///         </description>
///     </item>
///     <item>
///         <description>
///         Non-discovered tooltip is mode-independent: Available and Unavailable keep their fixed description
///         across every operating mode (and the absent mode), which the test sweeps to prove the mode is ignored
///         for these states rather than assuming it from one sample
///         (Render_WithNonDiscoveredState_SetsModeIndependentTooltip).
///         </description>
///     </item>
///     <item>
///         <description>
///         Discovered tooltip is mode-dependent: PlugAndPlay and Hybrid describe auto-exposure differently, while
///         Explicit — and an absent mode, which conservatively falls back to Explicit — describe non-exposure
///         (Render_WithDiscoveredState_SetsModeDependentTooltip).
///         </description>
///     </item>
/// </list>
/// Colour is asserted through the rendered class attribute (badge + badge-*), which is the observable proxy for
/// the BadgeVariant the component selected. Tooltip strings are golden values matched exactly.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class ModelStateBadgeTests : BunitContext
{
	/// <summary>
	/// The state-to-appearance scenarios: each reconciled state paired with the exact class attribute (base
	/// <c>badge</c> plus colour class) and the visible label the pill must render.
	/// </summary>
	public static TheoryData<ReconciledModelState, string, string> StateAppearanceCases => new()
	{
		{ ReconciledModelState.Available, "badge badge-success", "Available" },
		{ ReconciledModelState.Unavailable, "badge badge-danger", "Unavailable" },
		{ ReconciledModelState.Discovered, "badge badge-info", "Discovered" }
	};

	/// <summary>
	/// The fixed tooltip an <see cref="ReconciledModelState.Available"/> pill must always show, independent of the
	/// backend's operating mode.
	/// </summary>
	private const string AvailableTooltip = "Pinned in the registry and currently reported by the backend.";

	/// <summary>
	/// The fixed tooltip an <see cref="ReconciledModelState.Unavailable"/> pill must always show, independent of
	/// the backend's operating mode.
	/// </summary>
	private const string UnavailableTooltip =
		"Pinned in the registry, but the backend stopped reporting it. The pin is kept so you can decide " +
		"whether to remove it.";

	/// <summary>
	/// The mode-independent tooltip scenarios: each non-discovered state paired with every operating mode — and
	/// the absent (<see langword="null"/>) mode — all mapping to the single fixed description that state must show.
	/// Sweeping the whole mode domain (mirroring <see cref="DiscoveredTooltipCases"/>) is what proves the mode is
	/// ignored for these states, rather than trusting one arbitrary sample.
	/// </summary>
	public static TheoryData<ReconciledModelState, OperatingMode?, string> NonDiscoveredTooltipCases => new()
	{
		{ ReconciledModelState.Available, OperatingMode.PlugAndPlay, AvailableTooltip },
		{ ReconciledModelState.Available, OperatingMode.Hybrid, AvailableTooltip },
		{ ReconciledModelState.Available, OperatingMode.Explicit, AvailableTooltip },
		{ ReconciledModelState.Available, null, AvailableTooltip },
		{ ReconciledModelState.Unavailable, OperatingMode.PlugAndPlay, UnavailableTooltip },
		{ ReconciledModelState.Unavailable, OperatingMode.Hybrid, UnavailableTooltip },
		{ ReconciledModelState.Unavailable, OperatingMode.Explicit, UnavailableTooltip },
		{ ReconciledModelState.Unavailable, null, UnavailableTooltip }
	};

	/// <summary>
	/// The discovered-state tooltip scenarios keyed by operating mode. PlugAndPlay and Hybrid describe
	/// auto-exposure; Explicit and an absent mode (<see langword="null"/>, which falls back to the conservative
	/// Explicit description) describe non-exposure.
	/// </summary>
	public static TheoryData<OperatingMode?, string> DiscoveredTooltipCases => new()
	{
		{
			OperatingMode.PlugAndPlay,
			"Reported by the backend and auto-exposed because this backend is in PlugAndPlay mode. Pin it to " +
			"make the exposure explicit."
		},
		{
			OperatingMode.Hybrid,
			"Reported by the backend and auto-exposed because this backend is in Hybrid mode. Pin it to " +
			"override its settings."
		},
		{
			OperatingMode.Explicit,
			"Reported by the backend but not exposed because this backend is in Explicit mode. Pin it to " +
			"expose it."
		},
		{
			// No mode supplied: the tooltip conservatively falls back to the Explicit (non-exposed) description.
			null,
			"Reported by the backend but not exposed because this backend is in Explicit mode. Pin it to " +
			"expose it."
		}
	};

	/// <summary>
	/// Verifies that each reconciled state renders the matching badge colour class and visible label through the
	/// inner <see cref="Badge"/>'s span.
	/// </summary>
	/// <param name="state">The reconciled state to render.</param>
	/// <param name="expectedClass">The exact class attribute the pill must carry.</param>
	/// <param name="expectedLabel">The visible label the pill must show.</param>
	[Theory]
	[MemberData(nameof(StateAppearanceCases))]
	public void Render_WithState_AppliesVariantAndLabel(
		ReconciledModelState state,
		string               expectedClass,
		string               expectedLabel)
	{
		// Arrange

		// Act
		IRenderedComponent<ModelStateBadge> cut = Render<ModelStateBadge>(parameters => parameters
			.Add(badge => badge.State, state));

		// Assert: the colour class (the observable proxy for the selected variant) and the label both match.
		IElement span = cut.Find("span");
		Assert.Equal(expectedClass, span.ClassName);
		Assert.Equal(expectedLabel, span.TextContent);
	}

	/// <summary>
	/// Verifies that each non-discovered state carries its fixed tooltip for every operating mode — including the
	/// absent mode — which proves the mode genuinely has no effect on these states instead of checking a single
	/// arbitrary mode.
	/// </summary>
	/// <param name="state">The non-discovered reconciled state to render.</param>
	/// <param name="mode">The operating mode to supply, swept across the full domain (and the absent case).</param>
	/// <param name="expectedTooltip">The fixed tooltip the state must show regardless of the mode.</param>
	[Theory]
	[MemberData(nameof(NonDiscoveredTooltipCases))]
	public void Render_WithNonDiscoveredState_SetsModeIndependentTooltip(
		ReconciledModelState state,
		OperatingMode?       mode,
		string               expectedTooltip)
	{
		// Act
		IRenderedComponent<ModelStateBadge> cut = Render<ModelStateBadge>(parameters => parameters
			.Add(badge => badge.State, state)
			.Add(badge => badge.Mode, mode));

		// Assert
		IElement span = cut.Find("span");
		Assert.Equal(expectedTooltip, span.GetAttribute("title"));
	}

	/// <summary>
	/// Verifies that a discovered model's tooltip reflects the backend's operating mode, including the fallback
	/// to the conservative Explicit description when no mode is supplied.
	/// </summary>
	/// <param name="mode">The operating mode to render, or <see langword="null"/> for the fallback case.</param>
	/// <param name="expectedTooltip">The tooltip the discovered state must show for the mode.</param>
	[Theory]
	[MemberData(nameof(DiscoveredTooltipCases))]
	public void Render_WithDiscoveredState_SetsModeDependentTooltip(OperatingMode? mode, string expectedTooltip)
	{
		// Arrange

		// Act
		IRenderedComponent<ModelStateBadge> cut = Render<ModelStateBadge>(parameters => parameters
			.Add(badge => badge.State, ReconciledModelState.Discovered)
			.Add(badge => badge.Mode, mode));

		// Assert
		IElement span = cut.Find("span");
		Assert.Equal(expectedTooltip, span.GetAttribute("title"));
	}
}
