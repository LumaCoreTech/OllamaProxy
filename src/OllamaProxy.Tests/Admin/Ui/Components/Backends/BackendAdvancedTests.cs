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

// Advanced disclosure: a collapsed panel of rarely-touched knobs that opens to reveal them.
//
// BackendAdvanced keeps the common editor uncluttered by hiding the seldom-changed settings behind a
// disclosure. These tests follow it from its two visible states through the body it reveals:
//
//   1. Disclosure gate: the header is always present; the body renders only when open, and the header's a11y
//      state (aria-expanded, aria-label, indicator glyph) plus its aria-controls linkage track the open/closed
//      state (RendersHeaderWithTitle / WhenExpanded* / WhenCollapsed*).
//
//   2. Body structure: the reasoning-effort option list, the four labelled probes, and the five probing knobs
//      with the domain bounds that constrain them (ReasoningEffortSelect / ProbingToggles / ProbingKnobs).
//
//   3. Value display: how a draft's values render into the controls, including the unset cases
//      (ContextLength / ModelPrefix / ReasoningEffort / ProbingToggles / ProbingKnobs).
//
//   4. Busy-state gate: two distinct locks. The page-global IsBusy gates the header toggle; the FieldsBusy lock
//      gates the twelve body controls. A globally busy page raises both (WhenBusy* / WhenNotBusy* /
//      WhenBusyAndCollapsed), while a per-backend probe raises only FieldsBusy — the fields freeze but the header
//      stays operable so the operator can still open the section (WhenFieldsBusyOnly*).
//
// For the callback wiring — the toggle, the dedicated model-prefix route, and the shared configuration-changed
// notifier — see Callbacks.

/// <summary>
/// Render tests for <see cref="BackendAdvanced"/>, the collapsible advanced-settings disclosure for one backend.
/// These tests assert <em>which</em> DOM the disclosure emits for a given draft and open/closed state — its
/// header a11y contract, the body's field structure and option lists, how the draft's values render into the
/// controls, and the busy-state gate — rather than any mapping logic, of which the component has none.
/// </summary>
/// <remarks>
/// The suite is split across partial files, each a chapter in the same story. Reading order:
/// <list type="number">
///     <item>
///         <description>
///         This anchor: the disclosure gate (header always present, body gated on
///         <see cref="BackendAdvanced.Expanded"/>, and the header's a11y state), the body's structure (the
///         reasoning-effort options, the four probes, and the five probing knobs with their domain bounds), how a
///         draft's values render into the controls, and the two busy locks — the header's
///         <see cref="BackendAdvanced.IsBusy"/> gate and the body's <see cref="BackendAdvanced.FieldsBusy"/> gate.
///         </description>
///     </item>
///     <item>
///         <description>
///         <c>Callbacks</c>: the event wiring — clicking the header raises
///         <see cref="BackendAdvanced.OnToggle"/>, the model-prefix field forwards its raw value to
///         <see cref="BackendAdvanced.OnModelPrefixChanged"/>, and the remaining fields raise the shared
///         <see cref="BackendAdvanced.OnConfigurationChanged"/>.
///         </description>
///     </item>
/// </list>
/// The shared render harness, backend fixture builder, and DOM accessors live in the <c>Helpers</c> partial.
/// </remarks>
[Trait("Category", "Unit")]
public sealed partial class BackendAdvancedTests : BunitContext
{
	// --- 1. Disclosure gate ---

	/// <summary>
	/// Verifies that the disclosure header — the toggle carrying the "Advanced" title — is rendered whether the
	/// panel is open or closed, since the header is the always-visible affordance that reveals the body.
	/// </summary>
	[Fact]
	public void Render_Always_RendersHeaderWithTitle()
	{
		// Act: render collapsed to prove the header exists independently of the body.
		IRenderedComponent<BackendAdvanced> cut = RenderAdvanced(expanded: false);

		// Assert
		Assert.Equal("Advanced", cut.Find("span.backend-advanced-title").TextContent);
	}

