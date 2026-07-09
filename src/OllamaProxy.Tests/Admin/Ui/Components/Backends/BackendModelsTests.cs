// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using AngleSharp.Dom;

using Bunit;

using OllamaProxy.Admin.Reconciliation;
using OllamaProxy.Admin.Ui.Components.Backends;

namespace OllamaProxy.Tests.Admin.Ui.Components.Backends;

/// <summary>
/// Render tests for <see cref="BackendModels"/>, the combined model list for one backend. The pure display
/// mappings it delegates to are covered by <see cref="BackendModelsPresenterTests"/>; these tests instead assert
/// <em>which</em> DOM branch renders for a given input, so the component's state machine, layout gates, row shapes,
/// and callback wiring are pinned through the actually rendered markup rather than by inspecting the source.
/// </summary>
/// <remarks>
/// The suite is split across partial files, each a chapter in the same story. Reading order:
/// <list type="number">
///     <item>
///         <description>
///         This anchor: the render state machine (error, empty, fetching, streaming, loaded) and the header's
///         Refresh/Probe buttons — their labels and enabled state as the load lifecycle advances.
///         </description>
///     </item>
///     <item>
///         <description>
///         <c>Layout</c>: how <see cref="BackendModels.ModeIgnoresPins"/> reshapes the table — the plug-and-play
///         mode note, the pin column's presence, the headline counts, and the drift count.
///         </description>
///     </item>
///     <item>
///         <description>
///         <c>Rows</c>: the per-row shapes — the pinned editor pair, the discovered read-only row, the inline
///         duplicate-name flag, the not-exposed hint, the unavailable note, the drift badge, the capability chips
///         versus the undetermined placeholder, and the metadata detail panel.
///         </description>
///     </item>
///     <item>
///         <description>
///         <c>Callbacks</c>: the event wiring — Refresh, Probe, Cancel, toggle-pin, add-variant, and toggle-details
///         each raise the callback the page supplies.
///         </description>
///     </item>
/// </list>
/// The shared render harness and fixture builders live in the <c>Helpers</c> partial.
/// </remarks>
[Trait("Category", "Unit")]
public sealed partial class BackendModelsTests : BunitContext
{
	// --- 1. Render state machine: from first load through streaming, error, and a populated table ---

	/// <summary>
	/// Verifies that before any fetch has run (no snapshot, idle) the component shows the "not loaded" placeholder
	/// and neither an error nor a table.
	/// </summary>
	[Fact]
	public void Render_WhenNoSnapshotAndIdle_ShowsNoModelsLoadedPlaceholder()
	{
		// Act: ModelListState.Empty is the pre-expansion baseline — no snapshot, not fetching, no error.
		IRenderedComponent<BackendModels> cut = RenderModels(state: ModelListState.Empty);

		// Assert
		Assert.Equal("No models loaded yet.", cut.Find("p.backend-empty").TextContent.Trim());
		Assert.Empty(cut.FindAll("p.backend-error"));
		Assert.Empty(cut.FindAll("table.backend-table"));
	}

	/// <summary>
	/// Verifies that while the first fetch is in flight (no snapshot yet, fetching) the placeholder switches to the
	/// "fetching" wording and no streaming progress banner is shown.
	/// </summary>
	[Fact]
	public void Render_WhenFetchingWithoutSnapshot_ShowsFetchingPlaceholder()
	{
		// Act
		IRenderedComponent<BackendModels> cut = RenderModels(state: FetchingState());

		// Assert
		Assert.Equal("Fetching models…", cut.Find("p.backend-empty").TextContent.Trim());
		Assert.Empty(cut.FindAll(".backend-probe-progress"));
	}

