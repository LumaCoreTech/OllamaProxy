// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Admin.Ui.Components;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Tests.Admin.Ui.Components;

/// <summary>
/// Tests for <see cref="CapabilityChipBuilder"/>: how a resolved ModelCapabilities set becomes the ordered pills
/// the admin surface shows.
/// </summary>
/// <remarks>
/// The builder walks completion, tools, vision, embeddings in that fixed order and, per capability, decides among
/// four outcomes:
/// <list type="number">
///     <item>
///         <description>
///         Confirmed (supported, conclusive) -> a solid "cap-chip cap-chip-supported" chip.
///         </description>
///     </item>
///     <item>
///         <description>
///         Supported-but-unconfirmed (supported, inconclusive; only completion is fail-open) -> a dashed
///         "cap-chip cap-chip-supported-unconfirmed" chip carrying a trailing "?".
///         </description>
///     </item>
///     <item>
///         <description>
///         Off-but-unconfirmed (not supported, inconclusive) -> an amber "cap-chip cap-chip-inconclusive" chip
///         with "?".
///         </description>
///     </item>
///     <item>
///         <description>
///         Measured-unsupported (not supported, conclusive) -> no chip at all, so the cell never reads as a list
///         of noes.
///         </description>
///     </item>
/// </list>
/// The tests below pin down each outcome, the fixed capability order, and the empty result, with exact
/// css/title/text assertions (golden values, not a re-derivation of the production formula).
/// </remarks>
[Trait("Category", "Unit")]
public sealed class CapabilityChipBuilderTests
{
	/// <summary>
	/// The single-capability classification scenarios, exercised through completion because it is the sole
	/// fail-open probe. Columns: scenario, whether completion is supported, whether its probe stayed inconclusive,
	/// and the expected chip's CSS class, tooltip, and visible text.
	/// </summary>
	public static TheoryData<string, bool, bool, string, string, string> SingleCapabilityCases => new()
	{
		{
			"confirmed: supported and conclusive -> solid chip, no question mark",
			true, false,
			"cap-chip cap-chip-supported",
			"Supports completion (confirmed by probe or backend metadata).",
			"completion"
		},
		{
			"supported-but-unconfirmed: fail-open completion whose probe was inconclusive -> dashed chip with '?'",
			true, true,
			"cap-chip cap-chip-supported-unconfirmed",
			"The completion probe stayed inconclusive (it timed out or kept failing), so this is unconfirmed — " +
			"the model is kept capable anyway (fail-open) and stays exposed. Pin the model to set its " +
			"capabilities explicitly.",
			"completion ?"
		},
		{
			"off-but-unconfirmed: not supported but inconclusive -> amber chip with '?'",
			false, true,
			"cap-chip cap-chip-inconclusive",
			"The completion probe stayed inconclusive (it timed out or kept failing), so this is unconfirmed — " +
			"the model may still support it. Pin the model to set its capabilities explicitly.",
			"completion ?"
		}
	};

	/// <summary>
	/// Verifies that a capability set with every capability confirmed produces four solid chips in the fixed
	/// completion, tools, vision, embeddings order, each with its confirmed CSS class, tooltip, and bare label.
	/// </summary>
	[Fact]
	public void BuildChips_WhenAllCapabilitiesConfirmed_ReturnsSolidChipsInCapabilityOrder()
	{
		// Arrange: every functional flag set and no inconclusive overlay. Source is irrelevant to chip building
		// (the builder reads only the functional flags and the inconclusive overlay), so it is held constant.
		var capabilities = new ModelCapabilities(
			SupportsCompletion: true,
			SupportsTools: true,
			SupportsVision: true,
			SupportsEmbeddings: true,
			CapabilitySource.Probed);

		// Act
		IReadOnlyList<CapabilityChip> result = CapabilityChipBuilder.BuildChips(capabilities);

		// Assert: all four chips, in capability order, each solid and labelled by the bare capability name.
		Assert.Equal(4, result.Count);
		AssertChip(
			result[0],
			"cap-chip cap-chip-supported",
			"Supports completion (confirmed by probe or backend metadata).",
			"completion");
		AssertChip(
			result[1],
			"cap-chip cap-chip-supported",
			"Supports tools (confirmed by probe or backend metadata).",
			"tools");
		AssertChip(
			result[2],
			"cap-chip cap-chip-supported",
			"Supports vision (confirmed by probe or backend metadata).",
			"vision");
		AssertChip(
			result[3],
			"cap-chip cap-chip-supported",
			"Supports embeddings (confirmed by probe or backend metadata).",
			"embeddings");
	}

