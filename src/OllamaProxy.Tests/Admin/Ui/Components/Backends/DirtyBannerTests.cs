// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using AngleSharp.Dom;

using Bunit;

using OllamaProxy.Admin.Ui.Components.Backends;

namespace OllamaProxy.Tests.Admin.Ui.Components.Backends;

/// <summary>
/// Render tests for <see cref="DirtyBanner"/>, the "you have unsaved changes" reminder shown while the draft has
/// unsaved edits. The banner is a pure gate on its <see cref="DirtyBanner.Visible"/> flag: these tests assert it
/// renders with its live-region ARIA attributes and visible copy when visible, and emits nothing when not.
/// </summary>
[Trait("Category", "Unit")]
public sealed class DirtyBannerTests : BunitContext
{
	/// <summary>
	/// Verifies that when the draft is dirty the banner renders with the polite live-region role so screen readers
	/// announce the transition, and shows the unsaved-changes text.
	/// </summary>
	[Fact]
	public void Render_WhenVisible_RendersLiveRegionBannerWithText()
	{
		// Act
		IRenderedComponent<DirtyBanner> cut = RenderBanner(visible: true);

		// Assert: the banner is a polite status live region so it announces without grabbing focus.
		IElement banner = cut.Find("div.backends-dirty-banner");
		Assert.Equal("status", banner.GetAttribute("role"));
		Assert.Equal("polite", banner.GetAttribute("aria-live"));
		Assert.Equal("You have unsaved changes.", cut.Find("span.backends-dirty-text").TextContent.Trim());
	}

	/// <summary>
	/// Verifies that when the draft is clean the component emits nothing, so no reminder is shown.
	/// </summary>
	[Fact]
	public void Render_WhenNotVisible_RendersNothing()
	{
		// Act
		IRenderedComponent<DirtyBanner> cut = RenderBanner(visible: false);

		// Assert
		Assert.Empty(cut.FindAll("div.backends-dirty-banner"));
	}

	/// <summary>
	/// Renders <see cref="DirtyBanner"/> with the supplied visibility flag.
	/// </summary>
	/// <param name="visible">Whether the draft has unsaved edits.</param>
	/// <returns>The rendered <see cref="DirtyBanner"/> component.</returns>
	private IRenderedComponent<DirtyBanner> RenderBanner(bool visible)
	{
		return Render<DirtyBanner>(parameters => parameters
			.Add(component => component.Visible, visible));
	}
}