	/// <summary>
	/// Verifies that a streaming probe with no rows yet suppresses the empty placeholder (the banner already
	/// explains what is happening) and renders the progress banner with the running resolved count.
	/// </summary>
	[Fact]
	public void Render_WhenStreaming_SuppressesPlaceholderAndShowsProgressBanner()
	{
		// Act: StreamingState reports a running probe with ProbedCount = 2 and no snapshot rows yet.
		IRenderedComponent<BackendModels> cut = RenderModels(state: StreamingState());

		// Assert: the placeholder is gone and the banner reports the count.
		Assert.Empty(cut.FindAll("p.backend-empty"));
		Assert.Equal(
			"Probing capabilities… 2 resolved",
			cut.Find("span.backend-probe-progress-text").TextContent.Trim());
	}

	/// <summary>
	/// Verifies that a fetch error takes precedence over every other branch: the error message renders and neither
	/// the empty placeholder nor the table appears, even though the snapshot is populated.
	/// </summary>
	[Fact]
	public void Render_WhenErrorPresent_ShowsErrorAndSuppressesTable()
	{
		// Arrange: a populated snapshot alongside an error proves the error branch is checked first.
		var state = new ModelListState(Snapshot: [SampleCandidate], IsFetching: false, Error: "Upstream returned 500.");

		// Act
		IRenderedComponent<BackendModels> cut = RenderModels(
			state: state,
			reconciliation: Reconciliation(DiscoveredModel()));

		// Assert
		Assert.Equal("Upstream returned 500.", cut.Find("p.backend-error").TextContent.Trim());
		Assert.Empty(cut.FindAll("p.backend-empty"));
		Assert.Empty(cut.FindAll("table.backend-table"));
	}

	/// <summary>
	/// The loaded-but-empty reconciliation inputs: a <see langword="null"/> result and an empty-model result both
	/// reach the "reports no models" branch.
	/// </summary>
	public static TheoryData<ReconciliationResult?> EmptyReconciliations => new()
	{
		null,
		new ReconciliationResult([])
	};

	/// <summary>
	/// Verifies that once a snapshot has loaded but reconciliation yields no models, the component shows the
	/// "reports no models" placeholder rather than an empty table.
	/// </summary>
	/// <param name="reconciliation">The empty reconciliation input under test (null or zero-model result).</param>
	[Theory]
	[MemberData(nameof(EmptyReconciliations))]
	public void Render_WhenSnapshotLoadedButReconciliationEmpty_ShowsNoModelsReported(
		ReconciliationResult? reconciliation)
	{
		// Act: the snapshot is loaded (default LoadedState), so the empty branch is reconciliation-driven.
		IRenderedComponent<BackendModels> cut = RenderModels(reconciliation: reconciliation);

		// Assert
		Assert.Equal("This backend reports no models.", cut.Find("p.backend-empty").TextContent.Trim());
		Assert.Empty(cut.FindAll("table.backend-table"));
	}

	/// <summary>
	/// Verifies that a loaded snapshot with at least one reconciled model renders the table and drops every
	/// placeholder.
	/// </summary>
	[Fact]
	public void Render_WhenSnapshotLoadedWithModels_RendersTable()
	{
		// Act
		IRenderedComponent<BackendModels> cut = RenderModels(reconciliation: Reconciliation(DiscoveredModel()));

		// Assert: exactly one table, no placeholders — the loaded branch rendered fully.
		Assert.Single(cut.FindAll("table.backend-table"));
		Assert.Empty(cut.FindAll("p.backend-empty"));
		Assert.Empty(cut.FindAll("p.backend-error"));
	}

	// --- 2. Header buttons: Refresh and Probe labels and enabled state across the load lifecycle ---

	/// <summary>
	/// Verifies that when idle the Refresh button is enabled and reads "Refresh".
	/// </summary>
	[Fact]
	public void Render_RefreshButton_WhenIdle_IsEnabledWithRefreshLabel()
	{
		// Act
		IRenderedComponent<BackendModels> cut = RenderModels();

		// Assert
		IElement refresh = HeaderButtons(cut)[0];
		Assert.Equal("Refresh", refresh.TextContent.Trim());
		Assert.False(refresh.HasAttribute("disabled"));
	}

