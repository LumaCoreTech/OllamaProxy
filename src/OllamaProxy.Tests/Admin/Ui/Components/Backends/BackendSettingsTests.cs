// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using AngleSharp.Dom;
using AngleSharp.Html.Dom;

using Bunit;

using OllamaProxy.Admin.Editing;
using OllamaProxy.Admin.Ui.Components.Backends;
using OllamaProxy.Configuration;

namespace OllamaProxy.Tests.Admin.Ui.Components.Backends;

/// <summary>
/// Render tests for <see cref="BackendSettings"/>, the common-case connection form for one backend. The pure
/// API-key placeholder mapping it delegates to is covered by <see cref="BackendSettingsPresenterTests"/>; these
/// tests instead assert <em>which</em> DOM the form emits for a given draft — its field structure, the provider
/// and mode option lists, how the draft's values are reflected into the controls, the busy-state gate, and the
/// callback wiring.
/// </summary>
/// <remarks>
/// The suite is split across partial files, each a chapter in the same story. Reading order:
/// <list type="number">
///     <item>
///         <description>
///         This anchor: the field structure (five labelled fields), the provider and mode option lists, how a
///         draft's values render into the controls (including the presenter-driven API-key placeholder), and the
///         <see cref="BackendSettings.IsBusy"/> gate that disables the whole editor.
///         </description>
///     </item>
///     <item>
///         <description>
///         <c>Callbacks</c>: the event wiring — editing Name, Base URL, and API key raises
///         <see cref="BackendSettings.OnConfigurationChanged"/>, while the Provider and Mode pickers raise their
///         dedicated <see cref="BackendSettings.OnProviderTypeChanged"/> and
///         <see cref="BackendSettings.OnModeChanged"/> callbacks.
///         </description>
///     </item>
/// </list>
/// The shared render harness, backend fixture builder, and DOM accessors live in the <c>Helpers</c> partial.
/// </remarks>
[Trait("Category", "Unit")]
public sealed partial class BackendSettingsTests : BunitContext
{
	// --- 1. Field structure ---

	/// <summary>
	/// Verifies that the form renders its five fields — Name, Provider, Base URL, API key, Mode — each with its
	/// label, in the authored order, so the operator sees the whole common-case editor.
	/// </summary>
	[Fact]
	public void Render_Always_RendersFiveLabelledFieldsInOrder()
	{
		// Act
		IRenderedComponent<BackendSettings> cut = RenderSettings();

		// Assert
		Assert.Equal(
			["Name", "Provider", "Base URL", "API key", "Mode"],
			FieldLabels(cut));
	}

	// --- 2. Option lists ---

	/// <summary>
	/// Verifies that the Provider picker renders one option per supplied descriptor, in catalog order, with the
	/// provider-type discriminator as the value and the display name as the visible text — proving the picker
	/// renders whatever catalog the page supplies rather than a hard-coded list.
	/// </summary>
	[Fact]
	public void Render_ProviderSelect_RendersOneOptionPerDescriptorInCatalogOrder()
	{
		// Act
		IRenderedComponent<BackendSettings> cut = RenderSettings();

		// Assert: value = ProviderType, text = DisplayName, in the exact order the catalog published.
		Assert.Equal(
			[("openai", "OpenAI"), ("vllm", "vLLM"), ("venice", "Venice")],
			OptionPairs(ProviderSelect(cut)));
	}

	/// <summary>
	/// Verifies that the Mode picker renders the four fixed operating-mode choices — the provider-based default
	/// (empty value) plus the three explicit modes — since the mode set is intrinsic to the domain and not
	/// supplied by the page.
	/// </summary>
	[Fact]
	public void Render_ModeSelect_RendersProviderDefaultPlusThreeExplicitModes()
	{
		// Act
		IRenderedComponent<BackendSettings> cut = RenderSettings();

		// Assert: the empty-valued default plus one option per OperatingMode, in declaration order.
		Assert.Equal(
			[
				(string.Empty, "Default (provider-based)"),
				(nameof(OperatingMode.PlugAndPlay), "Plug-and-play"),
				(nameof(OperatingMode.Hybrid), "Hybrid"),
				(nameof(OperatingMode.Explicit), "Explicit")
			],
			OptionPairs(ModeSelect(cut)));
	}

	// --- 3. Value display ---

	/// <summary>
	/// Verifies that the Name and Base URL inputs render the draft's current values, so the operator edits the
	/// live draft rather than a blank form.
	/// </summary>
	[Fact]
	public void Render_TextInputs_ReflectBackendValues()
	{
		// Arrange
		DesiredBackend backend = CreateBackend(name: "my-openai", baseUrl: "https://example.test/v1");

		// Act
		IRenderedComponent<BackendSettings> cut = RenderSettings(backend);

		// Assert
		Assert.Equal("my-openai", cut.Find("input[type=text]").GetAttribute("value"));
		Assert.Equal("https://example.test/v1", cut.Find("input[type=url]").GetAttribute("value"));
	}