	/// <summary>
	/// Verifies that an open disclosure renders its body, so the operator sees the advanced fields when the panel
	/// is expanded.
	/// </summary>
	[Fact]
	public void Render_WhenExpanded_RendersBody()
	{
		// Act
		IRenderedComponent<BackendAdvanced> cut = RenderAdvanced(expanded: true);

		// Assert
		Assert.Single(cut.FindAll("div.backend-advanced-body"));
	}

	/// <summary>
	/// Verifies that an open disclosure sets its header's expanded a11y state — <c>aria-expanded="true"</c>, the
	/// "Hide advanced settings" label, and the down-pointing indicator — so assistive technology and sighted users
	/// both read the panel as open.
	/// </summary>
	[Fact]
	public void Render_WhenExpanded_SetsHeaderExpandedState()
	{
		// Act
		IRenderedComponent<BackendAdvanced> cut = RenderAdvanced(expanded: true);

		// Assert
		IElement header = Header(cut);
		Assert.Equal("true", header.GetAttribute("aria-expanded"));
		Assert.Equal("Hide advanced settings", header.GetAttribute("aria-label"));
		Assert.Equal("▾", cut.Find("span.backend-expand-indicator").TextContent);
	}

	/// <summary>
	/// Verifies that the header's <c>aria-controls</c> references the body's <c>id</c>, so assistive technology can
	/// associate the toggle with the region it reveals. The id is instance-generated, so the test asserts the
	/// linkage (and its stable prefix) rather than a fixed value.
	/// </summary>
	[Fact]
	public void Render_WhenExpanded_LinksHeaderToBodyViaAriaControls()
	{
		// Act
		IRenderedComponent<BackendAdvanced> cut = RenderAdvanced(expanded: true);

		// Assert: the header points at exactly the body's generated id.
		string bodyId = Body(cut).GetAttribute("id")!;
		Assert.StartsWith("backend-advanced-", bodyId);
		Assert.Equal(bodyId, Header(cut).GetAttribute("aria-controls"));
	}

	/// <summary>
	/// Verifies that a collapsed disclosure omits its body, keeping the advanced fields out of the DOM until the
	/// operator opens the panel.
	/// </summary>
	[Fact]
	public void Render_WhenCollapsed_OmitsBody()
	{
		// Act
		IRenderedComponent<BackendAdvanced> cut = RenderAdvanced(expanded: false);

		// Assert
		Assert.Empty(cut.FindAll("div.backend-advanced-body"));
	}

	/// <summary>
	/// Verifies that a collapsed disclosure sets its header's collapsed a11y state — <c>aria-expanded="false"</c>,
	/// the "Show advanced settings" label, and the right-pointing indicator — the observable counterpart to
	/// <see cref="Render_WhenExpanded_SetsHeaderExpandedState"/>.
	/// </summary>
	[Fact]
	public void Render_WhenCollapsed_SetsHeaderCollapsedState()
	{
		// Act
		IRenderedComponent<BackendAdvanced> cut = RenderAdvanced(expanded: false);

		// Assert
		IElement header = Header(cut);
		Assert.Equal("false", header.GetAttribute("aria-expanded"));
		Assert.Equal("Show advanced settings", header.GetAttribute("aria-label"));
		Assert.Equal("▸", cut.Find("span.backend-expand-indicator").TextContent);
	}

	// --- 2. Body structure ---

	/// <summary>
	/// Verifies that the reasoning-effort picker renders the leading "unspecified" default (empty value) followed
	/// by every <see cref="ReasoningEffort"/> level in ascending-budget order, with the enum name as the value and
	/// the display label as the visible text — so the operator can pick any level or clear the directive entirely.
	/// </summary>
	[Fact]
	public void Render_ReasoningEffortSelect_RendersUnspecifiedPlusEveryEffortInAscendingOrder()
	{
		// Act
		IRenderedComponent<BackendAdvanced> cut = RenderAdvanced();

		// Assert: value = enum name, text = display label, led by the empty-valued unspecified default.
		Assert.Equal(
			[
				(string.Empty, "Unspecified (no directive)"),
				(nameof(ReasoningEffort.None), "None"),
				(nameof(ReasoningEffort.Minimal), "Minimal"),
				(nameof(ReasoningEffort.Low), "Low"),
				(nameof(ReasoningEffort.Medium), "Medium"),
				(nameof(ReasoningEffort.High), "High"),
				(nameof(ReasoningEffort.XHigh), "Extra high"),
				(nameof(ReasoningEffort.Max), "Max")
			],
			OptionPairs(ReasoningEffortSelect(cut)));
	}

