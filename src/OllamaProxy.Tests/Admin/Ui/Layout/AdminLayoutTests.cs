// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using AngleSharp.Dom;

using Bunit;

using Microsoft.AspNetCore.Components;

using OllamaProxy.Admin.Ui.Layout;

namespace OllamaProxy.Tests.Admin.Ui.Layout;

/// <summary>
/// Tests for <see cref="AdminLayout"/>, the admin shell: the branded header, the top-level navigation links, the
/// routed body, and the Configuration dropdown's <c>aria-expanded</c> honesty on focus.
/// </summary>
/// <remarks>
/// The layout wraps every admin page, so its contract is the chrome it renders and the one piece of behavior it
/// owns:
/// <list type="number">
///     <item>
///         <description>
///         Navigation: the Home, Models, and Configuration (General/Backends) entry points are rendered with the
///         expected hrefs (RendersNavLinks).
///         </description>
///     </item>
///     <item>
///         <description>
///         Body: the routed page content supplied via <c>Body</c> is rendered inside the content wrapper
///         (RendersBody).
///         </description>
///     </item>
///     <item>
///         <description>
///         Dropdown accessibility: the Configuration trigger reports <c>aria-expanded="false"</c> initially, flips
///         to <c>true</c> on <c>focusin</c>, and back to <c>false</c> on <c>focusout</c>, keeping the flag honest
///         for screen readers (TogglesAriaExpanded*).
///         </description>
///     </item>
/// </list>
/// bUnit registers a fake <c>NavigationManager</c> by default, which the <c>NavLink</c> instances depend on.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class AdminLayoutTests : BunitContext
{
	// --- 1. Navigation ---

	/// <summary>
	/// Verifies that the layout renders the top-level navigation entry points (Home, Models, and the Configuration
	/// group's General and Backends links) with their expected hrefs.
	/// </summary>
	[Fact]
	public void Render_Nav_RendersExpectedLinks()
	{
		// Arrange

		// Act
		IRenderedComponent<AdminLayout> cut = Render<AdminLayout>();

		// Assert: NavLink renders an anchor whose href reflects the route (Home is the empty/root route).
		IReadOnlyList<IElement> anchors = cut.FindAll("nav a");
		Assert.Collection(
			anchors,
			home => Assert.Equal("", home.GetAttribute("href")),
			models => Assert.Equal("models", models.GetAttribute("href")),
			general => Assert.Equal("configuration/general", general.GetAttribute("href")),
			backends => Assert.Equal("configuration/backends", backends.GetAttribute("href")));
	}

	// --- 2. Body ---

	/// <summary>
	/// Verifies that the routed page content supplied through the layout's <c>Body</c> render fragment is rendered
	/// inside the content wrapper.
	/// </summary>
	[Fact]
	public void Render_WithBody_RendersBodyContent()
	{
		// Arrange
		const string marker = "routed-page-content";
		RenderFragment body = builder => builder.AddMarkupContent(0, $"<p>{marker}</p>");

		// Act
		IRenderedComponent<AdminLayout> cut =
			Render<AdminLayout>(parameters => parameters.Add(layout => layout.Body, body));

		// Assert
		IElement content = cut.Find(".admin-shell-content");
		Assert.Contains(marker, content.TextContent);
	}

	// --- 3. Dropdown accessibility ---

	/// <summary>
	/// Verifies that the Configuration dropdown trigger reports <c>aria-expanded="false"</c> before it receives
	/// focus, so screen readers see the collapsed state on load.
	/// </summary>
	[Fact]
	public void Render_ConfigurationTrigger_IsCollapsedByDefault()
	{
		// Arrange

		// Act
		IRenderedComponent<AdminLayout> cut = Render<AdminLayout>();

		// Assert
		IElement trigger = cut.Find(".admin-shell-tab-group-label");
		Assert.Equal("false", trigger.GetAttribute("aria-expanded"));
	}

	/// <summary>
	/// Verifies that focus entering the Configuration group flips the trigger's <c>aria-expanded</c> to
	/// <c>true</c>, matching the CSS-driven open state so the flag stays honest for screen readers.
	/// </summary>
	[Fact]
	public void FocusIn_ConfigurationGroup_SetsAriaExpandedTrue()
	{
		// Arrange
		IRenderedComponent<AdminLayout> cut = Render<AdminLayout>();

		// Act: focusin is bound on the group container, not the button itself.
		cut.Find(".admin-shell-tab-group").FocusIn();

		// Assert
		IElement trigger = cut.Find(".admin-shell-tab-group-label");
		Assert.Equal("true", trigger.GetAttribute("aria-expanded"));
	}

	/// <summary>
	/// Verifies that focus leaving the Configuration group clears the trigger's <c>aria-expanded</c> back to
	/// <c>false</c>, so a screen reader is not told the menu is open after focus has moved away.
	/// </summary>
	[Fact]
	public void FocusOut_ConfigurationGroup_SetsAriaExpandedFalse()
	{
		// Arrange: open the group first so the focusout transition is observable.
		IRenderedComponent<AdminLayout> cut = Render<AdminLayout>();
		cut.Find(".admin-shell-tab-group").FocusIn();

		// Act
		cut.Find(".admin-shell-tab-group").FocusOut();

		// Assert
		IElement trigger = cut.Find(".admin-shell-tab-group-label");
		Assert.Equal("false", trigger.GetAttribute("aria-expanded"));
	}
}
