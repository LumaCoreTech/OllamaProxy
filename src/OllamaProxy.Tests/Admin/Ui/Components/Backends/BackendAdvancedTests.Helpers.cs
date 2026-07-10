// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Globalization;

using AngleSharp.Dom;
using AngleSharp.Html.Dom;

using Bunit;

using OllamaProxy.Admin.Editing;
using OllamaProxy.Admin.Ui.Components.Backends;
using OllamaProxy.Configuration;

namespace OllamaProxy.Tests.Admin.Ui.Components.Backends;

public sealed partial class BackendAdvancedTests
{
	/// <summary>
	/// Renders <see cref="BackendAdvanced"/> with sensible defaults for every required parameter, so a test only
	/// supplies the inputs relevant to the branch it exercises. The disclosure defaults to <em>open</em>
	/// (<paramref name="expanded"/> is <see langword="true"/>) so most tests can address the body directly; the
	/// gate tests pass <see langword="false"/> to exercise the collapsed state. The component injects no services,
	/// so no container registration is needed.
	/// </summary>
	/// <param name="backend">The backend whose advanced settings are rendered; defaults to a probing-enabled backend.</param>
	/// <param name="expanded">Whether the disclosure is open; defaults to <see langword="true"/> so the body renders.</param>
	/// <param name="isBusy">Whether the owning page is busy, gating the disclosure header toggle.</param>
	/// <param name="fieldsBusy">
	/// Whether the editable body fields are locked. Defaults to <paramref name="isBusy"/> because in production the
	/// field lock is a superset of the header lock (a globally busy page locks both), so an unspecified value keeps
	/// header and fields in step. Pass an explicit value to exercise the probe case, where the fields lock
	/// (<see langword="true"/>) while the header stays operable (<paramref name="isBusy"/> is <see langword="false"/>).
	/// </param>
	/// <param name="configure">An optional hook to add event-callback parameters the test wants to observe.</param>
	/// <returns>The rendered <see cref="BackendAdvanced"/> component.</returns>
	private IRenderedComponent<BackendAdvanced> RenderAdvanced(
		DesiredBackend?                                               backend    = null,
		bool                                                          expanded   = true,
		bool                                                          isBusy     = false,
		bool?                                                         fieldsBusy = null,
		Action<ComponentParameterCollectionBuilder<BackendAdvanced>>? configure  = null)
	{
		backend ??= CreateBackend();

		return Render<BackendAdvanced>(parameters =>
		{
			parameters
				.Add(component => component.Backend, backend)
				.Add(component => component.Expanded, expanded)
				.Add(component => component.IsBusy, isBusy)
				.Add(component => component.FieldsBusy, fieldsBusy ?? isBusy);

			configure?.Invoke(parameters);
		});
	}

	/// <summary>
	/// Builds a <see cref="DesiredBackend"/> fixture for the advanced-settings form. Only the fields the disclosure
	/// renders are parameterized; the identity fields carry fixed placeholder values because the component reads
	/// nothing but <see cref="DesiredBackend.Options"/>.
	/// </summary>
	/// <param name="contextLength">The fallback context length shown in the field, or <see langword="null"/> for none.</param>
	/// <param name="modelPrefix">The exposure prefix shown in the field, or <see langword="null"/> for none.</param>
	/// <param name="reasoningEffort">The default reasoning effort selected, or <see langword="null"/> for unspecified.</param>
	/// <param name="probing">The probing settings to render; defaults to a fresh instance with every probe enabled.</param>
	/// <returns>The assembled backend draft.</returns>
	private static DesiredBackend CreateBackend(
		int?                      contextLength   = null,
		string?                   modelPrefix     = null,
		ReasoningEffort?          reasoningEffort = null,
		CapabilityProbingOptions? probing         = null) => new()
	{
		Name = "openai-prod",
		OriginalName = "openai-prod",
		Options = new BackendOptions
		{
			ContextLength = contextLength,
			ModelPrefix = modelPrefix,
			ReasoningEffort = reasoningEffort,
			Probing = probing ?? new CapabilityProbingOptions()
		}
	};

	/// <summary>
	/// Gets the disclosure header button — the toggle that opens and closes the advanced body and carries the
	/// <c>aria-expanded</c> / <c>aria-controls</c> state. It is always rendered, open or closed.
	/// </summary>
	/// <param name="cut">The rendered component.</param>
	/// <returns>The header button element.</returns>
	private static IElement Header(IRenderedComponent<BackendAdvanced> cut) =>
		cut.Find("button.backend-advanced-header");

	/// <summary>
	/// Gets the advanced body container. Only present when the disclosure is open, so callers that may render it
	/// closed must query <c>div.backend-advanced-body</c> for presence rather than calling this accessor.
	/// </summary>
	/// <param name="cut">The rendered component.</param>
	/// <returns>The advanced body element.</returns>
	private static IElement Body(IRenderedComponent<BackendAdvanced> cut) => cut.Find("div.backend-advanced-body");

	/// <summary>
	/// Gets the Default context length <c>&lt;input&gt;</c>. It is the only number input among the top-level
	/// <c>backend-field</c> rows (model prefix is text, reasoning effort is a select), so the type selector pins it
	/// unambiguously without depending on document order.
	/// </summary>
	/// <param name="cut">The rendered component.</param>
	/// <returns>The context-length input element.</returns>
	private static IElement ContextLengthInput(IRenderedComponent<BackendAdvanced> cut) =>
		cut.Find("div.backend-field input[type=number]");

	/// <summary>
	/// Gets the Model prefix <c>&lt;input&gt;</c>, the only text input among the top-level <c>backend-field</c> rows.
	/// </summary>
	/// <param name="cut">The rendered component.</param>
	/// <returns>The model-prefix input element.</returns>
	private static IElement ModelPrefixInput(IRenderedComponent<BackendAdvanced> cut) =>
		cut.Find("div.backend-field input[type=text]");

