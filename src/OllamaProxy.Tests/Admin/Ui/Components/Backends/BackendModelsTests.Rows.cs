// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using AngleSharp.Dom;

using Bunit;

using OllamaProxy.Admin.Ui.Components.Backends;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Tests.Admin.Ui.Components.Backends;

// Rows: the per-model shapes and the inline states layered on top of them.
//
// One reconciliation result renders three fundamentally different row shapes, and several inline states decorate
// them. These tests walk the shapes and then the decorations:
//
//   1. Discovered row: a single read-only row, exposed-as cell vs. the not-exposed hint (WhenExposed / WhenNotExposed).
//   2. Pinned editor pair: the head + overrides rows, the name input, the "+ variant" button, the reasoning
//      selector, and the read-only prefix affix (WhenPrefixed).
//   3. Inline states: the duplicate-name flag (WhenDuplicate / WhenUnique), the unavailable note, and the drift badge.
//   4. Capabilities cell: resolved chips vs. the undetermined placeholder (WhenResolved / WhenUndetermined).
//   5. Detail panel: the toggle's presence (WhenMetadataPresent / WhenNoMetadata) and the panel's open/closed state.
public sealed partial class BackendModelsTests
{
	// --- 1. Discovered (unpinned) row shape ---

	/// <summary>
	/// Verifies that a discovered model renders as a single row exposing its upstream id, so the operator can read
	/// which backend model the row represents.
	/// </summary>
	[Fact]
	public void Render_DiscoveredRow_ShowsUpstreamModel()
	{
		// Act
		IRenderedComponent<BackendModels> cut = RenderModels(
			reconciliation: Reconciliation(DiscoveredModel(name: "llama3")));

		// Assert
		Assert.Equal("llama3", cut.Find("span.model-upstream").TextContent.Trim());
	}

	/// <summary>
	/// Verifies that an auto-exposed discovered model shows its client-facing exposed name in the "Exposed as" cell.
	/// </summary>
	[Fact]
	public void Render_DiscoveredRow_WhenExposed_ShowsExposedName()
	{
		// Arrange: a discovered model that the runtime catalog exposes (Hybrid/PlugAndPlay semantics).
		IRenderedComponent<BackendModels> cut = RenderModels(
			reconciliation: Reconciliation(
				DiscoveredModel(name: "llama3", exposedName: "acme/llama3", isExposed: true)));

		// Assert
		IElement exposed = cut.Find("td.model-exposed-cell");
		Assert.Equal("acme/llama3", exposed.TextContent.Trim());
		Assert.False(exposed.ClassList.Contains("model-not-exposed"));
	}

	/// <summary>
	/// Verifies that a discovered model the catalog does not auto-expose (Explicit mode) renders the muted
	/// "(pin to expose)" hint instead of advertising a name the proxy never serves.
	/// </summary>
	[Fact]
	public void Render_DiscoveredRow_WhenNotExposed_ShowsPinToExposeHint()
	{
		// Arrange: Explicit mode leaves an unpinned discovered model not-exposed.
		IRenderedComponent<BackendModels> cut = RenderModels(
			reconciliation: Reconciliation(DiscoveredModel(name: "llama3", isExposed: false)));

		// Assert
		IElement exposed = cut.Find("td.model-exposed-cell");
		Assert.True(exposed.ClassList.Contains("model-not-exposed"));
		Assert.Equal("(pin to expose)", cut.Find("span.model-not-exposed-hint").TextContent.Trim());
	}

	// --- 2. Pinned (available) editor pair ---

	/// <summary>
	/// Verifies that a pinned model renders as the joined head + overrides row pair that carries its editable fields.
	/// </summary>
	[Fact]
	public void Render_PinnedRow_RendersHeadAndOverridesRowPair()
	{
		// Act
		IRenderedComponent<BackendModels> cut = RenderModels(reconciliation: Reconciliation(AvailablePin()));

		// Assert: exactly one head row and one overrides row.
		Assert.Single(cut.FindAll("tr.model-pin-head"));
		Assert.Single(cut.FindAll("tr.model-pin-overrides"));
	}

	/// <summary>
	/// Verifies that a pinned row exposes the editable client-facing name input, the "+ variant" action, and the
	/// reasoning-effort selector — the pin-only editing affordances a discovered row lacks.
	/// </summary>
	[Fact]
	public void Render_PinnedRow_ExposesNameInputVariantAndReasoningSelector()
	{
		// Act
		IRenderedComponent<BackendModels> cut = RenderModels(reconciliation: Reconciliation(AvailablePin()));

		// Assert
		Assert.Single(cut.FindAll("input.model-name-input"));
		Assert.Equal("+ variant", cut.Find("button.model-add-variant").TextContent.Trim());
		Assert.Single(cut.FindAll("select.model-reasoning-select"));
	}

