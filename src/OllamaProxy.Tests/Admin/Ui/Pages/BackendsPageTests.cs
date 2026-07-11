// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Bunit;

using OllamaProxy.Admin.Editing;
using OllamaProxy.Admin.Ui.Pages;

namespace OllamaProxy.Tests.Admin.Ui.Pages;

/// <summary>
/// Page-level bUnit workflow tests for <see cref="Backends"/>, the full backend configuration editor.
/// </summary>
/// <remarks>
/// These tests follow the page as an operator would drive it: from the initial load, through the empty and
/// populated overviews, into the dirty/apply/discard commit cycle, and finally the guards that protect unsaved
/// edits. They assert which DOM branch renders and how the page orchestrates <see cref="IAdminModelService"/>,
/// rather than duplicating the child-component render tests for backend cards and model tables.
/// <para>Reading order:</para>
/// <list type="number">
///     <item>
///         <description>
///         This anchor: load lifecycle and overview rendering — the initial service load, the empty-state message,
///         and the populated card list.
///         </description>
///     </item>
///     <item>
///         <description>
///         <c>CommitCycle</c>: dirty gating, apply forwarding, success reload, rejected-apply retention, and discard.
///         </description>
///     </item>
///     <item>
///         <description>
///         <c>DirtyState</c>: the visible unsaved-changes banner from clean load through edit and discard.
///         </description>
///     </item>
///     <item>
///         <description>
///         <c>ApplyDiscard</c>: the result banner after success, validation rejection, and write failure.
///         </description>
///     </item>
///     <item>
///         <description>
///         <c>Confirmation</c>: the destructive remove-backend prompt and its confirm/cancel branches.
///         </description>
///     </item>
///     <item>
///         <description>
///         <c>Probe</c>: first-expand fetch, explicit refresh, and streaming probe outcomes.
///         </description>
///     </item>
/// </list>
/// The shared harness, fake admin service, provider catalog, and DOM assertion helpers live in <c>Helpers</c>.
/// </remarks>
[Trait("Category", "Unit")]
public sealed partial class BackendsPageTests : BunitContext
{
	// --- 1. Load lifecycle: initial load, empty state, populated overview ---

	/// <summary>
	/// Verifies the page loads the editable draft from the admin service exactly once during its initial render,
	/// so the operator sees the live configuration without an explicit refresh.
	/// </summary>
	[Fact]
	public void OnInitializedAsync_WhenPageRenders_LoadsConfigurationOnce()
	{
		// Arrange
		FakeAdminModelService service = new() { StateFactory = static () => new DesiredProxyState() };

		// Act
		(IRenderedComponent<Backends> component, FakeAdminModelService _) = RenderBackends(service);

		// Assert
		Assert.Equal(1, service.GetEditableStateCallCount);
		Assert.NotNull(component.Instance);
	}

	/// <summary>
	/// Verifies the page renders the "no backends configured" guidance when the loaded draft is empty, so the
	/// operator is told to add one rather than facing a blank page.
	/// </summary>
	[Fact]
	public void Render_WhenNoBackendsConfigured_ShowsEmptyStateMessage()
	{
		// Arrange
		FakeAdminModelService service = new() { StateFactory = static () => new DesiredProxyState() };

		// Act
		(IRenderedComponent<Backends> component, FakeAdminModelService _) = RenderBackends(service);

		// Assert
		string markup = component.Find(".backends-status").TextContent;
		Assert.Equal("No backends are configured. Add one to get started.", markup);
	}

	/// <summary>
	/// Verifies the page renders one <c>BackendCard</c> per configured backend when the draft is populated, so the
	/// overview lists every backend the operator can edit.
	/// </summary>
	[Fact]
	public void Render_WhenBackendsConfigured_RendersConfiguredCards()
	{
		// Arrange
		FakeAdminModelService service = new()
		{
			StateFactory = static () => CreateDraft(
				CreateBackend("primary"),
				CreateBackend("secondary"))
		};

		// Act
		(IRenderedComponent<Backends> component, FakeAdminModelService _) = RenderBackends(service);

		// Assert
		Assert.Equal(["primary", "secondary"], BackendCardNames(component));
	}
}
