// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using AngleSharp.Dom;

using Bunit;

using OllamaProxy.Admin.Ui.Components;

namespace OllamaProxy.Tests.Admin.Ui.Components;

/// <summary>
/// Tests for <see cref="OllamaProxyLogo"/>, the inline-SVG brand mark: the caller-supplied root class, the
/// attribute splat onto the <c>svg</c> element, and the <see cref="OllamaProxyLogo.ShouldBlink"/> toggle that
/// activates the animated eye lid.
/// </summary>
/// <remarks>
/// The logo is a purely presentational component, so its contract is what it renders onto the root and the lid:
/// <list type="number">
///     <item>
///         <description>
///         Root class: the <see cref="OllamaProxyLogo.Class"/> parameter is applied verbatim to the <c>svg</c>
///         root (AppliesRootClass), and is absent when not supplied (OmitsClassWhenNotSupplied).
///         </description>
///     </item>
///     <item>
///         <description>
///         Attribute splat: an unmatched attribute (e.g. an <c>aria-label</c>) is splatted onto the <c>svg</c>
///         root so a caller can attach accessibility metadata without a dedicated parameter (SplatsAttributes).
///         </description>
///     </item>
///     <item>
///         <description>
///         Blink toggle: <see cref="OllamaProxyLogo.ShouldBlink"/> adds the <c>logo-lid--active</c> modifier to
///         the lid rect when set (ActivatesLid) and leaves it off by default (LidInactiveByDefault).
///         </description>
///     </item>
/// </list>
/// </remarks>
[Trait("Category", "Unit")]
public sealed class OllamaProxyLogoTests : BunitContext
{
	// --- 1. Root class ---

	/// <summary>
	/// Verifies the <see cref="OllamaProxyLogo.Class"/> parameter is applied verbatim to the <c>svg</c> root, so a
	/// caller can size and place the logo through its own class.
	/// </summary>
	[Fact]
	public void Render_WithClass_AppliesRootClass()
	{
		// Arrange
		const string cssClass = "site-logo";

		// Act
		IRenderedComponent<OllamaProxyLogo> cut =
			Render<OllamaProxyLogo>(parameters => parameters.Add(logo => logo.Class, cssClass));

		// Assert
		Assert.Equal(cssClass, cut.Find("svg").ClassName);
	}

	/// <summary>
	/// Verifies that when no class is supplied the <c>svg</c> root carries no class attribute, rather than an
	/// empty-string class, so the markup stays clean.
	/// </summary>
	[Fact]
	public void Render_WithoutClass_OmitsClassAttribute()
	{
		// Arrange

		// Act
		IRenderedComponent<OllamaProxyLogo> cut = Render<OllamaProxyLogo>();

		// Assert
		Assert.False(cut.Find("svg").HasAttribute("class"));
	}

	// --- 2. Attribute splat ---

	/// <summary>
	/// Verifies that an unmatched attribute (here an <c>aria-label</c>) is splatted onto the <c>svg</c> root, so a
	/// caller can attach accessibility metadata without the component declaring a dedicated parameter for it.
	/// </summary>
	[Fact]
	public void Render_WithAdditionalAttributes_SplatsThemOntoSvg()
	{
		// Arrange
		const string label = "OllamaProxy logo";

		// Act
		IRenderedComponent<OllamaProxyLogo> cut = Render<OllamaProxyLogo>(parameters =>
			parameters.AddUnmatched("aria-label", label));

		// Assert
		Assert.Equal(label, cut.Find("svg").GetAttribute("aria-label"));
	}

	// --- 3. Blink toggle ---

	/// <summary>
	/// Verifies that setting <see cref="OllamaProxyLogo.ShouldBlink"/> adds the <c>logo-lid--active</c> modifier to
	/// the lid rect, so the eye animates.
	/// </summary>
	[Fact]
	public void Render_WithShouldBlink_ActivatesLid()
	{
		// Arrange

		// Act
		IRenderedComponent<OllamaProxyLogo> cut =
			Render<OllamaProxyLogo>(parameters => parameters.Add(logo => logo.ShouldBlink, true));

		// Assert
		IElement lid = cut.Find("rect.logo-lid");
		Assert.Contains("logo-lid--active", lid.ClassList);
	}

	/// <summary>
	/// Verifies that the lid carries only the base <c>logo-lid</c> class by default, so the eye stays still unless
	/// blinking is explicitly requested.
	/// </summary>
	[Fact]
	public void Render_WithoutShouldBlink_LeavesLidInactive()
	{
		// Arrange

		// Act
		IRenderedComponent<OllamaProxyLogo> cut = Render<OllamaProxyLogo>();

		// Assert
		IElement lid = cut.Find("rect.logo-lid");
		Assert.DoesNotContain("logo-lid--active", lid.ClassList);
	}
}