	/// <summary>
	/// Verifies that the capability-probing section renders exactly its four labelled toggles — Completion, Tools,
	/// Vision, Embeddings — in authored order, one checkbox per independently disableable probe.
	/// </summary>
	[Fact]
	public void Render_ProbingToggles_RendersFourLabelledProbes()
	{
		// Act
		IRenderedComponent<BackendAdvanced> cut = RenderAdvanced();

		// Assert
		Assert.Equal(
			["Completion", "Tools", "Vision", "Embeddings"],
			ProbingToggleLabels(cut));
	}

	/// <summary>
	/// Verifies that the probing knobs render with the <c>min</c> / <c>max</c> bounds taken from the
	/// <see cref="CapabilityProbingOptions"/> domain constants, so the number inputs constrain input to the same
	/// range the options validation enforces. Deriving the expected bounds from those constants means a bound wired
	/// to the wrong constant surfaces as a mismatch.
	/// </summary>
	[Fact]
	public void Render_ProbingKnobs_RendersFiveKnobsWithDomainBounds()
	{
		// Act
		IRenderedComponent<BackendAdvanced> cut = RenderAdvanced();

		// Assert: each knob's (label, min, max) matches the constant pair the markup binds.
		Assert.Equal(
			[
				("Timeout (s)",
				 Bound(CapabilityProbingOptions.MinimumTimeoutSeconds),
				 Bound(CapabilityProbingOptions.MaximumTimeoutSeconds)),
				("Interactive timeout (s)",
				 Bound(CapabilityProbingOptions.MinimumInteractiveTimeoutSeconds),
				 Bound(CapabilityProbingOptions.MaximumInteractiveTimeoutSeconds)),
				("Max retries",
				 Bound(CapabilityProbingOptions.MinimumMaxProbeRetries),
				 Bound(CapabilityProbingOptions.MaximumMaxProbeRetries)),
				("Retry base delay (s)",
				 Bound(CapabilityProbingOptions.MinimumRetryBaseDelaySeconds),
				 Bound(CapabilityProbingOptions.MaximumRetryBaseDelaySeconds)),
				("Max concurrent probes",
				 Bound(CapabilityProbingOptions.MinimumMaxConcurrentProbes),
				 Bound(CapabilityProbingOptions.MaximumMaxConcurrentProbes))
			],
			ProbingKnobBounds(cut));
	}

	// --- 3. Value display ---

	/// <summary>
	/// Verifies that the context-length input renders the draft's current value, so the operator edits the live
	/// fallback rather than a blank field.
	/// </summary>
	[Fact]
	public void Render_ContextLength_ReflectsBackendValue()
	{
		// Arrange
		DesiredBackend backend = CreateBackend(contextLength: 8192);

		// Act
		IRenderedComponent<BackendAdvanced> cut = RenderAdvanced(backend);

		// Assert
		Assert.Equal("8192", InputValue(ContextLengthInput(cut)));
	}

	/// <summary>
	/// Verifies that a backend with no fallback context length renders an empty input, so the field shows the
	/// "unset" state (and its placeholder) rather than a stray value.
	/// </summary>
	[Fact]
	public void Render_ContextLength_WhenUnset_RendersEmpty()
	{
		// Arrange: the default fixture leaves ContextLength null.
		IRenderedComponent<BackendAdvanced> cut = RenderAdvanced();

		// Assert
		Assert.Equal(string.Empty, InputValue(ContextLengthInput(cut)));
	}

