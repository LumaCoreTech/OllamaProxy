// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using AngleSharp.Dom;

using Bunit;

using OllamaProxy.Admin.Catalog;
// The page type OllamaProxy.Admin.Ui.Pages.Models is aliased for symmetry with the other page-test harnesses and
// to keep the SUT reference unambiguous against the OllamaProxy.Core model types imported by the harness.
using ModelsPage = OllamaProxy.Admin.Ui.Pages.Models;

namespace OllamaProxy.Tests.Admin.Ui.Pages;

// Detail rows: how a model's per-row detail panel expands, collapses, and stays independent of its neighbours.
//
// Each summary row carries an expand button whose aria-expanded reflects the row's state. Clicking it opens the
// detail panel (a second row) and flips the button; clicking again collapses it. Opening another row keeps the
// first row open too, since several rows may be open at once and each tracks its own state.
//
// For the load lifecycle and catalog states see the anchor file; the shared harness lives in Helpers.
public sealed partial class ModelsPageTests
{
	// --- 2. Detail rows: expand, collapse, ARIA state, row independence ---

	/// <summary>
	/// Verifies a freshly rendered row is collapsed: no detail panel is present and the expand button reports
	/// <c>aria-expanded="false"</c>, so the table opens compact.
	/// </summary>
	[Fact]
	public void Render_WhenRowNotExpanded_ShowsNoDetailPanel()
	{
		// Arrange
		FakeAdminCatalogService service = new()
		{
			Catalog = LiveCatalog.Ready([CreateModel("alpha")])
		};

		// Act
		(IRenderedComponent<ModelsPage> component, FakeAdminCatalogService _) = RenderModels(service);

		// Assert
		Assert.Single(component.FindAll("tr.models-row"));
		Assert.Empty(component.FindAll(".models-detail-panel"));
		Assert.Equal("false", component.Find(".models-expand").GetAttribute("aria-expanded"));
		Assert.False(string.IsNullOrWhiteSpace(component.Find(".models-expand").GetAttribute("aria-controls")));
	}

	/// <summary>
	/// Verifies clicking a row's expand button opens its detail panel and flips the button to
	/// <c>aria-expanded="true"</c>, so the operator sees the full <c>/api/show</c> picture for that model.
	/// </summary>
	[Fact]
	public void ToggleDetails_WhenClicked_ShowsDetailPanel()
	{
		// Arrange
		FakeAdminCatalogService service = new()
		{
			Catalog = LiveCatalog.Ready([CreateModel("alpha")])
		};
		(IRenderedComponent<ModelsPage> component, FakeAdminCatalogService _) = RenderModels(service);

		// Act
		component.Find(".models-expand").Click();

		// Assert
		IElement button = component.Find(".models-expand");
		IElement panel = component.Find(".models-detail-panel");
		Assert.Equal("true", button.GetAttribute("aria-expanded"));
		Assert.Equal(button.GetAttribute("aria-controls"), panel.Id);
		Assert.Equal("Model details", component.Find(".models-detail-heading").TextContent);

		IReadOnlyList<(string Term, string Value)> fields = DetailFields(component);
		Assert.Contains(("Context length", "4,096 tokens"), fields);
		Assert.Contains(("Capability source", "Default"), fields);
		Assert.Contains(("Backend", "primary"), fields);
		Assert.Contains(("Upstream model", "alpha"), fields);
		Assert.Contains(("Reasoning effort", "—"), fields);
		Assert.Contains(("Quantization level", "n/a"), fields);
		Assert.Contains(("Architecture", "openai (synthesized)"), fields);
		Assert.Contains(("Format", "gguf (synthesized)"), fields);
	}

	/// <summary>
	/// Verifies clicking an expanded row's button again collapses the detail panel and returns the button to
	/// <c>aria-expanded="false"</c>, so the toggle is symmetric.
	/// </summary>
	[Fact]
	public void ToggleDetails_WhenClickedTwice_CollapsesDetailPanel()
	{
		// Arrange
		FakeAdminCatalogService service = new()
		{
			Catalog = LiveCatalog.Ready([CreateModel("alpha")])
		};
		(IRenderedComponent<ModelsPage> component, FakeAdminCatalogService _) = RenderModels(service);
		component.Find(".models-expand").Click();

		// Act
		component.Find(".models-expand").Click();

		// Assert
		Assert.Empty(component.FindAll(".models-detail-panel"));
		Assert.Equal("false", component.Find(".models-expand").GetAttribute("aria-expanded"));
	}

	/// <summary>
	/// Verifies expanding a second row leaves the first one open, since each row tracks its own expansion state and
	/// several may be open at once.
	/// </summary>
	[Fact]
	public void ToggleDetails_WhenTwoRowsExpanded_KeepsBothRowsOpen()
	{
		// Arrange
		FakeAdminCatalogService service = new()
		{
			Catalog = LiveCatalog.Ready([CreateModel("alpha"), CreateModel("beta")])
		};
		(IRenderedComponent<ModelsPage> component, FakeAdminCatalogService _) = RenderModels(service);

		// Act
		component.FindAll(".models-expand")[0].Click();
		component.FindAll(".models-expand")[1].Click();

		// Assert
		Assert.Equal(2, component.FindAll(".models-detail-panel").Count);
		IReadOnlyList<IElement> buttons = component.FindAll(".models-expand");
		Assert.Equal("true", buttons[0].GetAttribute("aria-expanded"));
		Assert.Equal("true", buttons[1].GetAttribute("aria-expanded"));
	}
}