	/// <summary>
	/// Verifies that the Provider and Mode selects render the draft's current selection, so switching a backend's
	/// provider or mode shows the persisted choice rather than the first option.
	/// </summary>
	[Fact]
	public void Render_Selects_ReflectBackendSelection()
	{
		// Arrange
		DesiredBackend backend = CreateBackend(providerType: "venice", mode: OperatingMode.Hybrid);

		// Act
		IRenderedComponent<BackendSettings> cut = RenderSettings(backend);

		// Assert
		Assert.Equal("venice", ((IHtmlSelectElement)ProviderSelect(cut)).Value);
		Assert.Equal(nameof(OperatingMode.Hybrid), ((IHtmlSelectElement)ModeSelect(cut)).Value);
	}

	/// <summary>
	/// Verifies that a backend with no pinned mode marks no option as selected, so the form pins no concrete mode
	/// and the browser falls back to displaying the first option — the empty-valued "Default (provider-based)"
	/// entry. This is the observable counterpart to <see cref="Render_Selects_ReflectBackendSelection"/>: a set
	/// mode renders a <c>selected</c> option, an unset mode renders none.
	/// </summary>
	/// <remarks>
	/// Blazor renders the <c>selected</c> attribute only on the option whose value matches the bound value's
	/// string form. A <see langword="null"/> nullable enum formats to a null string that matches no option's
	/// value (not even the empty-valued default), so no option is marked selected. Asserting the absence of a
	/// <c>selected</c> option is therefore the accurate DOM contract; asserting a selected value would encode a
	/// browser fallback that the rendered markup does not carry.
	/// </remarks>
	[Fact]
	public void Render_ModeSelect_WhenModeUnset_MarksNoOptionSelected()
	{
		// Arrange
		DesiredBackend backend = CreateBackend(mode: null);

		// Act
		IRenderedComponent<BackendSettings> cut = RenderSettings(backend);

		// Assert: no option carries the selected attribute, so the form pins no concrete mode.
		Assert.Empty(ModeSelect(cut).QuerySelectorAll("option[selected]"));
	}

	/// <summary>
	/// Verifies that a newly added backend — one whose <see cref="DesiredBackend.OriginalName"/> is
	/// <see langword="null"/> — shows the "Required" API-key placeholder, matching the presenter's new-backend
	/// contract.
	/// </summary>
	[Fact]
	public void Render_ApiKeyPlaceholder_WhenBackendIsNew_ShowsRequired()
	{
		// Arrange: a new backend carries no OriginalName.
		DesiredBackend backend = CreateBackend(originalName: null);

		// Act
		IRenderedComponent<BackendSettings> cut = RenderSettings(backend);

		// Assert
		Assert.Equal("Required", cut.Find("input[type=password]").GetAttribute("placeholder"));
	}

	/// <summary>
	/// Verifies that an existing backend shows the saved-secret API-key placeholder, matching the presenter's
	/// existing-backend contract so the operator knows the field may be left blank to keep the stored key.
	/// </summary>
	[Fact]
	public void Render_ApiKeyPlaceholder_WhenBackendExists_ShowsSavedSecretHint()
	{
		// Arrange: the default fixture is an existing backend (OriginalName set).
		IRenderedComponent<BackendSettings> cut = RenderSettings();

		// Assert
		Assert.Equal(
			"•••• saved — leave blank to keep",
			cut.Find("input[type=password]").GetAttribute("placeholder"));
	}

	// --- 4. Busy-state gate ---

	/// <summary>
	/// Verifies that when the page is busy every interactive control — both selects and all three inputs — is
	/// disabled, so the editor locks as a whole during a load or apply.
	/// </summary>
	[Fact]
	public void Render_WhenBusy_DisablesEveryControl()
	{
		// Act
		IRenderedComponent<BackendSettings> cut = RenderSettings(isBusy: true);

		// Assert: all five controls carry the disabled attribute.
		IReadOnlyList<IElement> controls = InteractiveControls(cut);
		Assert.Equal(5, controls.Count);
		Assert.All(controls, control => Assert.True(control.HasAttribute("disabled")));
	}

	/// <summary>
	/// Verifies that when the page is not busy no control is disabled, proving the busy branch is genuinely gated
	/// on <see cref="BackendSettings.IsBusy"/> rather than always applied.
	/// </summary>
	[Fact]
	public void Render_WhenNotBusy_EnablesEveryControl()
	{
		// Act
		IRenderedComponent<BackendSettings> cut = RenderSettings(isBusy: false);

		// Assert: no control carries the disabled attribute.
		IReadOnlyList<IElement> controls = InteractiveControls(cut);
		Assert.Equal(5, controls.Count);
		Assert.All(controls, control => Assert.False(control.HasAttribute("disabled")));
	}
}
