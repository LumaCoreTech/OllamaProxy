// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using AngleSharp.Dom;

using Bunit;

using OllamaProxy.Admin.Catalog;
using OllamaProxy.Configuration;
// The page type OllamaProxy.Admin.Ui.Pages.Models is aliased for symmetry with the other page-test harnesses and
// to keep the SUT reference unambiguous against the OllamaProxy.Core model types imported by the harness.
using ModelsPage = OllamaProxy.Admin.Ui.Pages.Models;

namespace OllamaProxy.Tests.Admin.Ui.Pages;

/// <summary>
/// Page-level bUnit tests for <see cref="ModelsPage"/>, the read-only viewer of the live model catalog the proxy is
/// serving right now.
/// </summary>
/// <remarks>
/// The page reads a <see cref="LiveCatalog"/> from <see cref="IAdminCatalogService"/> during its synchronous
/// initialization and renders one of three reachable states — the proxy is not serving, it serves an empty catalog,
/// or it serves models — followed by per-row detail expansion. These tests assert which state renders and how the
/// detail rows toggle, rather than re-testing the shared <c>CapabilityChips</c> child component.
/// <para>Reading order:</para>
/// <list type="number">
///     <item>
///         <description>
///         This anchor: the load and the three catalog states (not-ready notice, empty notice, populated table).
///         </description>
///     </item>
///     <item>
///         <description>
///         <c>Details</c>: the per-row detail panel — expanding, collapsing, ARIA state, and row independence.
///         </description>
///     </item>
/// </list>
/// The shared harness, fake catalog service, and model builder live in <c>Helpers</c>.
/// <para>
/// The <c>Loading…</c> branch (<c>mCatalog is null</c>) is intentionally not covered: the fake catalog service
/// resolves synchronously inside <c>OnInitialized()</c>, so the null state is never observable through a rendered
/// component. Covering it would require an artificial async-suspending service that does not reflect the real
/// synchronous <see cref="IAdminCatalogService.GetLiveCatalog"/> contract.
/// </para>
/// </remarks>
[Trait("Category", "Unit")]
public sealed partial class ModelsPageTests
{
	// --- 1. Load and catalog states: not-ready, empty, populated ---

	/// <summary>
	/// Verifies the page reads the live catalog from the service exactly once during its initial render, so the
	/// operator sees the current catalog without an explicit refresh.
	/// </summary>
	[Fact]
	public void OnInitialized_WhenPageRenders_ReadsCatalogOnce()
	{
		// Arrange
		FakeAdminCatalogService service = new() { Catalog = LiveCatalog.NotReady };

		// Act
		(IRenderedComponent<ModelsPage> _, FakeAdminCatalogService catalogService) = RenderModels(service);

		// Assert
		Assert.Equal(1, catalogService.GetLiveCatalogCallCount);
	}

	/// <summary>
	/// Verifies that when the proxy is not serving, the page shows the not-ready notice (and no model table), so the
	/// operator can tell a transient startup/recycle window apart from an empty catalog.
	/// </summary>
	[Fact]
	public void Render_WhenProxyNotReady_ShowsNotReadyNoticeWithoutTable()
	{
		// Arrange
		FakeAdminCatalogService service = new() { Catalog = LiveCatalog.NotReady };

		// Act
		(IRenderedComponent<ModelsPage> component, FakeAdminCatalogService _) = RenderModels(service);

		// Assert
		Assert.Single(component.FindAll(".models-status-notready"));
		Assert.Equal(
			"The proxy is not serving right now. This is normal during startup or the brief moment of a configuration " +
			"apply; refresh in a moment. If it persists, check the proxy host on the Backends page.",
			ModelsStatusText(component));
		Assert.Empty(component.FindAll(".models-table"));
		Assert.Empty(component.FindAll(".models-count"));
	}

	/// <summary>
	/// Verifies that a ready-but-empty catalog shows the "serving no models" notice and no table, so the operator
	/// sees a genuine configuration outcome rather than a not-serving condition.
	/// </summary>
	[Fact]
	public void Render_WhenCatalogEmpty_ShowsEmptyNoticeWithoutTable()
	{
		// Arrange
		FakeAdminCatalogService service = new() { Catalog = LiveCatalog.Ready([]) };

		// Act
		(IRenderedComponent<ModelsPage> component, FakeAdminCatalogService _) = RenderModels(service);

		// Assert
		Assert.Single(component.FindAll(".models-status"));
		Assert.Empty(component.FindAll(".models-status-notready"));
		Assert.Equal(
			"The proxy is serving no models. Pin or discover some on the Backends page.",
			ModelsStatusText(component));
		Assert.Empty(component.FindAll(".models-table"));
		Assert.Empty(component.FindAll(".models-count"));
	}

	/// <summary>
	/// Verifies that a populated catalog renders the model table with one summary row per model and the matching
	/// served-count line, so the operator sees exactly the models the proxy is serving.
	/// </summary>
	[Fact]
	public void Render_WhenCatalogHasModels_ShowsTableWithRowPerModel()
	{
		// Arrange
		FakeAdminCatalogService service = new()
		{
			Catalog = LiveCatalog.Ready(
			[
				CreateModel("alpha", backendName: "primary", upstreamModel: "upstream-alpha"),
				CreateModel(
					"beta",
					backendName: "secondary",
					upstreamModel: "upstream-beta",
					contextLength: 8192,
					reasoningEffort: ReasoningEffort.High)
			])
		};

		// Act
		(IRenderedComponent<ModelsPage> component, FakeAdminCatalogService _) = RenderModels(service);

		// Assert
		Assert.Empty(component.FindAll(".models-status"));
		Assert.Equal("2 model(s) served.", component.Find(".models-count").TextContent);
		Assert.Single(component.FindAll(".models-table"));
		IReadOnlyList<IElement> rows = ModelRows(component);
		Assert.Equal(2, rows.Count);
		AssertModelRow(rows[0], "alpha", "primary", "upstream-alpha", "4,096", "—");
		AssertModelRow(rows[1], "beta", "secondary", "upstream-beta", "8,192", "High");
	}
}
