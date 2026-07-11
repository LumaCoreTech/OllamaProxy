// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Bunit;

using OllamaProxy.Admin.Ui.Pages;

namespace OllamaProxy.Tests.Admin.Ui.Pages;

// Dirty banner: the unsaved-changes reminder that tracks the draft against its loaded baseline.
//
// A freshly loaded draft is clean, so the banner is absent. Editing the draft (adding a backend) flips the page
// dirty and the banner appears between the header and the cards; discarding reloads the draft and the banner
// disappears again. The banner shares its flag with the JS beforeunload guard and the in-app navigation guard,
// so its visibility is the page's single observable dirty signal.
//
// For the load lifecycle and shared harness see the anchor file and Helpers.
public sealed partial class BackendsPageTests
{
	// --- 3. Dirty banner: appears on edit, clears on discard ---

	/// <summary>
	/// Verifies the unsaved-changes banner is absent right after a load, since a freshly loaded draft mirrors what
	/// is live on disk and is by definition clean.
	/// </summary>
	[Fact]
	public void Render_WhenDraftIsClean_HidesDirtyBanner()
	{
		// Arrange
		FakeAdminModelService service = new()
		{
			StateFactory = static () => CreateDraft(CreateBackend("primary"))
		};

		// Act
		(IRenderedComponent<Backends> component, FakeAdminModelService _) = RenderBackends(service);

		// Assert
		Assert.Empty(component.FindAll(".backends-dirty-banner"));
	}

	/// <summary>
	/// Verifies that editing the draft (adding a backend) shows the unsaved-changes banner, so the operator is
	/// reminded to apply or discard before leaving.
	/// </summary>
	[Fact]
	public void AddBackend_WhenClicked_ShowsDirtyBanner()
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
		Assert.Single(component.FindAll(".backends-dirty-banner"));
	}

	/// <summary>
	/// Verifies that discarding a dirty draft reloads the clean baseline and hides the unsaved-changes banner, so
	/// the page stops nagging about edits that no longer exist.
	/// </summary>
	[Fact]
	public void DiscardAsync_WhenDraftDirty_HidesDirtyBanner()
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
		Assert.Empty(component.FindAll(".backends-dirty-banner"));
	}
}
