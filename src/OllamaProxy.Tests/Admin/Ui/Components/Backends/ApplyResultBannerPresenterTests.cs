// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Admin.Config;
using OllamaProxy.Admin.Ui.Components.Backends;

namespace OllamaProxy.Tests.Admin.Ui.Components.Backends;

/// <summary>
/// Tests for <see cref="ApplyResultBannerPresenter"/>, the pure mapping behind the
/// <see cref="ApplyResultBanner"/>: the CSS modifier token that colors the banner and the operator-facing
/// headline, keyed by the terminal <see cref="ApplyOutcome"/>.
/// </summary>
/// <remarks>
/// The presenter answers two questions the banner would otherwise embed in markup: which
/// <c>.apply-result-*</c> modifier colors the banner (green applied, amber rejected, red failed) and what the
/// operator reads. These tests pin the modifier tokens as golden values (they are the contract with the scoped
/// stylesheet), assert each headline is non-blank, and confirm the two non-success outcomes both reassure the
/// operator that the previous configuration is still live.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class ApplyResultBannerPresenterTests
{
	/// <summary>
	/// Verifies that each apply outcome maps to the exact CSS modifier token the scoped stylesheet pairs with
	/// its <c>.apply-result-*</c> color rule.
	/// </summary>
	/// <param name="outcome">The apply outcome under test.</param>
	/// <param name="expected">The expected CSS modifier token.</param>
	[Theory]
	[InlineData(ApplyOutcome.Applied, "applied")]
	[InlineData(ApplyOutcome.ValidationRejected, "rejected")]
	[InlineData(ApplyOutcome.WriteFailed, "failed")]
	public void CssModifier_WhenOutcomeDefined_ReturnsToken(ApplyOutcome outcome, string expected)
	{
		// Act
		string result = ApplyResultBannerPresenter.CssModifier(outcome);

		// Assert
		Assert.Equal(expected, result);
	}

	/// <summary>
	/// Verifies that each apply outcome maps to a non-blank headline.
	/// </summary>
	/// <param name="outcome">The apply outcome under test.</param>
	[Theory]
	[InlineData(ApplyOutcome.Applied)]
	[InlineData(ApplyOutcome.ValidationRejected)]
	[InlineData(ApplyOutcome.WriteFailed)]
	public void Headline_WhenOutcomeDefined_ReturnsNonBlankHeadline(ApplyOutcome outcome)
	{
		// Act
		string result = ApplyResultBannerPresenter.Headline(outcome);

		// Assert
		Assert.False(string.IsNullOrWhiteSpace(result), $"Outcome '{outcome}' produced a blank headline.");
	}

	/// <summary>
	/// Verifies that the successful outcome's headline reports the new configuration is live, distinguishing it
	/// from the two non-success outcomes.
	/// </summary>
	[Fact]
	public void Headline_WhenApplied_ReportsUpdatedConfigurationIsLive()
	{
		// Act
		string result = ApplyResultBannerPresenter.Headline(ApplyOutcome.Applied);

		// Assert
		Assert.Contains("applied", result, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Verifies that both non-success outcomes reassure the operator that the previous configuration is still
	/// live, which is the safety message that distinguishes a rejection or write failure from a successful apply.
	/// </summary>
	/// <param name="outcome">The non-success outcome under test.</param>
	[Theory]
	[InlineData(ApplyOutcome.ValidationRejected)]
	[InlineData(ApplyOutcome.WriteFailed)]
	public void Headline_WhenNotApplied_ReportsPreviousConfigurationStillLive(ApplyOutcome outcome)
	{
		// Act
		string result = ApplyResultBannerPresenter.Headline(outcome);

		// Assert
		Assert.Contains("previous configuration is still live", result, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Verifies that the three outcomes map to three distinct headlines, so a copy edit cannot silently collapse
	/// two outcomes onto the same message.
	/// </summary>
	[Fact]
	public void Headline_ForAllOutcomes_AreDistinct()
	{
		// Arrange
		string applied = ApplyResultBannerPresenter.Headline(ApplyOutcome.Applied);
		string rejected = ApplyResultBannerPresenter.Headline(ApplyOutcome.ValidationRejected);
		string failed = ApplyResultBannerPresenter.Headline(ApplyOutcome.WriteFailed);

		// Assert
		Assert.Equal(3, new HashSet<string>([applied, rejected, failed]).Count);
	}
}
