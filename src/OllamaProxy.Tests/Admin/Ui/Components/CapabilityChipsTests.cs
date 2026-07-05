// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Diagnostics.CodeAnalysis;

using AngleSharp.Dom;

using Bunit;

using OllamaProxy.Admin.Ui.Components;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Tests.Admin.Ui.Components;

/// <summary>
/// Tests for <see cref="CapabilityChips"/>, the capability-chip renderer: how the chips
/// <see cref="CapabilityChipBuilder"/> produces become DOM.
/// </summary>
/// <remarks>
/// The classification logic (which capability earns which chip kind) is exhaustively covered by
/// <see cref="CapabilityChipBuilderTests"/>, so these tests deliberately do NOT re-run that matrix. They verify
/// only what rendering owns:
/// <list type="number">
///     <item>
///         <description>
///         Empty result -> a single muted "none" placeholder span, and no chip container
///         (Render_WhenNoCapabilities_RendersNonePlaceholder).
///         </description>
///     </item>
///     <item>
///         <description>
///         Non-empty result -> a "caps-chips" container holding one span per chip, each carrying the chip's CSS
///         class, title tooltip, and visible text, in capability order, with no "none" placeholder
///         (Render_WithCapabilities_RendersOneSpanPerChipWithCssTitleAndText).
///         </description>
///     </item>
///     <item>
///         <description>
///         CSS isolation -> each locally authored element carries Blazor's generated scope attribute, so the
///         scoped stylesheet can style the placeholder, container, and chip spans. The tests assert only the
///         stable "b-" attribute shape, not the generated suffix.
///         </description>
///     </item>
/// </list>
/// </remarks>
[Trait("Category", "Unit")]
public sealed class CapabilityChipsTests : BunitContext
{
	/// <summary>
	/// Verifies that a capability set yielding no chips renders the muted <c>caps-none</c> placeholder with the
	/// literal text "none" and emits no chip container, so an empty cell reads as a deliberate placeholder rather
	/// than a blank.
	/// </summary>
	[Fact]
	public void Render_WhenNoCapabilities_RendersNonePlaceholder()
	{
		// Arrange: every flag off and conclusive, so the builder returns no chips.
		var capabilities = new ModelCapabilities(
			SupportsCompletion: false,
			SupportsTools: false,
			SupportsVision: false,
			SupportsEmbeddings: false,
			CapabilitySource.Default);

		// Act
		IRenderedComponent<CapabilityChips> cut = Render<CapabilityChips>(parameters => parameters
			.Add(chips => chips.Capabilities, capabilities));

		// Assert: the muted placeholder is present with its exact text, and no chip container was rendered.
		IElement placeholder = cut.Find("span.caps-none");
		Assert.Equal("none", placeholder.TextContent);
		AssertHasCssIsolationScope(placeholder);
		Assert.Empty(cut.FindAll("div.caps-chips"));
	}

	/// <summary>
	/// Verifies that a capability set yielding chips renders a <c>caps-chips</c> container with one span per
	/// chip — each carrying the chip's CSS class, title tooltip, and visible text — in capability order, and no
	/// "none" placeholder.
	/// </summary>
	[Fact]
	public void Render_WithCapabilities_RendersOneSpanPerChipWithCssTitleAndText()
	{
		// Arrange: completion confirmed and vision inconclusive produce exactly two chips (tools and embeddings
		// are measured-unsupported and omitted), so the rendered order and per-chip attributes can be pinned.
		var capabilities = new ModelCapabilities(
			SupportsCompletion: true,
			SupportsTools: false,
			SupportsVision: false,
			SupportsEmbeddings: false,
			CapabilitySource.Probed,
			InconclusiveCapabilities.Vision);

		// Act
		IRenderedComponent<CapabilityChips> cut = Render<CapabilityChips>(parameters => parameters
			.Add(chips => chips.Capabilities, capabilities));

		// Assert: no placeholder, and the container holds the two chips in capability order with full attributes.
		Assert.Empty(cut.FindAll("span.caps-none"));

		IElement container = cut.Find("div.caps-chips");
		AssertHasCssIsolationScope(container);

		IReadOnlyList<IElement> spans = cut.FindAll("div.caps-chips > span");
		Assert.Equal(2, spans.Count);

		AssertChipSpan(
			spans[0],
			"cap-chip cap-chip-supported",
			"Supports completion (confirmed by probe or backend metadata).",
			"completion");
		AssertChipSpan(
			spans[1],
			"cap-chip cap-chip-inconclusive",
			"The vision probe stayed inconclusive (it timed out or kept failing), so this is unconfirmed — " +
			"the model may still support it. Pin the model to set its capabilities explicitly.",
			"vision ?");
		AssertHasCssIsolationScope(spans[0]);
		AssertHasCssIsolationScope(spans[1]);
	}

	#region Test infrastructure

	/// <summary>
	/// Asserts a rendered chip span's complete observable state: its class attribute, title tooltip, and visible
	/// text.
	/// </summary>
	/// <param name="span">The rendered chip span to verify.</param>
	/// <param name="expectedClass">The expected class attribute.</param>
	/// <param name="expectedTitle">The expected title tooltip.</param>
	/// <param name="expectedText">The expected visible text.</param>
	[SuppressMessage("ReSharper", "ParameterOnlyUsedForPreconditionCheck.Local")]
	private static void AssertChipSpan(
		IElement span,
		string   expectedClass,
		string   expectedTitle,
		string   expectedText)
	{
		Assert.Equal(expectedClass, span.ClassName);
		Assert.Equal(expectedTitle, span.GetAttribute("title"));
		Assert.Equal(expectedText, span.TextContent);
	}

	/// <summary>
	/// Asserts that a locally authored element received Blazor's generated CSS-isolation scope attribute without
	/// coupling the test to the generated suffix.
	/// </summary>
	/// <param name="element">The rendered element expected to be scoped by <see cref="CapabilityChips"/>.</param>
	[SuppressMessage("ReSharper", "ParameterOnlyUsedForPreconditionCheck.Local")]
	private static void AssertHasCssIsolationScope(IElement element)
	{
		IAttr[] scopeAttributes = element.Attributes
			.Where(attribute => attribute.Name.StartsWith("b-", StringComparison.Ordinal))
			.ToArray();

		Assert.Equal(1, scopeAttributes.Length);
		Assert.Equal(string.Empty, scopeAttributes[0].Value);
	}

	#endregion
}
