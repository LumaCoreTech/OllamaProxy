// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using AngleSharp.Dom;

using Bunit;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using OllamaProxy.Admin.Config;
using OllamaProxy.Admin.Ui.Components.Backends;
using OllamaProxy.Hosting;

namespace OllamaProxy.Tests.Admin.Ui.Components.Backends;

/// <summary>
/// Tests for <see cref="ApplyBar"/>: the sync bar rendered at the bottom of the Backends page. The owning page
/// computes the structural problem and the busy/dirty flags, while ApplyBar owns the visible status text, the
/// API-key policy hint, the environment-variable hint, and the enabled/label state of the Apply and Discard
/// buttons.
/// </summary>
/// <remarks>
/// These tests verify that contract directly through the rendered component, so the structural validator is
/// covered not only as a pure function but also through the bar that blocks the operator. Each gate is checked
/// from <em>both</em> sides — error and clean, enabled and disabled — so a test cannot pass against a bar that
/// wired one branch permanently:
/// <list type="number">
///     <item>
///         <description>
///         Status text: a structural error renders in the dirty palette and replaces the clean baseline, while a
///         null error shows the clean baseline and no dirty message.
///         </description>
///     </item>
///     <item>
///         <description>
///         Apply gate: Apply is enabled only when there is no structural error, the page is not busy, and CanApply
///         is set; each of those three conditions is shown to disable it independently.
///         </description>
///     </item>
///     <item>
///         <description>
///         Discard gate: Discard is governed by CanDiscard and busy state only, independent of the structural
///         error, so an invalid edit can still be discarded.
///         </description>
///     </item>
///     <item>
///         <description>
///         Labels: the Apply and Discard captions switch to their in-progress wording while applying/discarding.
///         </description>
///     </item>
///     <item>
///         <description>
///         Policy hint and environment hint: the API-key policy line reflects the configured policy, and the
///         environment-variable hint appears only for the EnvironmentOnly policy when ShowEnvironmentKeyHint is set.
///         </description>
///     </item>
///     <item>
///         <description>
///         Callbacks: clicking Apply and Discard invokes the respective EventCallback the page supplies.
///         </description>
///     </item>
/// </list>
/// </remarks>
[Trait("Category", "Unit")]
public sealed class ApplyBarTests : BunitContext
{
	/// <summary>
	/// A representative structural error message, standing in for any non-null gate reason the page computes.
	/// </summary>
	private const string SampleStructuralError = "Every backend needs a non-blank name.";

	/// <summary>
	/// Verifies that <see cref="ApplyBar"/> renders a supplied structural error as the dirty status message and
	/// removes the clean baseline text.
	/// </summary>
	[Fact]
	public void Render_WhenStructuralErrorProvided_RendersDirtyStatusMessage()
	{
		// Arrange
		RegisterAdminOptions();

		// Act
		IRenderedComponent<ApplyBar> cut = Render<ApplyBar>(parameters => parameters
			.Add(bar => bar.StructuralError, SampleStructuralError)
			.Add(bar => bar.CanApply, true)
			.Add(bar => bar.CanDiscard, true));

		// Assert
		IElement status = cut.Find("span.sync-bar-dirty");
		Assert.Equal(SampleStructuralError, status.TextContent);
		Assert.Empty(cut.FindAll("span.sync-bar-clean"));
	}

	/// <summary>
	/// Verifies that <see cref="ApplyBar"/> shows the clean baseline status and no dirty message when there is no
	/// structural error, proving the dirty branch is genuinely gated on the error rather than always rendered.
	/// </summary>
	[Fact]
	public void Render_WhenNoStructuralError_RendersCleanBaselineStatus()
	{
		// Arrange
		RegisterAdminOptions();

		// Act
		IRenderedComponent<ApplyBar> cut = Render<ApplyBar>(parameters => parameters
			.Add(bar => bar.StructuralError, null)
			.Add(bar => bar.CanApply, true)
			.Add(bar => bar.CanDiscard, true));

		// Assert
		IElement status = cut.Find("span.sync-bar-clean");
		Assert.Equal("Apply writes the whole configuration and recycles the proxy.", status.TextContent);
		Assert.Empty(cut.FindAll("span.sync-bar-dirty"));
	}

	/// <summary>
	/// Verifies that <see cref="ApplyBar"/> disables Apply while a structural error is present, even when the owning
	/// page reports that there are changes to apply.
	/// </summary>
	[Fact]
	public void Render_WhenStructuralErrorProvided_DisablesApplyButton()
	{
		// Arrange
		RegisterAdminOptions();

		// Act
		IRenderedComponent<ApplyBar> cut = Render<ApplyBar>(parameters => parameters
			.Add(bar => bar.StructuralError, "Backend names must be unique. Duplicated: cloud.")
			.Add(bar => bar.CanApply, true)
			.Add(bar => bar.CanDiscard, true));

		// Assert
		IElement apply = cut.Find("button.sync-bar-apply");
		Assert.Equal("Apply", apply.TextContent.Trim());
		Assert.True(apply.HasAttribute("disabled"));
	}