	/// <summary>
	/// Verifies that a prefixed backend renders the fixed prefix as a read-only affix beside the editable name, so
	/// the operator edits only the bare name after the slash.
	/// </summary>
	[Fact]
	public void Render_PinnedRow_WhenPrefixed_ShowsReadOnlyPrefixAffix()
	{
		// Arrange: a backend whose ModelPrefix is applied at exposure.
		IRenderedComponent<BackendModels> cut = RenderModels(
			backend: CreateBackend(modelPrefix: "acme"),
			reconciliation: Reconciliation(AvailablePin()));

		// Assert
		Assert.Equal("acme/", cut.Find("span.model-name-prefix").TextContent.Trim());
	}

	// --- 3. Inline states ---

	/// <summary>
	/// Verifies that a pinned row whose name collides with another entry is flagged inline: the name input carries
	/// the invalid marker class and the duplicate-name alert renders beneath it.
	/// </summary>
	[Fact]
	public void Render_PinnedRow_WhenDuplicateName_FlagsInputAndShowsAlert()
	{
		// Arrange: the backend's duplicate set contains this pin's name, so the inline check trips.
		IReadOnlySet<string> duplicates = new HashSet<string>(["gpt-4"], StringComparer.OrdinalIgnoreCase);

		IRenderedComponent<BackendModels> cut = RenderModels(
			reconciliation: Reconciliation(AvailablePin(name: "gpt-4")),
			duplicateNames: duplicates);

		// Assert
		Assert.True(cut.Find("input.model-name-input").ClassList.Contains("model-name-input-invalid"));
		Assert.Equal(
			"Duplicate name — each model on this backend must be exposed under a distinct name.",
			cut.Find("p.model-name-duplicate").TextContent.Trim());
	}

	/// <summary>
	/// Verifies that a pinned row whose name is unique carries no invalid marker and no duplicate alert, proving the
	/// flag is genuinely gated on the duplicate set rather than always rendered.
	/// </summary>
	[Fact]
	public void Render_PinnedRow_WhenNameUnique_HasNoDuplicateFlag()
	{
		// Act: no duplicate set supplied, so the default empty set marks nothing.
		IRenderedComponent<BackendModels> cut = RenderModels(
			reconciliation: Reconciliation(AvailablePin(name: "gpt-4")));

		// Assert
		Assert.False(cut.Find("input.model-name-input").ClassList.Contains("model-name-input-invalid"));
		Assert.Empty(cut.FindAll("p.model-name-duplicate"));
	}

	/// <summary>
	/// Verifies that an unavailable pin collapses its two reported facets into a single note, rather than rendering
	/// empty disabled controls that would read as "supports nothing, context zero".
	/// </summary>
	[Fact]
	public void Render_UnavailablePin_ShowsBackendNoLongerReportsNote()
	{
		// Act
		IRenderedComponent<BackendModels> cut = RenderModels(reconciliation: Reconciliation(UnavailablePin()));

		// Assert
		Assert.Equal(
			"Backend no longer reports this model.",
			cut.Find("td.model-unavailable-note").TextContent.Trim());
	}

	/// <summary>
	/// Verifies that a drifted pin renders the amber warning badge with the drift tooltip wired from the presenter,
	/// so the operator can hover to see exactly what diverged.
	/// </summary>
	[Fact]
	public void Render_DriftedPin_ShowsWarningBadgeWithDriftTooltip()
	{
		// Act: a pin whose explicit 4,096 override drifts from the backend's reported 8,192.
		IRenderedComponent<BackendModels> cut = RenderModels(reconciliation: Reconciliation(DriftedPin()));

		// Assert
		IElement badge = cut.Find("span.badge.badge-warning");
		Assert.Equal("Drifted", badge.TextContent.Trim());
		Assert.Equal(
			"Pinned settings differ from the backend: context is pinned as 4,096, but the backend reports 8,192.",
			badge.GetAttribute("title"));
	}

	// --- 4. Capabilities cell ---

