// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Bunit;
// The page type OllamaProxy.Admin.Ui.Pages.Configuration collides with the OllamaProxy.Configuration namespace,
// so it is aliased rather than imported via a plain using of OllamaProxy.Admin.Ui.Pages.
using ConfigurationPage = OllamaProxy.Admin.Ui.Pages.Configuration;

namespace OllamaProxy.Tests.Admin.Ui.Pages;

// Dirty banner: the visible "you have unsaved changes" reminder that tracks the draft state.
//
// The banner is absent on a clean load, appears the moment the listener URL is edited, and disappears again once
// the edit is reverted (either by discarding or by manually restoring the original value). These tests assert the
// banner's presence in each state; the button-enablement side of dirty tracking lives in CommitCycle.
//
// For the shared harness see the anchor file and Helpers.
public sealed partial class ConfigurationPageTests
{
	// --- 3. Dirty banner: hidden, shown, cleared ---

	/// <summary>
	/// Verifies the unsaved-changes banner is absent after a clean load, since the draft still mirrors the live
	/// configuration and there is nothing to warn about.
	/// </summary>
	[Fact]
	public void Render_WhenDraftIsClean_HidesDirtyBanner()
	{
		// Arrange
		FakeAdminModelService service = new() { StateFactory = static () => CreateDraft() };

		// Act
		(IRenderedComponent<ConfigurationPage> component, FakeAdminModelService _) = RenderConfiguration(service);

		// Assert
		Assert.Empty(component.FindAll(".backends-dirty-banner"));
	}

	/// <summary>
	/// Verifies editing the listener URL shows the unsaved-changes banner, warning the operator that the draft
	/// diverges from the live configuration.
	/// </summary>
	[Fact]
	public void EditListenUrl_WhenChanged_ShowsDirtyBanner()
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
		Assert.Single(component.FindAll(".backends-dirty-banner"));
	}

	/// <summary>
	/// Verifies that restoring the listener URL to its loaded value clears the unsaved-changes banner, since the
	/// draft once again matches the live configuration even though it was edited in between.
	/// </summary>
	[Fact]
	public void EditListenUrl_WhenRevertedToLoadedValue_HidesDirtyBanner()
	{
		// Arrange
		FakeAdminModelService service = new()
		{
			StateFactory = static () => CreateDraft(listenUrl: "http://localhost:11434")
		};
		(IRenderedComponent<ConfigurationPage> component, FakeAdminModelService _) = RenderConfiguration(service);
		component.Find("input.configuration-input").Change("http://0.0.0.0:11434");

		// Act
		component.Find("input.configuration-input").Change("http://localhost:11434");

		// Assert
		Assert.Empty(component.FindAll(".backends-dirty-banner"));
	}
}
