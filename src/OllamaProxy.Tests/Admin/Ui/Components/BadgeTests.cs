// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using AngleSharp.Dom;
using Bunit;
using OllamaProxy.Admin.Ui.Components;

namespace OllamaProxy.Tests.Admin.Ui.Components;

/// <summary>
/// Tests for <see cref="Badge"/>, the shared pill primitive rendered: variant -> colour class, child content,
/// and attribute splat.
/// </summary>
/// <remarks>
/// Badge is the one place the pill shape and palette live, so every badge across the admin surface routes through
/// it. These tests verify the three things a caller relies on:
/// <list type="number">
///     <item>
///         <description>
///         Variant -> CSS class: each <see cref="BadgeVariant"/> renders the base "badge" class plus its
///         "badge-*" colour class, and an unspecified variant falls back to the Neutral default
///         (RendersNeutralClass).
///         </description>
///     </item>
///     <item>
///         <description>
///         Child content: the label a caller passes is rendered inside the span (RendersChildContent).
///         </description>
///     </item>
///     <item>
///         <description>
///         Attribute splat: any extra attribute (e.g. a title tooltip) is splatted onto the rendered span so a
///         caller can attach a tooltip without a dedicated parameter (SplatsAdditionalAttributes).
///         </description>
///     </item>
/// </list>
/// Assertions read the whole class attribute (ClassName) rather than probing for a single token, so a regression
/// that drops the base "badge" class or reorders the pair is caught. The CSS-isolation scope attribute is a
/// separate attribute and does not appear in ClassName, so it does not affect these assertions.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class BadgeTests : BunitContext
{
	/// <summary>
	/// The variant-to-CSS-class scenarios: every <see cref="BadgeVariant"/> paired with the exact class
	/// attribute the badge must render (the base <c>badge</c> class plus the variant's colour class).
	/// </summary>
	public static TheoryData<BadgeVariant, string> VariantClassCases => new()
	{
		{ BadgeVariant.Neutral, "badge badge-neutral" },
		{ BadgeVariant.Success, "badge badge-success" },
		{ BadgeVariant.Danger, "badge badge-danger" },
		{ BadgeVariant.Info, "badge badge-info" },
		{ BadgeVariant.Warning, "badge badge-warning" }
	};

	/// <summary>
	/// Verifies that each badge variant renders the base <c>badge</c> class together with its colour class, in
	/// that order, as the span's complete class attribute.
	/// </summary>
	/// <param name="variant">The badge variant to render.</param>
	/// <param name="expectedClass">The exact class attribute the span must carry.</param>
	[Theory]
	[MemberData(nameof(VariantClassCases))]
	public void Render_WithVariant_AppliesBaseAndVariantCssClass(BadgeVariant variant, string expectedClass)
	{
		// Arrange

		// Act
		IRenderedComponent<Badge> cut = Render<Badge>(parameters => parameters.Add(badge => badge.Variant, variant));

		// Assert: the complete class attribute is the base class plus the variant class, in order.
		IElement span = cut.Find("span");
		Assert.Equal(expectedClass, span.ClassName);
	}

	/// <summary>
	/// Verifies that a badge rendered without an explicit variant falls back to the <c>badge-neutral</c> colour
	/// class, matching the <see cref="BadgeVariant.Neutral"/> parameter default.
	/// </summary>
	[Fact]
	public void Render_WithoutVariant_RendersNeutralClass()
	{
		// Arrange: no Variant parameter is supplied, so the component's default applies.

		// Act
		IRenderedComponent<Badge> cut = Render<Badge>();

		// Assert
		IElement span = cut.Find("span");
		Assert.Equal("badge badge-neutral", span.ClassName);
	}

	/// <summary>
	/// Verifies that the child content a caller supplies is rendered as the badge's visible label.
	/// </summary>
	[Fact]
	public void Render_WithChildContent_RendersContentAsLabel()
	{
		// Arrange
		const string label = "Available";

		// Act
		IRenderedComponent<Badge> cut = Render<Badge>(parameters => parameters.AddChildContent(label));

		// Assert
		IElement span = cut.Find("span");
		Assert.Equal(label, span.TextContent);
	}

	/// <summary>
	/// Verifies that an unmatched attribute (here a <c>title</c> tooltip) is splatted onto the rendered span, so
	/// a caller can attach a tooltip without the component declaring a dedicated parameter for it.
	/// </summary>
	[Fact]
	public void Render_WithAdditionalAttributes_SplatsThemOntoSpan()
	{
		// Arrange: a title attribute is not a declared Badge parameter, so it must flow through AdditionalAttributes
		// (CaptureUnmatchedValues) onto the span.
		const string title = "Pin no longer matches the backend";

		// Act
		IRenderedComponent<Badge> cut = Render<Badge>(parameters => parameters
			.AddChildContent("Drifted")
			.AddUnmatched("title", title));

		// Assert: the tooltip landed on the span, and the label still renders (no side effect on content).
		IElement span = cut.Find("span");
		Assert.Equal(title, span.GetAttribute("title"));
		Assert.Equal("Drifted", span.TextContent);
	}
}