	/// <summary>
	/// Verifies that a single capability's supported/inconclusive combination maps to the expected chip CSS
	/// class, tooltip, and text, covering the three chip kinds the builder can emit.
	/// </summary>
	/// <param name="scenario">A human-readable description of the case under test.</param>
	/// <param name="supported">Whether the capability's functional flag is set.</param>
	/// <param name="inconclusive">Whether the capability's probe stayed inconclusive.</param>
	/// <param name="expectedCssClass">The expected chip CSS class.</param>
	/// <param name="expectedTitle">The expected chip tooltip.</param>
	/// <param name="expectedText">The expected visible chip label.</param>
	[Theory]
	[MemberData(nameof(SingleCapabilityCases))]
	public void BuildChips_WhenSingleCapabilityClassified_ProducesChipWithMatchingCssTitleAndText(
		string scenario,
		bool   supported,
		bool   inconclusive,
		string expectedCssClass,
		string expectedTitle,
		string expectedText)
	{
		_ = scenario;

		// Arrange: isolate completion and leave the other three flags off and conclusive so exactly one chip is
		// produced. Source is irrelevant to chip building.
		InconclusiveCapabilities overlay = inconclusive
			                                   ? InconclusiveCapabilities.Completion
			                                   : InconclusiveCapabilities.None;
		var capabilities = new ModelCapabilities(
			SupportsCompletion: supported,
			SupportsTools: false,
			SupportsVision: false,
			SupportsEmbeddings: false,
			CapabilitySource.Probed,
			overlay);

		// Act
		IReadOnlyList<CapabilityChip> result = CapabilityChipBuilder.BuildChips(capabilities);

		// Assert
		CapabilityChip chip = Assert.Single(result);
		AssertChip(chip, expectedCssClass, expectedTitle, expectedText);
	}

	/// <summary>
	/// Verifies that a capability probed as unsupported and conclusive produces no chip, while the surviving
	/// supported and inconclusive capabilities remain in their fixed capability order.
	/// </summary>
	[Fact]
	public void BuildChips_WhenCapabilityMeasuredUnsupported_OmitsThatCapability()
	{
		// Arrange: completion confirmed and vision inconclusive survive; tools and embeddings are
		// measured-unsupported (flag off, not inconclusive) and must be dropped so the cell shows no negatives.
		var capabilities = new ModelCapabilities(
			SupportsCompletion: true,
			SupportsTools: false,
			SupportsVision: false,
			SupportsEmbeddings: false,
			CapabilitySource.Probed,
			InconclusiveCapabilities.Vision);

		// Act
		IReadOnlyList<CapabilityChip> result = CapabilityChipBuilder.BuildChips(capabilities);

		// Assert: only completion (solid) and vision (inconclusive) remain, and in that order — tools and
		// embeddings are omitted rather than rendered as negatives.
		Assert.Equal(2, result.Count);
		AssertChip(
			result[0],
			"cap-chip cap-chip-supported",
			"Supports completion (confirmed by probe or backend metadata).",
			"completion");
		AssertChip(
			result[1],
			"cap-chip cap-chip-inconclusive",
			"The vision probe stayed inconclusive (it timed out or kept failing), so this is unconfirmed — " +
			"the model may still support it. Pin the model to set its capabilities explicitly.",
			"vision ?");
	}

	/// <summary>
	/// Verifies that a capability set with nothing supported and nothing inconclusive produces no chips, so the
	/// component renders its muted "none" placeholder instead of an empty chip row.
	/// </summary>
	[Fact]
	public void BuildChips_WhenNoCapabilitySupportedOrInconclusive_ReturnsEmpty()
	{
		// Arrange: every flag off and conclusive.
		var capabilities = new ModelCapabilities(
			SupportsCompletion: false,
			SupportsTools: false,
			SupportsVision: false,
			SupportsEmbeddings: false,
			CapabilitySource.Default);

		// Act
		IReadOnlyList<CapabilityChip> result = CapabilityChipBuilder.BuildChips(capabilities);

		// Assert
		Assert.Empty(result);
	}

	#region Test infrastructure

	/// <summary>
	/// Asserts a chip's complete observable state: its CSS class, tooltip, and visible text.
	/// </summary>
	/// <param name="chip">The chip to verify.</param>
	/// <param name="expectedCssClass">The expected CSS class.</param>
	/// <param name="expectedTitle">The expected tooltip.</param>
	/// <param name="expectedText">The expected visible text.</param>
	private static void AssertChip(
		CapabilityChip chip,
		string         expectedCssClass,
		string         expectedTitle,
		string         expectedText)
	{
		Assert.Equal(expectedCssClass, chip.CssClass);
		Assert.Equal(expectedTitle, chip.Title);
		Assert.Equal(expectedText, chip.Text);
	}

	#endregion
}
