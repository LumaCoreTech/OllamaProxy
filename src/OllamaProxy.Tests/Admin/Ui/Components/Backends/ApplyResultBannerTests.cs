// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using AngleSharp.Dom;

using Bunit;

using OllamaProxy.Admin.Config;
using OllamaProxy.Admin.Ui.Components.Backends;

namespace OllamaProxy.Tests.Admin.Ui.Components.Backends;

/// <summary>
/// Render tests for <see cref="ApplyResultBanner"/>, the outcome banner shown after a sync attempt. The pure copy
/// it delegates to is covered by <see cref="ApplyResultBannerPresenterTests"/>; these tests assert which DOM the
/// banner emits for a given outcome and error list — whether it renders at all, which
/// <c>.apply-result-*</c> modifier colors it, and how the field-level error list is shown.
/// </summary>
/// <remarks>
/// Three concerns, checked from both sides so a test cannot pass against a banner that wired one branch
/// permanently:
/// <list type="number">
///     <item>
///         <description>Visibility: a null outcome renders nothing at all.</description>
///     </item>
///     <item>
///         <description>
///         Outcome mapping: each outcome renders the banner with its exact modifier class and headline copy.
///         </description>
///     </item>
///     <item>
///         <description>
///         Error list: a populated list renders one item per error, while a null or empty list omits the list
///         entirely.
///         </description>
///     </item>
/// </list>
/// </remarks>
[Trait("Category", "Unit")]
public sealed class ApplyResultBannerTests : BunitContext
{
	// --- 1. Visibility ---

	/// <summary>
	/// Verifies that with no outcome (no apply attempted in this page lifetime) the component emits nothing, so the
	/// page shows no banner at all.
	/// </summary>
	[Fact]
	public void Render_WhenOutcomeNull_RendersNothing()
	{
		// Act
		IRenderedComponent<ApplyResultBanner> cut = RenderBanner(outcome: null);

		// Assert
		Assert.Empty(cut.FindAll("div.apply-result"));
	}

	// --- 2. Outcome mapping ---

	/// <summary>
	/// The outcome-to-DOM cases: each apply outcome renders the banner with its exact modifier class (the contract
	/// with the scoped stylesheet) and its operator-facing headline.
	/// </summary>
	public static TheoryData<string, ApplyOutcome, string, string> OutcomeCases => new()
	{
		// Successful apply: green banner, proxy running the updated configuration.
		{
			"Applied",
			ApplyOutcome.Applied,
			"apply-result apply-result-applied",
			"Changes applied. The proxy is now running the updated configuration."
		},
		// Validation dry-run rejected: amber banner, previous configuration still live.
		{
			"ValidationRejected",
			ApplyOutcome.ValidationRejected,
			"apply-result apply-result-rejected",
			"The change was rejected and rolled back. The previous configuration is still live."
		},
		// Write to disk failed: red banner, previous configuration still live.
		{
			"WriteFailed",
			ApplyOutcome.WriteFailed,
			"apply-result apply-result-failed",
			"The configuration could not be written. The previous configuration is still live."
		}
	};

	/// <summary>
	/// Verifies that a set outcome renders the banner with the modifier class that colors it and the headline copy
	/// the operator reads.
	/// </summary>
	/// <param name="scenario">Descriptive name of the test case, used only for readable test output.</param>
	/// <param name="outcome">The apply outcome under test.</param>
	/// <param name="expectedClass">The full expected class attribute of the banner root.</param>
	/// <param name="expectedHeadline">The expected headline text.</param>
	[Theory]
	[MemberData(nameof(OutcomeCases))]
	public void Render_WhenOutcomeSet_RendersModifierAndHeadline(
		string       scenario,
		ApplyOutcome outcome,
		string       expectedClass,
		string       expectedHeadline)
	{
		_ = scenario;

		// Act
		IRenderedComponent<ApplyResultBanner> cut = RenderBanner(outcome);

		// Assert
		IElement banner = cut.Find("div.apply-result");
		Assert.Equal(expectedClass, banner.ClassName);
		Assert.Equal(expectedHeadline, cut.Find("p.apply-result-headline").TextContent.Trim());
	}

	// --- 3. Error list ---

	/// <summary>
	/// Verifies that a rejection carrying field-level reasons renders them as a list, one item per reason in order,
	/// so the operator sees exactly what the dry-run flagged.
	/// </summary>
	[Fact]
	public void Render_WhenErrorsPresent_RendersAllErrorsAsList()
	{
		// Arrange: a validation rejection is the outcome that typically carries field-level reasons.
		string[] errors = ["Backend 'openai' has no base URL.", "Model name must be unique."];

		// Act
		IRenderedComponent<ApplyResultBanner> cut = RenderBanner(ApplyOutcome.ValidationRejected, errors);

		// Assert: every reason is rendered as its own list item, in the supplied order.
		IReadOnlyList<IElement> items = cut.FindAll("ul.apply-result-errors li");
		Assert.Equal(2, items.Count);
		Assert.Equal("Backend 'openai' has no base URL.", items[0].TextContent.Trim());
		Assert.Equal("Model name must be unique.", items[1].TextContent.Trim());
	}

	/// <summary>
	/// Verifies that a banner with a null error list omits the list entirely, so a successful apply shows only its
	/// headline.
	/// </summary>
	[Fact]
	public void Render_WhenErrorsNull_OmitsErrorList()
	{
		// Act: a successful apply carries no errors.
		IRenderedComponent<ApplyResultBanner> cut = RenderBanner(ApplyOutcome.Applied, errors: null);

		// Assert
		Assert.Empty(cut.FindAll("ul.apply-result-errors"));
	}

	/// <summary>
	/// Verifies that a banner with an empty (but non-null) error list also omits the list, proving the guard is on
	/// the count rather than on null alone.
	/// </summary>
	[Fact]
	public void Render_WhenErrorsEmpty_OmitsErrorList()
	{
		// Act: an empty list exercises the Count > 0 branch distinctly from the null case above.
		IRenderedComponent<ApplyResultBanner> cut = RenderBanner(ApplyOutcome.ValidationRejected, errors: []);

		// Assert
		Assert.Empty(cut.FindAll("ul.apply-result-errors"));
	}

	/// <summary>
	/// Renders <see cref="ApplyResultBanner"/> with the supplied outcome and error list.
	/// </summary>
	/// <param name="outcome">The apply outcome, or <see langword="null"/> for the no-attempt state.</param>
	/// <param name="errors">The field-level error reasons, or <see langword="null"/> for none.</param>
	/// <returns>The rendered <see cref="ApplyResultBanner"/> component.</returns>
	private IRenderedComponent<ApplyResultBanner> RenderBanner(
		ApplyOutcome?          outcome,
		IReadOnlyList<string>? errors = null)
	{
		return Render<ApplyResultBanner>(parameters => parameters
			.Add(component => component.Outcome, outcome)
			.Add(component => component.Errors, errors));
	}
}