	/// <summary>
	/// Verifies that the model-prefix input renders the draft's current value, so the operator edits the live
	/// exposure prefix.
	/// </summary>
	[Fact]
	public void Render_ModelPrefix_ReflectsBackendValue()
	{
		// Arrange
		DesiredBackend backend = CreateBackend(modelPrefix: "vllm");

		// Act
		IRenderedComponent<BackendAdvanced> cut = RenderAdvanced(backend);

		// Assert
		Assert.Equal("vllm", InputValue(ModelPrefixInput(cut)));
	}

	/// <summary>
	/// Verifies that the reasoning-effort select renders the draft's current selection, so switching a backend's
	/// default effort shows the persisted choice rather than the first option.
	/// </summary>
	[Fact]
	public void Render_ReasoningEffort_ReflectsBackendSelection()
	{
		// Arrange
		DesiredBackend backend = CreateBackend(reasoningEffort: ReasoningEffort.High);

		// Act
		IRenderedComponent<BackendAdvanced> cut = RenderAdvanced(backend);

		// Assert
		Assert.Equal(nameof(ReasoningEffort.High), ((IHtmlSelectElement)ReasoningEffortSelect(cut)).Value);
	}

	/// <summary>
	/// Verifies that a backend with no pinned reasoning effort marks no option as selected, so the picker pins no
	/// concrete level. This is the observable counterpart to
	/// <see cref="Render_ReasoningEffort_ReflectsBackendSelection"/>: a set effort renders a <c>selected</c> option,
	/// an unset one renders none.
	/// </summary>
	/// <remarks>
	/// Blazor renders the <c>selected</c> attribute only on the option whose value matches the bound value's string
	/// form. A <see langword="null"/> nullable enum formats to a null string that matches no option's value (not
	/// even the empty-valued unspecified default), so no option is marked selected. Asserting the absence of a
	/// <c>selected</c> option is therefore the accurate DOM contract.
	/// </remarks>
	[Fact]
	public void Render_ReasoningEffort_WhenUnset_MarksNoOptionSelected()
	{
		// Arrange: the default fixture leaves ReasoningEffort null.
		IRenderedComponent<BackendAdvanced> cut = RenderAdvanced();

		// Assert: no option carries the selected attribute, so the picker pins no concrete effort.
		Assert.Empty(ReasoningEffortSelect(cut).QuerySelectorAll("option[selected]"));
	}

	/// <summary>
	/// Verifies that each probing toggle renders its draft's checked state, so a backend with some probes disabled
	/// shows exactly which are on rather than a uniform default.
	/// </summary>
	[Fact]
	public void Render_ProbingToggles_ReflectBackendState()
	{
		// Arrange: a mixed pattern so a swapped or uniform toggle binding is caught.
		DesiredBackend backend = CreateBackend(
			probing: new CapabilityProbingOptions
			{
				ProbeCompletion = true,
				ProbeTools = false,
				ProbeVision = true,
				ProbeEmbeddings = false
			});

		// Act
		IRenderedComponent<BackendAdvanced> cut = RenderAdvanced(backend);

		// Assert
		Assert.Equal(
			[true, false, true, false],
			ProbingToggles(cut).Select(IsChecked).ToList());
	}

	/// <summary>
	/// Verifies that each probing knob renders its draft's value, so the operator edits the live probing settings.
	/// The five values are distinct and differ from the defaults so a swapped knob binding is caught.
	/// </summary>
	[Fact]
	public void Render_ProbingKnobs_ReflectBackendValues()
	{
		// Arrange
		DesiredBackend backend = CreateBackend(
			probing: new CapabilityProbingOptions
			{
				TimeoutSeconds = 15,
				InteractiveTimeoutSeconds = 90,
				MaxProbeRetries = 5,
				RetryBaseDelaySeconds = 8,
				MaxConcurrentProbes = 4
			});

		// Act
		IRenderedComponent<BackendAdvanced> cut = RenderAdvanced(backend);

		// Assert: knobs in authored order — timeout, interactive timeout, max retries, retry base delay, concurrency.
		Assert.Equal(
			["15", "90", "5", "8", "4"],
			ProbingKnobInputs(cut).Select(InputValue).ToList());
	}

	// --- 4. Busy-state gate ---

