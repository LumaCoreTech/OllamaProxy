// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using AngleSharp.Dom;

using Bunit;

using OllamaProxy.Admin.Ui.Components.Backends;

namespace OllamaProxy.Tests.Admin.Ui.Components.Backends;

// Layout: how ModeIgnoresPins reshapes the table.
//
// A pin-aware backend (Explicit/Hybrid) and a pin-ignoring one (plug-and-play) render the same reconciliation
// result differently — the pin column, the mode note, and the available/unavailable counts all hinge on that one
// flag. These tests check the reshape from both sides, plus the drift count that surfaces only when a pin drifts:
//
//   1. Mode note: shown only when pins are ignored (WhenModeIgnoresPins / WhenPinAware).
//   2. Pin column: header + checkbox cell present only when pin-aware (WhenPinAware / WhenModeIgnoresPins).
//   3. Counts: available/unavailable shown only when pin-aware; discovered always; drift only when non-zero.
public sealed partial class BackendModelsTests
{
	// --- 1. Mode note ---

	/// <summary>
	/// Verifies that a pin-ignoring (plug-and-play) backend renders the mode note explaining that pins are ignored.
	/// </summary>
	[Fact]
	public void Render_ModeNote_WhenModeIgnoresPins_IsShown()
	{
		// Act
		IRenderedComponent<BackendModels> cut = RenderModels(
			modeIgnoresPins: true,
			reconciliation: Reconciliation(DiscoveredModel()));

		// Assert
		Assert.Single(cut.FindAll("p.backend-mode-note"));
	}

	/// <summary>
	/// Verifies that a pin-aware backend (the default Explicit fixture) renders no mode note.
	/// </summary>
	[Fact]
	public void Render_ModeNote_WhenPinAware_IsAbsent()
	{
		// Act
		IRenderedComponent<BackendModels> cut = RenderModels(reconciliation: Reconciliation(DiscoveredModel()));

		// Assert
		Assert.Empty(cut.FindAll("p.backend-mode-note"));
	}

	// --- 2. Pin column ---

	/// <summary>
	/// Verifies that a pin-aware backend renders the "Pinned" column header, giving the checkbox column its place in
	/// the table.
	/// </summary>
	[Fact]
	public void Render_PinColumn_WhenPinAware_HasPinnedHeader()
	{
		// Act
		IRenderedComponent<BackendModels> cut = RenderModels(reconciliation: Reconciliation(DiscoveredModel()));

		// Assert: the pin column header is present exactly once.
		IReadOnlyList<IElement> headers = cut.FindAll("thead th");
		Assert.Single(headers, header => header.TextContent.Trim() == "Pinned");
	}

	/// <summary>
	/// Verifies that a pin-ignoring backend drops the "Pinned" column header entirely, since pins have no effect in
	/// plug-and-play mode.
	/// </summary>
	[Fact]
	public void Render_PinColumn_WhenModeIgnoresPins_HasNoPinnedHeader()
	{
		// Act
		IRenderedComponent<BackendModels> cut = RenderModels(
			modeIgnoresPins: true,
			reconciliation: Reconciliation(DiscoveredModel()));

		// Assert
		IReadOnlyList<IElement> headers = cut.FindAll("thead th");
		Assert.DoesNotContain(headers, header => header.TextContent.Trim() == "Pinned");
	}

	/// <summary>
	/// Verifies that a pin-aware discovered row renders a pin checkbox labelled to pin the upstream model, so the
	/// operator can promote it into the registry.
	/// </summary>
	[Fact]
	public void Render_PinCheckbox_WhenPinAware_IsRenderedForDiscoveredRow()
	{
		// Act
		IRenderedComponent<BackendModels> cut = RenderModels(
			reconciliation: Reconciliation(DiscoveredModel(name: "llama3")));

		// Assert: the discovered row exposes a "Pin <upstream>" checkbox.
		IElement checkbox = cut.Find("tbody input[type=checkbox]");
		Assert.Equal("Pin llama3", checkbox.GetAttribute("aria-label"));
	}