	/// <summary>
	/// Verifies that <see cref="ApplyBar"/> enables Apply when the draft is structurally sound, not busy, and the
	/// page reports there are changes to apply — the counterpart proving the disabled cases are real gates.
	/// </summary>
	[Fact]
	public void Render_WhenNoErrorAndCanApply_EnablesApplyButton()
	{
		// Arrange
		RegisterAdminOptions();

		// Act
		IRenderedComponent<ApplyBar> cut = Render<ApplyBar>(parameters => parameters
			.Add(bar => bar.StructuralError, null)
			.Add(bar => bar.CanApply, true)
			.Add(bar => bar.CanDiscard, true));

		// Assert
		IElement apply = cut.Find("button.sync-bar-apply");
		Assert.False(apply.HasAttribute("disabled"));
	}

	/// <summary>
	/// Verifies that <see cref="ApplyBar"/> disables Apply when there is nothing to apply, even with a structurally
	/// sound draft, so the button tracks <see cref="ApplyBar.CanApply"/>.
	/// </summary>
	[Fact]
	public void Render_WhenNoErrorButCannotApply_DisablesApplyButton()
	{
		// Arrange
		RegisterAdminOptions();

		// Act
		IRenderedComponent<ApplyBar> cut = Render<ApplyBar>(parameters => parameters
			.Add(bar => bar.StructuralError, null)
			.Add(bar => bar.CanApply, false)
			.Add(bar => bar.CanDiscard, true));

		// Assert
		IElement apply = cut.Find("button.sync-bar-apply");
		Assert.True(apply.HasAttribute("disabled"));
	}

	/// <summary>
	/// Verifies that <see cref="ApplyBar"/> disables both action buttons while the page is busy, regardless of the
	/// structural error or the CanApply/CanDiscard flags, so no second operation can start mid-flight.
	/// </summary>
	[Fact]
	public void Render_WhenBusy_DisablesBothButtons()
	{
		// Arrange
		RegisterAdminOptions();

		// Act
		IRenderedComponent<ApplyBar> cut = Render<ApplyBar>(parameters => parameters
			.Add(bar => bar.StructuralError, null)
			.Add(bar => bar.IsBusy, true)
			.Add(bar => bar.CanApply, true)
			.Add(bar => bar.CanDiscard, true));

		// Assert
		Assert.True(cut.Find("button.sync-bar-apply").HasAttribute("disabled"));
		Assert.True(cut.Find("button.sync-bar-discard").HasAttribute("disabled"));
	}

	/// <summary>
	/// Verifies that <see cref="ApplyBar"/> keeps Discard controlled by <see cref="ApplyBar.CanDiscard"/> rather than
	/// by the structural error, so an invalid edit can still be discarded.
	/// </summary>
	[Fact]
	public void Render_WhenStructuralErrorProvided_LeavesDiscardControlledByCanDiscard()
	{
		// Arrange
		RegisterAdminOptions();

		// Act
		IRenderedComponent<ApplyBar> cut = Render<ApplyBar>(parameters => parameters
			.Add(bar => bar.StructuralError, SampleStructuralError)
			.Add(bar => bar.CanApply, true)
			.Add(bar => bar.CanDiscard, true));

		// Assert
		IElement discard = cut.Find("button.sync-bar-discard");
		Assert.Equal("Discard changes", discard.TextContent.Trim());
		Assert.False(discard.HasAttribute("disabled"));
	}

	/// <summary>
	/// Verifies that <see cref="ApplyBar"/> disables Discard when <see cref="ApplyBar.CanDiscard"/> is false, so a
	/// clean draft offers nothing to discard.
	/// </summary>
	[Fact]
	public void Render_WhenCannotDiscard_DisablesDiscardButton()
	{
		// Arrange
		RegisterAdminOptions();

		// Act
		IRenderedComponent<ApplyBar> cut = Render<ApplyBar>(parameters => parameters
			.Add(bar => bar.StructuralError, null)
			.Add(bar => bar.CanApply, true)
			.Add(bar => bar.CanDiscard, false));

		// Assert
		IElement discard = cut.Find("button.sync-bar-discard");
		Assert.True(discard.HasAttribute("disabled"));
	}

	/// <summary>
	/// Verifies that <see cref="ApplyBar"/> switches the Apply and Discard captions to their in-progress wording
	/// while an apply or discard is running.
	/// </summary>
	[Fact]
	public void Render_WhenApplyingAndDiscarding_ShowsInProgressLabels()
	{
		// Arrange
		RegisterAdminOptions();

		// Act
		IRenderedComponent<ApplyBar> cut = Render<ApplyBar>(parameters => parameters
			.Add(bar => bar.StructuralError, null)
			.Add(bar => bar.IsApplying, true)
			.Add(bar => bar.IsDiscarding, true)
			.Add(bar => bar.CanApply, true)
			.Add(bar => bar.CanDiscard, true));

		// Assert
		Assert.Equal("Applying…", cut.Find("button.sync-bar-apply").TextContent.Trim());
		Assert.Equal("Discarding…", cut.Find("button.sync-bar-discard").TextContent.Trim());
	}