	/// <summary>
	/// Gets the Default reasoning effort <c>&lt;select&gt;</c>, the only select the disclosure renders.
	/// </summary>
	/// <param name="cut">The rendered component.</param>
	/// <returns>The reasoning-effort select element.</returns>
	private static IElement ReasoningEffortSelect(IRenderedComponent<BackendAdvanced> cut) =>
		cut.Find("div.backend-field select");

	/// <summary>
	/// Gets the four capability-probing toggle checkboxes in authored order (Completion, Tools, Vision, Embeddings).
	/// </summary>
	/// <param name="cut">The rendered component.</param>
	/// <returns>The probing toggle checkbox elements.</returns>
	private static IReadOnlyList<IElement> ProbingToggles(IRenderedComponent<BackendAdvanced> cut) =>
		cut.FindAll("div.backend-probing-toggles input[type=checkbox]");

	/// <summary>
	/// Gets the five capability-probing knob number inputs in authored order (timeout, interactive timeout, max
	/// retries, retry base delay, max concurrent probes).
	/// </summary>
	/// <param name="cut">The rendered component.</param>
	/// <returns>The probing knob input elements.</returns>
	private static IReadOnlyList<IElement> ProbingKnobInputs(IRenderedComponent<BackendAdvanced> cut) =>
		cut.FindAll("div.backend-probing-knobs input[type=number]");

	/// <summary>
	/// Gets every interactive control inside the advanced body (both field inputs, the reasoning-effort select, the
	/// four probing toggles, and the five probing knobs) in document order, so the busy-state tests can assert the
	/// disabled attribute across the whole body at once. The header button is asserted separately because it lives
	/// outside the body and stays present when the disclosure is closed.
	/// </summary>
	/// <param name="cut">The rendered component.</param>
	/// <returns>The interactive control elements within the body.</returns>
	private static IReadOnlyList<IElement> BodyControls(IRenderedComponent<BackendAdvanced> cut) =>
		cut.FindAll("div.backend-advanced-body input, div.backend-advanced-body select");

	/// <summary>
	/// Gets the visible label text of each probing toggle in document order. A checkbox contributes no text, so the
	/// label's <see cref="INode.TextContent"/> is the probe name alone.
	/// </summary>
	/// <param name="cut">The rendered component.</param>
	/// <returns>The trimmed toggle labels, one per checkbox, in render order.</returns>
	private static IReadOnlyList<string> ProbingToggleLabels(IRenderedComponent<BackendAdvanced> cut) =>
		cut.FindAll("label.backend-check").Select(label => label.TextContent.Trim()).ToList();

	/// <summary>
	/// Gets the <c>(label, min, max)</c> triple of each probing knob in document order, so the bounds can be asserted
	/// as a single exact sequence against the domain constants that define them.
	/// </summary>
	/// <param name="cut">The rendered component.</param>
	/// <returns>The knob label plus its rendered <c>min</c> and <c>max</c> attributes, in render order.</returns>
	private static IReadOnlyList<(string Label, string Min, string Max)> ProbingKnobBounds(
		IRenderedComponent<BackendAdvanced> cut)
	{
		return cut
			.FindAll("div.backend-probing-knobs label")
			.Select(label =>
			{
				IElement input = label.QuerySelector("input")!;
				return (
					       label.TextContent.Trim(),
					       input.GetAttribute("min") ?? string.Empty,
					       input.GetAttribute("max") ?? string.Empty);
			})
			.ToList();
	}

	/// <summary>
	/// Extracts the <c>(value, text)</c> pair of every <c>&lt;option&gt;</c> in a select, so option lists can be
	/// asserted as a single exact sequence.
	/// </summary>
	/// <param name="select">The select whose options are read.</param>
	/// <returns>The option value/text pairs in document order.</returns>
	private static IReadOnlyList<(string Value, string Text)> OptionPairs(IElement select)
	{
		return select
			.QuerySelectorAll("option")
			.Select(option => (option.GetAttribute("value") ?? string.Empty, option.TextContent.Trim()))
			.ToList();
	}

	/// <summary>
	/// Reads an input's effective value, normalizing an absent <c>value</c> attribute to the empty string. Blazor
	/// omits the attribute entirely for a <see langword="null"/>-bound field (rather than rendering <c>value=""</c>),
	/// so reading the IDL value is the robust way to assert the "no value" case alongside populated ones.
	/// </summary>
	/// <param name="input">The input whose value is read.</param>
	/// <returns>The input's value, or the empty string when unset.</returns>
	private static string InputValue(IElement input) => ((IHtmlInputElement)input).Value;

	/// <summary>
	/// Reads a checkbox's checked state.
	/// </summary>
	/// <param name="checkbox">The checkbox whose state is read.</param>
	/// <returns><see langword="true"/> if the checkbox is checked; otherwise <see langword="false"/>.</returns>
	private static bool IsChecked(IElement checkbox) => ((IHtmlInputElement)checkbox).IsChecked;

	/// <summary>
	/// Formats a bound constant exactly as Blazor renders it into a <c>min</c> / <c>max</c> attribute — a plain
	/// invariant-culture integer — so the expected knob bounds derive from the same domain constants the markup
	/// binds, and a wrong-constant wiring bug surfaces as a mismatch.
	/// </summary>
	/// <param name="value">The bound constant to format.</param>
	/// <returns>The invariant-culture string form of <paramref name="value"/>.</returns>
	private static string Bound(int value) => value.ToString(CultureInfo.InvariantCulture);
}
