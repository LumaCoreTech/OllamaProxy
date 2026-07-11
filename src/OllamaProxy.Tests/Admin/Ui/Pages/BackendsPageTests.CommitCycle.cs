// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Bunit;

using OllamaProxy.Admin;
using OllamaProxy.Admin.Config;
using OllamaProxy.Admin.Ui.Pages;

namespace OllamaProxy.Tests.Admin.Ui.Pages;

// Commit cycle: how the page moves from clean to dirty and back through Apply and Discard.
//
// A freshly loaded draft is clean, so the sync bar's Apply and Discard are both disabled. Adding a backend
// mutates the draft and flips the page dirty, enabling both. Applying forwards the draft to the service and, on
// success, reloads (so the draft returns to clean); discarding reloads without applying. These tests drive that
// story through the rendered sync bar and the fake service's call counters.
//
// For the load lifecycle and shared harness see the anchor file and Helpers.
public sealed partial class BackendsPageTests
{
	// --- 2. Commit cycle: dirty gating, apply, discard ---

	/// <summary>
	/// Verifies the sync bar's Apply and Discard buttons are disabled right after a load, since a freshly loaded
	/// draft mirrors what is live on disk and has nothing to commit or discard.
	/// </summary>
	[Fact]
	public void Render_WhenDraftIsClean_DisablesApplyAndDiscard()
	{
		// Arrange
		FakeAdminModelService service = new()
		{
			StateFactory = static () => CreateDraft(CreateBackend("primary"))
		};

		// Act
		(IRenderedComponent<Backends> component, FakeAdminModelService _) = RenderBackends(service);

		// Assert
		Assert.True(component.Find("button.sync-bar-apply").HasAttribute("disabled"));
		Assert.True(component.Find("button.sync-bar-discard").HasAttribute("disabled"));
	}

	/// <summary>
	/// Verifies that adding a backend flips the page dirty, enabling both commit-cycle buttons, so the operator can
	/// either apply the newly added backend or discard it.
	/// </summary>
	[Fact]
	public void AddBackend_WhenClicked_EnablesApplyAndDiscard()
	{
		// Arrange
		FakeAdminModelService service = new()
		{
			StateFactory = static () => CreateDraft(CreateBackend("primary"))
		};
		(IRenderedComponent<Backends> component, FakeAdminModelService _) = RenderBackends(service);

		// Act
		component.Find("button.backends-add").Click();

		// Assert
		Assert.False(component.Find("button.sync-bar-apply").HasAttribute("disabled"));
		Assert.False(component.Find("button.sync-bar-discard").HasAttribute("disabled"));
	}

	/// <summary>
	/// Verifies that applying a dirty draft forwards it to the service and, on a successful apply, reloads the
	/// draft so the editor reflects the now-live configuration. The reload is observed as a second
	/// <see cref="IAdminModelService.GetEditableStateAsync"/> call after the initial load.
	/// </summary>
	[Fact]
	public void SyncAsync_WhenDraftDirtyAndApplySucceeds_ForwardsDraftAndReloads()
	{
		// Arrange
		FakeAdminModelService service = new()
		{
			StateFactory = static () => CreateDraft(CreateBackend("primary")),
			ApplyHandler = static _ => ApplyResult.Applied
		};
		(IRenderedComponent<Backends> component, FakeAdminModelService _) = RenderBackends(service);
		component.Find("button.backends-add").Click();

		// Act
		component.Find("button.sync-bar-apply").Click();

		// Assert
		Assert.Equal(1, service.ApplyCallCount);
		Assert.NotNull(service.LastAppliedState);
		Assert.Equal(
			["primary", "new-backend"],
			service.LastAppliedState.Backends.Select(static backend => backend.Name));
		// Initial load plus the post-apply reload.
		Assert.Equal(2, service.GetEditableStateCallCount);
		Assert.Empty(component.FindAll(".backends-dirty-banner"));
	}

	/// <summary>
	/// Verifies that a rejected apply keeps the edits: the draft is not reloaded (so the operator can retry), which
	/// is observed as no additional <see cref="IAdminModelService.GetEditableStateAsync"/> call beyond the initial
	/// load, even though the apply itself was attempted.
	/// </summary>
	[Fact]
	public void SyncAsync_WhenApplyRejected_KeepsEditsWithoutReloading()
	{
		// Arrange
		FakeAdminModelService service = new()
		{
			StateFactory = static () => CreateDraft(CreateBackend("primary")),
			ApplyHandler = static _ => ApplyResult.ValidationRejected(["nope"])
		};
		(IRenderedComponent<Backends> component, FakeAdminModelService _) = RenderBackends(service);
		component.Find("button.backends-add").Click();

		// Act
		component.Find("button.sync-bar-apply").Click();

		// Assert
		Assert.Equal(1, service.ApplyCallCount);
		// Only the initial load ran; a rejected apply does not reload the draft.
		Assert.Equal(1, service.GetEditableStateCallCount);
		Assert.Equal(["primary", "new-backend"], BackendCardNames(component));
	}

	/// <summary>
	/// Verifies that discarding a dirty draft reloads it from the service without applying, so the unsaved edits
	/// are dropped and the editor returns to the live configuration.
	/// </summary>
	[Fact]
	public void DiscardAsync_WhenDraftDirty_ReloadsWithoutApplying()
	{
		// Arrange
		FakeAdminModelService service = new()
		{
			StateFactory = static () => CreateDraft(CreateBackend("primary"))
		};
		(IRenderedComponent<Backends> component, FakeAdminModelService _) = RenderBackends(service);
		component.Find("button.backends-add").Click();

		// Act
		component.Find("button.sync-bar-discard").Click();

		// Assert
		Assert.Equal(0, service.ApplyCallCount);
		// Initial load plus the discard's reload.
		Assert.Equal(2, service.GetEditableStateCallCount);
		Assert.Equal(["primary"], BackendCardNames(component));
	}
}