	/// <summary>
	/// Verifies that <see cref="ApplyBar"/> describes each API-key persistence policy with the matching status
	/// line, so the operator can read the deployment's secret handling from the bar.
	/// </summary>
	/// <param name="policy">The configured persistence policy.</param>
	/// <param name="expectedText">The policy status text the bar must show.</param>
	[Theory]
	[InlineData(ApiKeyPersistencePolicy.WriteToFile, "Saved in configuration file")]
	[InlineData(ApiKeyPersistencePolicy.EnvironmentOnly, "Environment variables only")]
	public void Render_WithApiKeyPolicy_ShowsMatchingPolicyText(
		ApiKeyPersistencePolicy policy,
		string                  expectedText)
	{
		// Arrange
		RegisterAdminOptions(policy);

		// Act
		IRenderedComponent<ApplyBar> cut = Render<ApplyBar>(parameters => parameters
			.Add(bar => bar.StructuralError, null)
			.Add(bar => bar.CanApply, true)
			.Add(bar => bar.CanDiscard, true));

		// Assert
		IElement policyValue = cut.Find("span.sync-bar-policy-value");
		Assert.Equal(expectedText, policyValue.TextContent.Trim());
	}

	/// <summary>
	/// Verifies that <see cref="ApplyBar"/> shows the environment-variable hint only when the deployment uses the
	/// EnvironmentOnly policy and the hint is enabled, covering every combination of the two conditions.
	/// </summary>
	/// <param name="policy">The configured persistence policy.</param>
	/// <param name="showHint">The value of <see cref="ApplyBar.ShowEnvironmentKeyHint"/>.</param>
	/// <param name="expectHint">Whether the hint paragraph is expected to render.</param>
	[Theory]
	[InlineData(ApiKeyPersistencePolicy.EnvironmentOnly, true, true)]
	[InlineData(ApiKeyPersistencePolicy.EnvironmentOnly, false, false)]
	[InlineData(ApiKeyPersistencePolicy.WriteToFile, true, false)]
	[InlineData(ApiKeyPersistencePolicy.WriteToFile, false, false)]
	public void Render_EnvironmentKeyHint_AppearsOnlyForEnvironmentOnlyPolicyWhenEnabled(
		ApiKeyPersistencePolicy policy,
		bool                    showHint,
		bool                    expectHint)
	{
		// Arrange
		RegisterAdminOptions(policy);

		// Act
		IRenderedComponent<ApplyBar> cut = Render<ApplyBar>(parameters => parameters
			.Add(bar => bar.StructuralError, null)
			.Add(bar => bar.CanApply, true)
			.Add(bar => bar.CanDiscard, true)
			.Add(bar => bar.ShowEnvironmentKeyHint, showHint));

		// Assert
		Assert.Equal(expectHint ? 1 : 0, cut.FindAll("p.sync-bar-hint").Count);
	}

	/// <summary>
	/// Verifies that clicking Apply raises <see cref="ApplyBar.OnApply"/> so the owning page can run the commit.
	/// </summary>
	[Fact]
	public void Click_Apply_InvokesOnApplyCallback()
	{
		// Arrange
		RegisterAdminOptions();
		int applyInvocations = 0;

		IRenderedComponent<ApplyBar> cut = Render<ApplyBar>(parameters => parameters
			.Add(bar => bar.StructuralError, null)
			.Add(bar => bar.CanApply, true)
			.Add(bar => bar.CanDiscard, true)
			.Add(bar => bar.OnApply, () => applyInvocations++));

		// Act
		cut.Find("button.sync-bar-apply").Click();

		// Assert
		Assert.Equal(1, applyInvocations);
	}

	/// <summary>
	/// Verifies that clicking Discard raises <see cref="ApplyBar.OnDiscard"/> so the owning page can reload the draft.
	/// </summary>
	[Fact]
	public void Click_Discard_InvokesOnDiscardCallback()
	{
		// Arrange
		RegisterAdminOptions();
		int discardInvocations = 0;

		IRenderedComponent<ApplyBar> cut = Render<ApplyBar>(parameters => parameters
			.Add(bar => bar.StructuralError, null)
			.Add(bar => bar.CanApply, true)
			.Add(bar => bar.CanDiscard, true)
			.Add(bar => bar.OnDiscard, () => discardInvocations++));

		// Act
		cut.Find("button.sync-bar-discard").Click();

		// Assert
		Assert.Equal(1, discardInvocations);
	}

	/// <summary>
	/// Registers the admin options service required by <see cref="ApplyBar"/> during bUnit rendering.
	/// </summary>
	/// <param name="policy">The API-key persistence policy the bar should read from options.</param>
	private void RegisterAdminOptions(ApiKeyPersistencePolicy policy = ApiKeyPersistencePolicy.WriteToFile)
	{
		Services.AddSingleton<IOptions<AdminOptions>>(
			Options.Create(
				new AdminOptions
				{
					ApiKeyPersistencePolicy = policy
				}));
	}
}