	/// <summary>
	/// Verifies that when the page is busy the header and every one of the twelve body controls carries the
	/// disabled attribute, so the whole disclosure locks during a load or apply. A globally busy page raises both
	/// locks (<see cref="BackendAdvanced.IsBusy"/> for the header, <see cref="BackendAdvanced.FieldsBusy"/> for the
	/// fields), which the harness models by defaulting <c>fieldsBusy</c> to <c>isBusy</c>.
	/// </summary>
	[Fact]
	public void Render_WhenBusy_DisablesHeaderAndEveryBodyControl()
	{
		// Act
		IRenderedComponent<BackendAdvanced> cut = RenderAdvanced(isBusy: true);

		// Assert
		Assert.True(Header(cut).HasAttribute("disabled"));

		IReadOnlyList<IElement> controls = BodyControls(cut);
		Assert.Equal(12, controls.Count);
		Assert.All(controls, control => Assert.True(control.HasAttribute("disabled")));
	}

	/// <summary>
	/// Verifies that when the page is not busy neither the header nor any body control is disabled, proving the busy
	/// branch is genuinely gated on <see cref="BackendAdvanced.IsBusy"/> rather than always applied.
	/// </summary>
	[Fact]
	public void Render_WhenNotBusy_EnablesHeaderAndEveryBodyControl()
	{
		// Act
		IRenderedComponent<BackendAdvanced> cut = RenderAdvanced(isBusy: false);

		// Assert
		Assert.False(Header(cut).HasAttribute("disabled"));

		IReadOnlyList<IElement> controls = BodyControls(cut);
		Assert.Equal(12, controls.Count);
		Assert.All(controls, control => Assert.False(control.HasAttribute("disabled")));
	}

	/// <summary>
	/// Verifies that a probe-only lock (<see cref="BackendAdvanced.FieldsBusy"/> set while
	/// <see cref="BackendAdvanced.IsBusy"/> is not) disables every one of the twelve body controls, so the settings
	/// that configure a running probe freeze while it streams. This is the probe half of the two-lock design.
	/// </summary>
	[Fact]
	public void Render_WhenFieldsBusyOnly_DisablesEveryBodyControl()
	{
		// Act: the page is not globally busy, but this backend is fetching/probing.
		IRenderedComponent<BackendAdvanced> cut = RenderAdvanced(isBusy: false, fieldsBusy: true);

		// Assert
		IReadOnlyList<IElement> controls = BodyControls(cut);
		Assert.Equal(12, controls.Count);
		Assert.All(controls, control => Assert.True(control.HasAttribute("disabled")));
	}

	/// <summary>
	/// Verifies that a probe-only lock leaves the header toggle <em>operable</em>: with
	/// <see cref="BackendAdvanced.FieldsBusy"/> set but <see cref="BackendAdvanced.IsBusy"/> clear, the operator can
	/// still expand or collapse the section to watch the probe fill in. This pins the two locks apart so a rewire
	/// that folds the header into the field lock is caught.
	/// </summary>
	[Fact]
	public void Render_WhenFieldsBusyOnly_LeavesHeaderOperable()
	{
		// Act
		IRenderedComponent<BackendAdvanced> cut = RenderAdvanced(isBusy: false, fieldsBusy: true);

		// Assert: the field lock does not reach the header — it gates on IsBusy alone.
		Assert.False(Header(cut).HasAttribute("disabled"));
	}

	/// <summary>
	/// Verifies that a busy page disables the header even while the disclosure is collapsed, so the operator cannot
	/// open the panel mid-operation. With the body absent, the header is the only control the busy gate can reach.
	/// </summary>
	[Fact]
	public void Render_WhenBusyAndCollapsed_DisablesHeader()
	{
		// Act
		IRenderedComponent<BackendAdvanced> cut = RenderAdvanced(expanded: false, isBusy: true);

		// Assert: the body is gone, and the still-present header is disabled.
		Assert.Empty(cut.FindAll("div.backend-advanced-body"));
		Assert.True(Header(cut).HasAttribute("disabled"));
	}
}
