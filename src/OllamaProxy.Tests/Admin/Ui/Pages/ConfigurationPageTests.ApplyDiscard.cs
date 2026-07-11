// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using AngleSharp.Dom;

using Bunit;

using OllamaProxy.Admin.Config;
// The page type OllamaProxy.Admin.Ui.Pages.Configuration collides with the OllamaProxy.Configuration namespace,
// so it is aliased rather than imported via a plain using of OllamaProxy.Admin.Ui.Pages.
using ConfigurationPage = OllamaProxy.Admin.Ui.Pages.Configuration;

namespace OllamaProxy.Tests.Admin.Ui.Pages;

// Apply result banner: the outcome message the page renders after a sync attempt.
//
// Before any apply the banner is absent. A rejected apply surfaces the dry-run's field-level reasons so the
// operator can fix them and retry; a write failure surfaces its single message. A successful apply reloads the
// draft — returning the page to clean — while intentionally keeping the success banner visible so the operator sees
// that the commit completed.
//
// For the load lifecycle and shared harness see the anchor file and Helpers.
public sealed partial class ConfigurationPageTests
{
	// --- 4. Apply result banner: success, rejection, and write-failure outcomes ---

	/// <summary>
	/// Verifies no apply-result banner is rendered before the operator has attempted an apply, so a freshly loaded
	/// page shows no stale outcome.
	/// </summary>
	[Fact]
	public void Render_WhenNoApplyAttempted_HidesApplyResultBanner()
	{
		// Arrange
		FakeAdminModelService service = new() { StateFactory = static () => CreateDraft() };

		// Act
		(IRenderedComponent<ConfigurationPage> component, FakeAdminModelService _) = RenderConfiguration(service);

		// Assert
		Assert.Empty(component.FindAll(".apply-result"));
	}

	/// <summary>
	/// Verifies that a validation-rejected apply renders the dry-run's field-level reasons in the result banner, so
	/// the operator sees exactly which rules the draft violated.
	/// </summary>
	[Fact]
	public void ApplyAsync_WhenValidationRejected_ShowsRejectionReasons()
	{
		// Arrange
		FakeAdminModelService service = new()
		{
			StateFactory = static () => CreateDraft(listenUrl: "http://localhost:11434"),
			ApplyHandler = static _ =>
				ApplyResult.ValidationRejected(["Listen URL is invalid.", "Directory is required."])
		};
		(IRenderedComponent<ConfigurationPage> component, FakeAdminModelService _) = RenderConfiguration(service);
		component.Find("input.configuration-input").Change("http://0.0.0.0:11434");

		// Act
		component.Find("button.sync-bar-apply").Click();

		// Assert
		Assert.Equal(
			"The change was rejected and rolled back. The previous configuration is still live.",
			ApplyResultHeadline(component));
		Assert.Contains("apply-result-rejected", ApplyResultBanner(component).ClassList);
		IReadOnlyList<IElement> reasons = component.FindAll(".apply-result-errors li");
		Assert.Equal(2, reasons.Count);
		Assert.Equal("Listen URL is invalid.", reasons[0].TextContent);
		Assert.Equal("Directory is required.", reasons[1].TextContent);
	}

	/// <summary>
	/// Verifies that a write-failed apply renders its single failure message in the result banner, so the operator
	/// learns the change could not be persisted at all.
	/// </summary>
	[Fact]
	public void ApplyAsync_WhenWriteFailed_ShowsWriteFailureMessage()
	{
		// Arrange
		FakeAdminModelService service = new()
		{
			StateFactory = static () => CreateDraft(listenUrl: "http://localhost:11434"),
			ApplyHandler = static _ => ApplyResult.WriteFailed("The configuration file is read-only.")
		};
		(IRenderedComponent<ConfigurationPage> component, FakeAdminModelService _) = RenderConfiguration(service);
		component.Find("input.configuration-input").Change("http://0.0.0.0:11434");

		// Act
		component.Find("button.sync-bar-apply").Click();

		// Assert
		Assert.Equal(
			"The configuration could not be written. The previous configuration is still live.",
			ApplyResultHeadline(component));
		Assert.Contains("apply-result-failed", ApplyResultBanner(component).ClassList);
		IReadOnlyList<IElement> reasons = component.FindAll(".apply-result-errors li");
		Assert.Single(reasons);
		Assert.Equal("The configuration file is read-only.", reasons[0].TextContent);
	}

	/// <summary>
	/// Verifies that a successful apply keeps the success banner visible across the post-apply reload, so the
	/// operator gets confirmation even though the reloaded draft has returned to clean.
	/// </summary>
	[Fact]
	public void ApplyAsync_WhenApplySucceeds_ShowsSuccessBannerAfterReload()
	{
		// Arrange
		FakeAdminModelService service = new()
		{
			StateFactory = static () => CreateDraft(listenUrl: "http://localhost:11434"),
			ApplyHandler = static _ => ApplyResult.Applied
		};
		(IRenderedComponent<ConfigurationPage> component, FakeAdminModelService _) = RenderConfiguration(service);
		component.Find("input.configuration-input").Change("http://0.0.0.0:11434");

		// Act
		component.Find("button.sync-bar-apply").Click();

		// Assert
		Assert.Equal(
			"Changes applied. The proxy is now running the updated configuration.",
			ApplyResultHeadline(component));
		Assert.Contains("apply-result-applied", ApplyResultBanner(component).ClassList);
		Assert.Empty(component.FindAll(".apply-result-errors li"));
		Assert.Empty(component.FindAll(".backends-dirty-banner"));
	}

	/// <summary>
	/// Verifies that a rejected apply keeps the page dirty (the unsaved-changes banner stays), so the operator can
	/// correct the draft and retry without re-entering their edits.
	/// </summary>
	[Fact]
	public void ApplyAsync_WhenValidationRejected_KeepsDirtyBanner()
	{
		// Arrange
		FakeAdminModelService service = new()
		{
			StateFactory = static () => CreateDraft(listenUrl: "http://localhost:11434"),
			ApplyHandler = static _ => ApplyResult.ValidationRejected(["nope"])
		};
		(IRenderedComponent<ConfigurationPage> component, FakeAdminModelService _) = RenderConfiguration(service);
		component.Find("input.configuration-input").Change("http://0.0.0.0:11434");

		// Act
		component.Find("button.sync-bar-apply").Click();

		// Assert
		Assert.Single(component.FindAll(".backends-dirty-banner"));
	}
}