	/// <summary>
	/// Verifies that a plain refresh in flight (fetching, not streaming) disables the Refresh button and switches
	/// its label to "Fetching…".
	/// </summary>
	[Fact]
	public void Render_RefreshButton_WhenFetching_IsDisabledWithFetchingLabel()
	{
		// Act
		IRenderedComponent<BackendModels> cut = RenderModels(state: FetchingState());

		// Assert
		IElement refresh = HeaderButtons(cut)[0];
		Assert.Equal("Fetching…", refresh.TextContent.Trim());
		Assert.True(refresh.HasAttribute("disabled"));
	}

	/// <summary>
	/// Verifies that during a streaming probe the Refresh button keeps its plain "Refresh" label (the "Fetching…"
	/// wording is reserved for a non-streaming refresh) but is still disabled while the fetch runs.
	/// </summary>
	[Fact]
	public void Render_RefreshButton_WhenStreaming_IsDisabledWithRefreshLabel()
	{
		// Act
		IRenderedComponent<BackendModels> cut = RenderModels(state: StreamingState());

		// Assert
		IElement refresh = HeaderButtons(cut)[0];
		Assert.Equal("Refresh", refresh.TextContent.Trim());
		Assert.True(refresh.HasAttribute("disabled"));
	}

	/// <summary>
	/// Verifies that when idle the Probe button is enabled and reads "Probe capabilities".
	/// </summary>
	[Fact]
	public void Render_ProbeButton_WhenIdle_IsEnabledWithProbeLabel()
	{
		// Act
		IRenderedComponent<BackendModels> cut = RenderModels();

		// Assert
		IElement probe = HeaderButtons(cut)[1];
		Assert.Equal("Probe capabilities", probe.TextContent.Trim());
		Assert.False(probe.HasAttribute("disabled"));
	}

	/// <summary>
	/// Verifies that during a streaming probe the Probe button is disabled and switches its label to "Probing…".
	/// </summary>
	[Fact]
	public void Render_ProbeButton_WhenStreaming_IsDisabledWithProbingLabel()
	{
		// Act
		IRenderedComponent<BackendModels> cut = RenderModels(state: StreamingState());

		// Assert
		IElement probe = HeaderButtons(cut)[1];
		Assert.Equal("Probing…", probe.TextContent.Trim());
		Assert.True(probe.HasAttribute("disabled"));
	}

	/// <summary>
	/// Verifies that a plain refresh in flight also disables the Probe button while keeping its default label, so a
	/// second fetch cannot be started mid-refresh.
	/// </summary>
	[Fact]
	public void Render_ProbeButton_WhenFetching_IsDisabledWithProbeLabel()
	{
		// Act
		IRenderedComponent<BackendModels> cut = RenderModels(state: FetchingState());

		// Assert
		IElement probe = HeaderButtons(cut)[1];
		Assert.Equal("Probe capabilities", probe.TextContent.Trim());
		Assert.True(probe.HasAttribute("disabled"));
	}

	/// <summary>
	/// Verifies that a busy page (applying, not fetching) disables both header buttons, mirroring the page-wide
	/// busy gate onto the model actions.
	/// </summary>
	[Fact]
	public void Render_HeaderButtons_WhenBusy_AreDisabled()
	{
		// Act: idle load state but the page reports it is busy with an apply.
		IRenderedComponent<BackendModels> cut = RenderModels(isBusy: true);

		// Assert
		IReadOnlyList<IElement> buttons = HeaderButtons(cut);
		Assert.True(buttons[0].HasAttribute("disabled"));
		Assert.True(buttons[1].HasAttribute("disabled"));
	}

	/// <summary>
	/// Returns the two header action buttons (Refresh at index 0, Probe at index 1) scoped to the header actions
	/// container, so the streaming Cancel button — which shares the action class but lives in the progress banner —
	/// is never picked up.
	/// </summary>
	/// <param name="cut">The rendered component.</param>
	/// <returns>The Refresh and Probe buttons in DOM order.</returns>
	private static IReadOnlyList<IElement> HeaderButtons(IRenderedComponent<BackendModels> cut) =>
		cut.FindAll(".backend-models-actions button");
}
