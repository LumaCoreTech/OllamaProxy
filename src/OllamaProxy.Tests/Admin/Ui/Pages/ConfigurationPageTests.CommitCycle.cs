// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Bunit;

using OllamaProxy.Admin;
using OllamaProxy.Admin.Config;
// The page type OllamaProxy.Admin.Ui.Pages.Configuration collides with the OllamaProxy.Configuration namespace,
// so it is aliased rather than imported via a plain using of OllamaProxy.Admin.Ui.Pages.
using ConfigurationPage = OllamaProxy.Admin.Ui.Pages.Configuration;

namespace OllamaProxy.Tests.Admin.Ui.Pages;

// Commit cycle: how the page moves from clean to dirty and back through Apply and Discard.
//
// A freshly loaded draft is clean, so the sync bar's Apply and Discard are both disabled. Editing the listener
// URL mutates the draft and flips the page dirty, enabling both. Applying forwards the draft to the service and,
// on success, reloads (so the draft returns to clean); discarding reloads without applying. A rejected apply keeps
// the edits so the operator can retry. These tests drive that story through the rendered sync bar and the fake
// service's call counters.
//
// For the load lifecycle and shared harness see the anchor file and Helpers.
public sealed partial class ConfigurationPageTests
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
		FakeAdminModelService service = new() { StateFactory = static () => CreateDraft() };

		// Act
		(IRenderedComponent<ConfigurationPage> component, FakeAdminModelService _) = RenderConfiguration(service);

		// Assert
		Assert.True(component.Find("button.sync-bar-apply").HasAttribute("disabled"));
		Assert.True(component.Find("button.sync-bar-discard").HasAttribute("disabled"));
	}

	/// <summary>
	/// Verifies that editing the listener URL flips the page dirty, enabling both commit-cycle buttons, so the
	/// operator can either apply the edit or discard it.
	/// </summary>
	[Fact]
	public void EditListenUrl_WhenChanged_EnablesApplyAndDiscard()
	{
		// Arrange
		FakeAdminModelService service = new()
		{
			StateFactory = static () => CreateDraft(listenUrl: "http://localhost:11434")
		};
		(IRenderedComponent<ConfigurationPage> component, FakeAdminModelService _) = RenderConfiguration(service);

		// Act
		component.Find("input.configuration-input").Change("http://0.0.0.0:11434");

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
	public void ApplyAsync_WhenDraftDirtyAndApplySucceeds_ForwardsDraftAndReloads()
	{
		// Arrange
		FakeAdminModelService service = new()
		{
			StateFactory = static () => CreateDraft(listenUrl: "http://localhost:11434"),
			ApplyHandler = static _ => ApplyResult.Applied
		};
		(IRenderedComponent<ConfigurationPage> component, FakeAdminModelService _) = RenderConfiguration(service);
		component.Find("input.configuration-input").Change("http://0.0.0.0:11434");

		// Act
		component.Find("button.sync-bar-apply").Click();

		// Assert
		Assert.Equal(1, service.ApplyCallCount);
		Assert.NotNull(service.LastAppliedState);
		Assert.Equal("http://0.0.0.0:11434", service.LastAppliedState.ListenUrl);
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
	public void ApplyAsync_WhenApplyRejected_KeepsEditsWithoutReloading()
	{
		// Arrange
		FakeAdminModelService service = new()
		{
			StateFactory = static () => CreateDraft(listenUrl: "http://localhost:11434"),
			ApplyHandler = static _ => ApplyResult.ValidationRejected(["nope"])
		};
		(IRenderedComponent<ConfigurationPage> component, FakeAdminModelService _) = RenderConfiguration(service);
		component.Find("input.configuration-input").Change("http://0.0.0.0:11434");

		// Act
		component.Find("button.sync-bar-apply").Click();

		// Assert
		Assert.Equal(1, service.ApplyCallCount);
		// Only the initial load ran; a rejected apply does not reload the draft.
		Assert.Equal(1, service.GetEditableStateCallCount);
		Assert.Equal("http://0.0.0.0:11434", ListenUrlValue(component));
	}

	/// <summary>
	/// Verifies that discarding a dirty draft reloads it from the service without applying, so the unsaved edit is
	/// dropped and the editor returns to the live configuration.
	/// </summary>
	[Fact]
	public void DiscardAsync_WhenDraftDirty_ReloadsWithoutApplying()
	{
		// Arrange
		FakeAdminModelService service = new()
		{
			StateFactory = static () => CreateDraft(listenUrl: "http://localhost:11434")
		};
		(IRenderedComponent<ConfigurationPage> component, FakeAdminModelService _) = RenderConfiguration(service);
		component.Find("input.configuration-input").Change("http://0.0.0.0:11434");

		// Act
		component.Find("button.sync-bar-discard").Click();

		// Assert
		Assert.Equal(0, service.ApplyCallCount);
		// Initial load plus the discard's reload.
		Assert.Equal(2, service.GetEditableStateCallCount);
		Assert.Equal("http://localhost:11434", ListenUrlValue(component));
	}
}