	/// <summary>
	/// Verifies that a pin-ignoring backend renders no checkboxes in the table body at all, since there is no pin
	/// column to host them.
	/// </summary>
	[Fact]
	public void Render_PinCheckbox_WhenModeIgnoresPins_IsAbsent()
	{
		// Act
		IRenderedComponent<BackendModels> cut = RenderModels(
			modeIgnoresPins: true,
			reconciliation: Reconciliation(DiscoveredModel()));

		// Assert
		Assert.Empty(cut.FindAll("tbody input[type=checkbox]"));
	}

	// --- 3. Counts ---

	/// <summary>
	/// Verifies that a pin-aware backend renders the available, unavailable, and discovered counts computed from the
	/// reconciliation result.
	/// </summary>
	[Fact]
	public void Render_Counts_WhenPinAware_ShowsAvailableUnavailableAndDiscovered()
	{
		// Arrange: one of each state so all three headline counts are exercised together.
		IRenderedComponent<BackendModels> cut = RenderModels(
			reconciliation: Reconciliation(AvailablePin(), UnavailablePin(), DiscoveredModel()));

		// Act
		IReadOnlyList<string> counts = CountTexts(cut);

		// Assert: exactly three spans, one per count — a regression that adds a spurious span is caught.
		Assert.Equal(3, counts.Count);
		Assert.Contains("1 available", counts);
		Assert.Contains("1 unavailable", counts);
		Assert.Contains("1 discovered", counts);
	}

	/// <summary>
	/// Verifies that a pin-ignoring backend suppresses the available and unavailable counts (pins have no effect) but
	/// still shows the discovered count.
	/// </summary>
	[Fact]
	public void Render_Counts_WhenModeIgnoresPins_ShowsDiscoveredOnly()
	{
		// Arrange: same three-state result, but plug-and-play collapses the pin-derived counts.
		IRenderedComponent<BackendModels> cut = RenderModels(
			modeIgnoresPins: true,
			reconciliation: Reconciliation(AvailablePin(), UnavailablePin(), DiscoveredModel()));

		// Act
		IReadOnlyList<string> counts = CountTexts(cut);

		// Assert: exactly one span (discovered) — the pin-derived counts are fully suppressed.
		Assert.Single(counts);
		Assert.Contains("1 discovered", counts);
		Assert.DoesNotContain("1 available", counts);
		Assert.DoesNotContain("1 unavailable", counts);
	}

	/// <summary>
	/// Verifies that the drift count renders as a dedicated highlighted span when at least one pin has drifted.
	/// </summary>
	[Fact]
	public void Render_DriftCount_WhenPinsDrift_IsShown()
	{
		// Act: a drifted pin makes ReconciliationResult.DriftCount non-zero.
		IRenderedComponent<BackendModels> cut = RenderModels(reconciliation: Reconciliation(DriftedPin()));

		// Assert
		Assert.Equal("1 drifted", cut.Find("span.backend-count-drift").TextContent.Trim());
	}

	/// <summary>
	/// Verifies that the drift count span is omitted entirely when no pin has drifted, so a clean table carries no
	/// drift affordance.
	/// </summary>
	[Fact]
	public void Render_DriftCount_WhenNoDrift_IsAbsent()
	{
		// Act: a plain available pin never drifts (no discovered facets to compare against).
		IRenderedComponent<BackendModels> cut = RenderModels(reconciliation: Reconciliation(AvailablePin()));

		// Assert
		Assert.Empty(cut.FindAll("span.backend-count-drift"));
	}

	/// <summary>
	/// Returns the trimmed text of every headline count span in the counts row.
	/// </summary>
	/// <param name="cut">The rendered component.</param>
	/// <returns>The count labels in DOM order.</returns>
	private static IReadOnlyList<string> CountTexts(IRenderedComponent<BackendModels> cut)
	{
		return
		[
			..cut
				.FindAll(".backend-counts span")
				.Select(span => span.TextContent.Trim())
		];
	}
}
