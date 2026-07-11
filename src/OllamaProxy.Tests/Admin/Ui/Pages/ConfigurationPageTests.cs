// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Bunit;

using OllamaProxy.Admin;
// The page type OllamaProxy.Admin.Ui.Pages.Configuration collides with the OllamaProxy.Configuration namespace,
// so it is aliased rather than imported via a plain using of OllamaProxy.Admin.Ui.Pages.
using ConfigurationPage = OllamaProxy.Admin.Ui.Pages.Configuration;

namespace OllamaProxy.Tests.Admin.Ui.Pages;

/// <summary>
/// Page-level bUnit workflow tests for <see cref="ConfigurationPage"/>, the general-configuration editor (listener
/// URL and request tracing).
/// </summary>
/// <remarks>
/// These tests follow the page as an operator would drive it: from the initial load, through the rendered form,
/// into the dirty/apply/discard commit cycle, and the validation gate that blocks a broken configuration. They
/// assert which DOM branch renders and how the page orchestrates <see cref="IAdminModelService"/>, rather than
/// duplicating the render tests for the shared <c>ApplyBar</c>, <c>DirtyBanner</c>, and <c>ApplyResultBanner</c>
/// components.
/// <para>Reading order:</para>
/// <list type="number">
///     <item>
///         <description>
///         This anchor: load lifecycle — the loading placeholder, the initial service load, and the rendered form.
///         </description>
///     </item>
///     <item>
///         <description>
///         <c>CommitCycle</c>: dirty gating, apply forwarding, success reload, rejected-apply retention, and discard.
///         </description>
///     </item>
///     <item>
///         <description>
///         <c>DirtyState</c>: the visible unsaved-changes banner from clean load through edit and discard.
///         </description>
///     </item>
///     <item>
///         <description>
///         <c>ApplyDiscard</c>: the result banner after success, validation rejection, and write failure.
///         </description>
///     </item>
///     <item>
///         <description>
///         <c>Validation</c>: the data-annotation gate — an invalid listener URL blocks Apply; the tracing toggle
///         reveals its dependent fields.
///         </description>
///     </item>
/// </list>
/// The shared harness, fake admin service, and DOM assertion helpers live in <c>Helpers</c>.
/// </remarks>
[Trait("Category", "Unit")]
public sealed partial class ConfigurationPageTests : BunitContext
{
	// --- 1. Load lifecycle: initial load and form rendering ---

	/// <summary>
	/// Verifies the page loads the editable draft from the admin service exactly once during its initial render, so
	/// the operator sees the live configuration without an explicit refresh.
	/// </summary>
	[Fact]
	public void OnInitializedAsync_WhenPageRenders_LoadsConfigurationOnce()
	{
		// Arrange
		FakeAdminModelService service = new() { StateFactory = static () => CreateDraft() };

		// Act
		(IRenderedComponent<ConfigurationPage> component, FakeAdminModelService _) = RenderConfiguration(service);

		// Assert
		Assert.Equal(1, service.GetEditableStateCallCount);
		Assert.NotNull(component.Instance);
	}

	/// <summary>
	/// Verifies the page renders the editable form once the draft has loaded, showing the loaded listener URL, so
	/// the operator edits against the live configuration rather than a blank or placeholder form.
	/// </summary>
	[Fact]
	public void Render_WhenDraftLoaded_ShowsFormWithLoadedListenUrl()
	{
		// Arrange
		FakeAdminModelService service = new()
		{
			StateFactory = static () => CreateDraft(listenUrl: "http://0.0.0.0:11434")
		};

		// Act
		(IRenderedComponent<ConfigurationPage> component, FakeAdminModelService _) = RenderConfiguration(service);

		// Assert
		Assert.Single(component.FindAll("form.configuration-form"));
		Assert.Equal("http://0.0.0.0:11434", ListenUrlValue(component));
	}

	/// <summary>
	/// Verifies the request-tracing dependent fields are hidden when tracing is disabled in the loaded draft, so the
	/// form only offers the directory and file-count inputs once the operator opts in.
	/// </summary>
	[Fact]
	public void Render_WhenTracingDisabled_HidesDependentTracingFields()
	{
		// Arrange
		FakeAdminModelService service = new()
		{
			StateFactory = static () => CreateDraft(tracingEnabled: false)
		};

		// Act
		(IRenderedComponent<ConfigurationPage> component, FakeAdminModelService _) = RenderConfiguration(service);

		// Assert
		Assert.Equal(["Listen URL", "Enable request tracing"], ConfigurationLabels(component));
		// Only the listener-URL text input is present; tracing's directory and max-files inputs stay hidden while off.
		Assert.Single(component.FindAll("input.configuration-input"));
		Assert.Single(component.FindAll("input[type=checkbox]"));
	}
}
