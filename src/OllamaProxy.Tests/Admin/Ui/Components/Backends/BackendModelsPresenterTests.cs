// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Admin.Reconciliation;
using OllamaProxy.Admin.Ui.Components.Backends;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Tests.Admin.Ui.Components.Backends;

/// <summary>
/// Tests for <see cref="BackendModelsPresenter"/>, the pure display mappings behind the <see cref="BackendModels"/>
/// table: the capability and context summaries, the drift tooltip, the pricing line and its USD formatter, the
/// per-row detail-panel id, and the inline duplicate-name check.
/// </summary>
/// <remarks>
/// Each member answers one small question the table would otherwise embed in markup, and each is pinned to golden
/// values (<see cref="Assert.Equal(object?, object?)"/>) because the strings are the operator-facing copy the
/// table renders. Because the file covers several distinct members, each is isolated in its own <c>#region</c>,
/// ordered to match the presenter. <see cref="BackendModelsPresenter.DriftSummary"/> composes the two summaries,
/// so its cases build real <see cref="ReconciledModel"/> rows that trigger capability and/or context drift, which
/// doubles as end-to-end proof that the pieces read together correctly.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class BackendModelsPresenterTests
{
	#region CapabilitySummary

	/// <summary>
	/// Verifies that a known capability set is summarized as the comma-separated list of its enabled flags in
	/// canonical order, collapsing to <c>"none"</c> when nothing is enabled.
	/// </summary>
	/// <param name="completion">Whether completion is supported.</param>
	/// <param name="tools">Whether tool calling is supported.</param>
	/// <param name="vision">Whether vision input is supported.</param>
	/// <param name="embeddings">Whether embeddings are supported.</param>
	/// <param name="expected">The expected summary string.</param>
	[Theory]
	[InlineData(false, false, false, false, "none")]
	[InlineData(true, false, false, false, "completion")]
	[InlineData(false, true, false, false, "tools")]
	[InlineData(false, false, true, false, "vision")]
	[InlineData(false, false, false, true, "embeddings")]
	[InlineData(true, true, true, true, "completion, tools, vision, embeddings")]
	[InlineData(false, true, false, true, "tools, embeddings")]
	public void CapabilitySummary_WhenKnown_ListsEnabledFlagsInOrder(
		bool   completion,
		bool   tools,
		bool   vision,
		bool   embeddings,
		string expected)
	{
		// Arrange: provenance is irrelevant to the summary, which reads only the four functional flags.
		ModelCapabilities capabilities = Caps(completion, tools, vision, embeddings);

		// Act
		string result = BackendModelsPresenter.CapabilitySummary(capabilities);

		// Assert
		Assert.Equal(expected, result);
	}

	/// <summary>
	/// Verifies that an unknown (null) capability set is rendered as the em-dash placeholder, distinct from the
	/// <c>"none"</c> a known-but-empty set produces.
	/// </summary>
	[Fact]
	public void CapabilitySummary_WhenNull_ReturnsPlaceholder()
	{
		// Act
		string result = BackendModelsPresenter.CapabilitySummary(null);

		// Assert
		Assert.Equal("—", result);
	}

	#endregion

	#region ContextSummary

	/// <summary>
	/// The context-summary cases: a null window collapses to the placeholder, and a known window is formatted
	/// with invariant-culture thousands separators.
	/// </summary>
	public static TheoryData<long?, string> ContextSummaryCases => new()
	{
		{ null, "—" },
		{ 0L, "0" },
		{ 4096L, "4,096" },
		{ 128_000L, "128,000" },
		{ 1_000_000L, "1,000,000" }
	};

	/// <summary>
	/// Verifies that a context window is formatted with thousands separators, or the em-dash placeholder when
	/// none could be resolved.
	/// </summary>
	/// <param name="contextLength">The context window in tokens, or <see langword="null"/>.</param>
	/// <param name="expected">The expected summary string.</param>
	[Theory]
	[MemberData(nameof(ContextSummaryCases))]
	public void ContextSummary_FormatsTokenCountOrPlaceholder(long? contextLength, string expected)
	{
		// Act
		string result = BackendModelsPresenter.ContextSummary(contextLength);

		// Assert
		Assert.Equal(expected, result);
	}

	#endregion

	#region DriftSummary

	/// <summary>
	/// Verifies that a pin drifted only in its capabilities produces a sentence naming the pinned versus
	/// backend-reported capability sets.
	/// </summary>
	[Fact]
	public void DriftSummary_WhenCapabilitiesDrift_DescribesCapabilityDifference()
	{
		// Arrange: an Available pin whose flags (completion-only) differ from the backend's (completion + tools).
		// The discovered context is left unknown so only capability drift is in play.
		var model = new ReconciledModel(
			"model",
			"model",
			"cloud",
			"model",
			Caps(true, false, false, false),
			4096,
			ReconciledModelState.Available,
			DiscoveredCapabilities: Caps(true, true, false, false));

		// Act
		string result = BackendModelsPresenter.DriftSummary(model);

		// Assert
		Assert.Equal(
			"Pinned settings differ from the backend: capabilities are pinned as completion, but the backend reports completion, tools.",
			result);
	}

	/// <summary>
	/// Verifies that a pin drifted only in its context window produces a sentence naming the pinned versus
	/// backend-reported windows.
	/// </summary>
	[Fact]
	public void DriftSummary_WhenContextDrifts_DescribesContextDifference()
	{
		// Arrange: an Available pin with matching flags (no capability drift) but an explicit context override
		// (4,096) that differs from the backend's reported window (8,192). The explicit override is required for
		// context drift — an inherited window never drifts.
		var model = new ReconciledModel(
			"model",
			"model",
			"cloud",
			"model",
			Caps(true, false, false, false),
			4096,
			ReconciledModelState.Available,
			ExplicitContextOverride: true,
			DiscoveredCapabilities: Caps(true, false, false, false),
			DiscoveredContextLength: 8192);

		// Act
		string result = BackendModelsPresenter.DriftSummary(model);

		// Assert
		Assert.Equal(
			"Pinned settings differ from the backend: context is pinned as 4,096, but the backend reports 8,192.",
			result);
	}

	/// <summary>
	/// Verifies that a pin drifted in both facets joins the capability and context clauses with a semicolon.
	/// </summary>
	[Fact]
	public void DriftSummary_WhenBothDrift_JoinsBothClauses()
	{
		// Arrange: an Available pin that differs from the backend in both capabilities (completion-only vs
		// completion + tools) and context (explicit 4,096 vs reported 8,192).
		var model = new ReconciledModel(
			"model",
			"model",
			"cloud",
			"model",
			Caps(true, false, false, false),
			4096,
			ReconciledModelState.Available,
			ExplicitContextOverride: true,
			DiscoveredCapabilities: Caps(true, true, false, false),
			DiscoveredContextLength: 8192);

		// Act
		string result = BackendModelsPresenter.DriftSummary(model);

		// Assert
		Assert.Equal(
			"Pinned settings differ from the backend: capabilities are pinned as completion, but the backend reports completion, tools; context is pinned as 4,096, but the backend reports 8,192.",
			result);
	}

	/// <summary>
	/// Verifies that a model reporting no concrete drift (here a Discovered row, whose state short-circuits both
	/// drift checks) falls back to the generic difference sentence rather than an empty detail list.
	/// </summary>
	[Fact]
	public void DriftSummary_WhenNoConcreteDrift_ReturnsGenericFallback()
	{
		// Arrange: a Discovered row is never Available, so HasCapabilityDrift and HasContextDrift both
		// short-circuit to false, leaving DriftSummary with no clauses to compose.
		var model = new ReconciledModel(
			"model",
			"model",
			"cloud",
			"model",
			Caps(true, false, false, false),
			4096,
			ReconciledModelState.Discovered);

		// Act
		string result = BackendModelsPresenter.DriftSummary(model);

		// Assert
		Assert.Equal("Pinned settings differ from what the backend reports.", result);
	}

	#endregion

	#region PriceSummary

	/// <summary>
	/// Verifies that both reported prices render as a labeled input/output pair joined by the middle-dot
	/// separator.
	/// </summary>
	[Fact]
	public void PriceSummary_WhenBothPricesKnown_RendersLabeledPair()
	{
		// Arrange
		var metadata = new ProviderModelMetadata(
			PromptUsdPerMillionTokens: 3m,
			CompletionUsdPerMillionTokens: 15m);

		// Act
		string result = BackendModelsPresenter.PriceSummary(metadata);

		// Assert
		Assert.Equal("in $3.00 · out $15.00", result);
	}

	/// <summary>
	/// Verifies that a reported input price alone renders as the labeled input figure only.
	/// </summary>
	[Fact]
	public void PriceSummary_WhenOnlyInputKnown_RendersInputOnly()
	{
		// Arrange
		var metadata = new ProviderModelMetadata(PromptUsdPerMillionTokens: 3m);

		// Act
		string result = BackendModelsPresenter.PriceSummary(metadata);

		// Assert
		Assert.Equal("in $3.00", result);
	}

	/// <summary>
	/// Verifies that a reported output price alone renders as the labeled output figure only.
	/// </summary>
	[Fact]
	public void PriceSummary_WhenOnlyOutputKnown_RendersOutputOnly()
	{
		// Arrange
		var metadata = new ProviderModelMetadata(CompletionUsdPerMillionTokens: 15m);

		// Act
		string result = BackendModelsPresenter.PriceSummary(metadata);

		// Assert
		Assert.Equal("out $15.00", result);
	}

	/// <summary>
	/// Verifies that metadata reporting no price at all yields an empty string, so the pricing row is suppressed
	/// entirely.
	/// </summary>
	[Fact]
	public void PriceSummary_WhenNoPriceKnown_ReturnsEmpty()
	{
		// Arrange
		var metadata = new ProviderModelMetadata(DisplayName: "some model");

		// Act
		string result = BackendModelsPresenter.PriceSummary(metadata);

		// Assert
		Assert.Equal(string.Empty, result);
	}

	#endregion

	#region FormatUsd

	/// <summary>
	/// The USD-formatting cases: sub-dollar values keep up to four fractional digits, one-dollar-and-up values
	/// fix two, and an absent amount yields <see langword="null"/>.
	/// </summary>
	public static TheoryData<decimal?, string?> FormatUsdCases => new()
	{
		{ null, null },
		{ 0m, "$0" },
		{ 0.5m, "$0.5" },
		{ 0.25m, "$0.25" },
		{ 0.123456m, "$0.1235" }, // sub-dollar precision caps at four fractional digits (rounds the fifth)
		{ 0.9999m, "$0.9999" },   // just below the $1 boundary still uses the sub-dollar format
		{ 1m, "$1.00" },          // exactly $1 switches to the fixed two-digit format
		{ 2.5m, "$2.50" },
		{ 12m, "$12.00" }
	};

	/// <summary>
	/// Verifies that a USD amount is prefixed with <c>$</c> and formatted with sub-dollar precision below one
	/// dollar and fixed two-digit precision at or above it, returning <see langword="null"/> when absent.
	/// </summary>
	/// <param name="amount">The USD amount, or <see langword="null"/>.</param>
	/// <param name="expected">The expected formatted string, or <see langword="null"/>.</param>
	[Theory]
	[MemberData(nameof(FormatUsdCases))]
	public void FormatUsd_FormatsAmountByMagnitude(decimal? amount, string? expected)
	{
		// Act
		string? result = BackendModelsPresenter.FormatUsd(amount);

		// Assert
		Assert.Equal(expected, result);
	}

	#endregion

	#region ModelDetailPanelId

	/// <summary>
	/// Verifies that the detail-panel id interpolates the component id seed and the row index into the stable
	/// <c>aria-controls</c> target.
	/// </summary>
	/// <param name="componentId">The owning component's id seed.</param>
	/// <param name="rowIndex">The model row index.</param>
	/// <param name="expected">The expected panel id.</param>
	[Theory]
	[InlineData("abc", 0, "backend-model-detail-abc-0")]
	[InlineData("abc", 5, "backend-model-detail-abc-5")]
	[InlineData("xyz", 12, "backend-model-detail-xyz-12")]
	public void ModelDetailPanelId_ComposesComponentIdAndRowIndex(string componentId, int rowIndex, string expected)
	{
		// Act
		string result = BackendModelsPresenter.ModelDetailPanelId(componentId, rowIndex);

		// Assert
		Assert.Equal(expected, result);
	}

	/// <summary>
	/// Verifies that two backend tables with different component id seeds produce different ids for the same row
	/// index, which is the collision-avoidance guarantee the seed exists for.
	/// </summary>
	[Fact]
	public void ModelDetailPanelId_ForDifferentComponents_ProducesDistinctIds()
	{
		// Arrange + Act: same row index, different component seeds.
		string first = BackendModelsPresenter.ModelDetailPanelId("table-one", 3);
		string second = BackendModelsPresenter.ModelDetailPanelId("table-two", 3);

		// Assert
		Assert.NotEqual(first, second);
	}

	#endregion

	#region IsDuplicateName

	/// <summary>
	/// The duplicate-name cases: a blank name is never a duplicate, a non-member is not, and a member matches
	/// after trimming and case-insensitively (mirroring the trimmed, case-insensitive duplicate set).
	/// </summary>
	public static TheoryData<string?, string[], bool> DuplicateNameCases => new()
	{
		{ null, ["gpt-4"], false },       // null name is never a duplicate
		{ "", ["gpt-4"], false },         // empty name is never a duplicate
		{ "   ", ["gpt-4"], false },      // whitespace-only name is never a duplicate
		{ "gpt-4", [], false },           // empty duplicate set matches nothing
		{ "llama3", ["gpt-4"], false },   // name absent from the set is not a duplicate
		{ "gpt-4", ["gpt-4"], true },     // exact member match
		{ "  gpt-4  ", ["gpt-4"], true }, // name is trimmed before the membership test
		{ "GPT-4", ["gpt-4"], true }      // membership is case-insensitive
	};

	/// <summary>
	/// Verifies that a name is flagged as a duplicate only when it is non-blank and its trimmed form is a
	/// case-insensitive member of the backend's duplicate set.
	/// </summary>
	/// <param name="name">The model's current client-facing name.</param>
	/// <param name="duplicateNames">The client-facing names the backend registers more than once.</param>
	/// <param name="expected">Whether the name is expected to be flagged as a duplicate.</param>
	[Theory]
	[MemberData(nameof(DuplicateNameCases))]
	public void IsDuplicateName_FlagsTrimmedCaseInsensitiveMembers(
		string?  name,
		string[] duplicateNames,
		bool     expected)
	{
		// Arrange: the production set is trimmed and case-insensitive, so the fixture matches that comparer.
		IReadOnlySet<string> set = new HashSet<string>(duplicateNames, StringComparer.OrdinalIgnoreCase);

		// Act
		bool result = BackendModelsPresenter.IsDuplicateName(name, set);

		// Assert
		Assert.Equal(expected, result);
	}

	#endregion

	#region Test infrastructure

	/// <summary>
	/// Builds a <see cref="ModelCapabilities"/> from the four functional flags, using an arbitrary provenance
	/// source that none of the presenter mappings read.
	/// </summary>
	/// <param name="completion">Whether completion is supported.</param>
	/// <param name="tools">Whether tool calling is supported.</param>
	/// <param name="vision">Whether vision input is supported.</param>
	/// <param name="embeddings">Whether embeddings are supported.</param>
	/// <returns>The configured capabilities.</returns>
	private static ModelCapabilities Caps(
		bool completion,
		bool tools,
		bool vision,
		bool embeddings) => new(
		completion,
		tools,
		vision,
		embeddings,
		CapabilitySource.ProviderMetadata);

	#endregion
}
