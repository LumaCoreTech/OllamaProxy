// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Bunit;
// The page type OllamaProxy.Admin.Ui.Pages.Configuration collides with the OllamaProxy.Configuration namespace,
// so it is aliased rather than imported via a plain using of OllamaProxy.Admin.Ui.Pages.
using ConfigurationPage = OllamaProxy.Admin.Ui.Pages.Configuration;

namespace OllamaProxy.Tests.Admin.Ui.Pages;

// Validation gate and conditional fields: what blocks Apply and what the tracing toggle reveals.
//
// The listener URL carries [Required]/[Url] data annotations. Entering an invalid URL trips the
// DataAnnotationsValidator, which the page surfaces as a structural error in the sync bar and uses to keep Apply
// disabled even though the draft is dirty — a broken configuration must never be committed. Separately, the
// request-tracing checkbox gates its dependent fields (directory, max files): they are only rendered once tracing
// is enabled.
//
// For the shared harness see the anchor file and Helpers.
public sealed partial class ConfigurationPageTests
{
	// --- 5. Validation gate and conditional tracing fields ---

	/// <summary>
	/// Verifies that entering an invalid listener URL surfaces the structural error in the sync bar, telling the
	/// operator to fix the validation problem before the change can be applied.
	/// </summary>
	[Fact]
	public void EditListenUrl_WhenInvalid_ShowsStructuralError()
	{
		// Arrange
		FakeAdminModelService service = new()
		{
			StateFactory = static () => CreateDraft(listenUrl: "http://localhost:11434")
		};
		(IRenderedComponent<ConfigurationPage> component, FakeAdminModelService _) = RenderConfiguration(service);

		// Act
		component.Find("input.configuration-input").Change("not-a-url");

		// Assert
		Assert.Equal("Fix validation errors before applying.", component.Find(".sync-bar-dirty").TextContent);
	}

	/// <summary>
	/// Verifies that an invalid listener URL keeps Apply disabled even though the edit made the draft dirty, so a
	/// broken configuration can never be committed.
	/// </summary>
	[Fact]
	public void EditListenUrl_WhenInvalid_DisablesApply()
	{
		// Arrange
		FakeAdminModelService service = new()
		{
			StateFactory = static () => CreateDraft(listenUrl: "http://localhost:11434")
		};
		(IRenderedComponent<ConfigurationPage> component, FakeAdminModelService _) = RenderConfiguration(service);

		// Act
		component.Find("input.configuration-input").Change("not-a-url");

		// Assert
		Assert.True(component.Find("button.sync-bar-apply").HasAttribute("disabled"));
	}

	/// <summary>
	/// Verifies that enabling request tracing reveals its dependent fields (the output directory and max-files
	/// inputs), so the operator can configure them only once tracing is switched on.
	/// </summary>
	[Fact]
	public void ToggleTracing_WhenEnabled_RevealsDependentFields()
	{
		// Arrange
		FakeAdminModelService service = new()
		{
			StateFactory = static () => CreateDraft(tracingEnabled: false)
		};
		(IRenderedComponent<ConfigurationPage> component, FakeAdminModelService _) = RenderConfiguration(service);

		// Act
		component.Find("input[type=checkbox]").Change(true);

		// Assert
		Assert.Equal(
			[
				"Listen URL",
				"Enable request tracing",
				"Output directory",
				"Maximum trace files",
				"Redact inline attachments"
			],
			ConfigurationLabels(component));
		// Listener URL plus the newly revealed directory and max-files inputs.
		Assert.Equal(3, component.FindAll("input.configuration-input").Count);
		// Enable tracing plus the newly revealed redact-attachments toggle.
		Assert.Equal(2, component.FindAll("input[type=checkbox]").Count);
	}
}