	/// <summary>
	/// Verifies that a discovered row with resolved capabilities renders them as shared capability chips, one solid
	/// chip per supported flag in canonical order.
	/// </summary>
	[Fact]
	public void Render_CapabilitiesCell_WhenResolved_RendersChips()
	{
		// Arrange: completion + tools supported (both conclusive), so two solid chips are expected.
		ModelCapabilities capabilities = Caps(completion: true, tools: true, vision: false, embeddings: false);

		IRenderedComponent<BackendModels> cut = RenderModels(
			reconciliation: Reconciliation(DiscoveredModel(name: "llama3", capabilities: capabilities)));

		// Assert
		Assert.Single(cut.FindAll("div.caps-chips"));
		IReadOnlyList<string> chips = cut.FindAll("span.cap-chip").Select(chip => chip.TextContent.Trim()).ToList();
		Assert.Equal(["completion", "tools"], chips);
	}

	/// <summary>
	/// Verifies that a discovered row whose probing was skipped (null capabilities) renders the em-dash placeholder,
	/// distinct from the "none" a known-but-empty set would produce.
	/// </summary>
	[Fact]
	public void Render_CapabilitiesCell_WhenUndetermined_RendersPlaceholder()
	{
		// Act: null capabilities model a probe-skipped discovery.
		IRenderedComponent<BackendModels> cut = RenderModels(
			reconciliation: Reconciliation(DiscoveredModel(name: "llama3", capabilities: null)));

		// Assert
		Assert.Empty(cut.FindAll("div.caps-chips"));
		Assert.Equal("—", cut.Find("span.model-reported-none").TextContent.Trim());
	}

	// --- 5. Metadata detail panel ---

	/// <summary>
	/// Verifies that a model the backend reported metadata for renders the collapsed detail toggle (no panel yet),
	/// advertising that details are available to expand.
	/// </summary>
	[Fact]
	public void Render_DetailToggle_WhenMetadataPresentAndCollapsed_ShowsClosedToggleWithoutPanel()
	{
		// Arrange: metadata makes the toggle appear; the default predicate leaves it collapsed.
		var metadata = new ProviderModelMetadata(DisplayName: "Llama 3 Instruct");

		IRenderedComponent<BackendModels> cut = RenderModels(
			reconciliation: Reconciliation(DiscoveredModel(name: "llama3", metadata: metadata)));

		// Assert
		IElement toggle = cut.Find("button.model-details-toggle");
		Assert.Equal("false", toggle.GetAttribute("aria-expanded"));
		Assert.Equal("Show model details", toggle.GetAttribute("aria-label"));
		Assert.Empty(cut.FindAll("tr.model-detail-row"));
	}

	/// <summary>
	/// Verifies that expanding a model's detail panel renders the panel row, flips the toggle to expanded, wires the
	/// toggle's <c>aria-controls</c> to the panel's id, and shows the reported metadata field.
	/// </summary>
	[Fact]
	public void Render_DetailPanel_WhenExpanded_RendersPanelAndWiresAria()
	{
		// Arrange: metadata present and the predicate reports this model as expanded.
		var metadata = new ProviderModelMetadata(DisplayName: "Llama 3 Instruct");

		IRenderedComponent<BackendModels> cut = RenderModels(
			reconciliation: Reconciliation(DiscoveredModel(name: "llama3", metadata: metadata)),
			isDetailsExpanded: _ => true);

		// Assert
		IElement toggle = cut.Find("button.model-details-toggle");
		IElement panelRow = cut.Find("tr.model-detail-row");
		Assert.Equal("true", toggle.GetAttribute("aria-expanded"));
		Assert.Equal("Hide model details", toggle.GetAttribute("aria-label"));
		Assert.Equal(panelRow.Id, toggle.GetAttribute("aria-controls"));
		Assert.Equal("Llama 3 Instruct", cut.Find("tr.model-detail-row dd").TextContent.Trim());
	}

	/// <summary>
	/// Verifies that a model the backend reported no metadata for renders neither the detail toggle nor a panel, even
	/// when the expand predicate would report it expanded — there is nothing to show.
	/// </summary>
	[Fact]
	public void Render_DetailToggle_WhenNoMetadata_IsAbsent()
	{
		// Act: no metadata, and an always-true predicate to prove the gate is the metadata, not the predicate.
		IRenderedComponent<BackendModels> cut = RenderModels(
			reconciliation: Reconciliation(DiscoveredModel(name: "llama3", metadata: null)),
			isDetailsExpanded: _ => true);

		// Assert
		Assert.Empty(cut.FindAll("button.model-details-toggle"));
		Assert.Empty(cut.FindAll("tr.model-detail-row"));
	}
}
