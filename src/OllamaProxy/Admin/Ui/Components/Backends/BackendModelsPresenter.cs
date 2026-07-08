// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Globalization;

using OllamaProxy.Admin.Reconciliation;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Admin.Ui.Components.Backends;

/// <summary>
/// Translates a backend's reconciled model rows into the display strings the <see cref="BackendModels"/> table
/// renders: the compact capability and context summaries, the drift tooltip, the per-million-token pricing line,
/// a stable per-row detail-panel id, and the inline duplicate-name check.
/// </summary>
/// <remarks>
/// The mapping logic lives here rather than in the component's code-behind so it can be unit-tested as a pure
/// function without rendering the (large) component. <see cref="BackendModels"/> is a thin renderer over the
/// strings this presenter returns. The two members that the component still wraps —
/// <see cref="ModelDetailPanelId"/> and <see cref="IsDuplicateName"/> — take the component's per-instance state
/// (the DOM-id seed and the pinned-name lookup) as arguments so the mapping itself stays pure and side-effect free.
/// </remarks>
static class BackendModelsPresenter
{
	/// <summary>
	/// Gets a compact, comma-separated list of the capabilities a model supports, or a placeholder when the set
	/// is unknown or nothing is enabled.
	/// </summary>
	/// <param name="capabilities">
	/// The capability set to summarize, or <see langword="null"/> when the backend left them undetermined.
	/// </param>
	/// <returns>
	/// A comma-separated list of the enabled capabilities (e.g. <c>"completion, tools"</c>); <c>"none"</c> when
	/// the set is known but no flag is enabled; or <c>"—"</c> when <paramref name="capabilities"/> is
	/// <see langword="null"/>.
	/// </returns>
	public static string CapabilitySummary(ModelCapabilities? capabilities)
	{
		if (capabilities is null) return "—";

		List<string> flags = [];
		if (capabilities.SupportsCompletion) flags.Add("completion");
		if (capabilities.SupportsTools) flags.Add("tools");
		if (capabilities.SupportsVision) flags.Add("vision");
		if (capabilities.SupportsEmbeddings) flags.Add("embeddings");

		return flags.Count > 0 ? string.Join(", ", flags) : "none";
	}

	/// <summary>
	/// Gets a human-readable context-window value, formatting the token count with thousands separators or a
	/// placeholder when none could be resolved.
	/// </summary>
	/// <param name="contextLength">The context window in tokens, or <see langword="null"/> when unresolved.</param>
	/// <returns>
	/// The token count formatted with thousands separators (invariant culture), or <c>"—"</c> when
	/// <paramref name="contextLength"/> is <see langword="null"/>.
	/// </returns>
	public static string ContextSummary(long? contextLength)
	{
		return contextLength is { } tokens
			       ? tokens.ToString("N0", CultureInfo.InvariantCulture)
			       : "—";
	}

	/// <summary>
	/// Gets a short, human-readable description of how a drifted pin diverges from the backend's reported shape,
	/// naming the capability and context differences so the operator can see what the pin still overrides.
	/// </summary>
	/// <param name="model">The available pin whose drift is being described.</param>
	/// <returns>
	/// A sentence detailing the pinned-versus-reported capability and/or context differences; or a generic
	/// fallback sentence when neither facet reports a concrete difference.
	/// </returns>
	public static string DriftSummary(ReconciledModel model)
	{
		List<string> parts = [];

		if (model.HasCapabilityDrift)
		{
			parts.Add(
				$"capabilities are pinned as {CapabilitySummary(model.Capabilities)}, but the backend reports {CapabilitySummary(model.DiscoveredCapabilities)}");
		}

		if (model.HasContextDrift)
		{
			parts.Add(
				$"context is pinned as {ContextSummary(model.ContextLength)}, but the backend reports {ContextSummary(model.DiscoveredContextLength)}");
		}

		return parts.Count > 0
			       ? $"Pinned settings differ from the backend: {string.Join("; ", parts)}."
			       : "Pinned settings differ from what the backend reports.";
	}

	/// <summary>
	/// Formats a model's normalized per-million-token pricing for display, emitting an input/output pair when
	/// both are known, a single labeled figure when only one is, or an empty string when the backend reported
	/// no price.
	/// </summary>
	/// <param name="metadata">The backend-reported metadata carrying the normalized prices.</param>
	/// <returns>
	/// <c>"in $x · out $y"</c> when both prices are known, <c>"in $x"</c> or <c>"out $y"</c> when only one is,
	/// or <see cref="string.Empty"/> when neither price was reported.
	/// </returns>
	public static string PriceSummary(ProviderModelMetadata metadata)
	{
		string? input = FormatUsd(metadata.PromptUsdPerMillionTokens);
		string? output = FormatUsd(metadata.CompletionUsdPerMillionTokens);

		return (input, output) switch
		{
			({ } i, { } o) => $"in {i} · out {o}",
			({ } i, null)  => $"in {i}",
			(null, { } o)  => $"out {o}",
			var _          => string.Empty
		};
	}

	/// <summary>
	/// Formats a USD amount with enough precision for the small per-token fractions, or <see langword="null"/>
	/// when the amount is absent.
	/// </summary>
	/// <param name="amount">The USD amount to format, or <see langword="null"/> when not reported.</param>
	/// <returns>
	/// The amount prefixed with <c>"$"</c>, using up to four fractional digits for sub-dollar values and two for
	/// larger ones; or <see langword="null"/> when <paramref name="amount"/> is <see langword="null"/>.
	/// </returns>
	public static string? FormatUsd(decimal? amount)
	{
		return amount is { } value
			       ? "$" + value.ToString(value < 1m ? "0.####" : "0.00", CultureInfo.InvariantCulture)
			       : null;
	}

	/// <summary>
	/// Builds a stable identifier for a model's detail panel so the toggle button can reference it with
	/// <c>aria-controls</c>. The row index is unique within a single rendered table and the component id prevents
	/// collisions between several backend tables on the same page.
	/// </summary>
	/// <param name="componentId">The owning component's stable per-instance id seed.</param>
	/// <param name="rowIndex">The model row index in the current reconciliation table.</param>
	/// <returns>A detail panel id unique to the model within its backend table.</returns>
	public static string ModelDetailPanelId(string componentId, int rowIndex)
	{
		return $"backend-model-detail-{componentId}-{rowIndex.ToString(CultureInfo.InvariantCulture)}";
	}

	/// <summary>
	/// Returns <see langword="true"/> when a pinned model's current client-facing name collides with another
	/// entry on this backend, so the row's name input can be flagged inline. The name is trimmed before the
	/// membership test to match <paramref name="duplicateNames"/>, whose keys are trimmed; a blank name is never
	/// a duplicate (its own required-name rule reports it instead).
	/// </summary>
	/// <param name="name">The pinned model's current client-facing name.</param>
	/// <param name="duplicateNames">
	/// The set of client-facing names the backend registers more than once, trimmed and compared
	/// case-insensitively.
	/// </param>
	/// <returns>
	/// <see langword="true"/> if <paramref name="name"/> is non-blank and its trimmed form is one of
	/// <paramref name="duplicateNames"/>; otherwise <see langword="false"/>.
	/// </returns>
	public static bool IsDuplicateName(string? name, IReadOnlySet<string> duplicateNames)
	{
		return !string.IsNullOrWhiteSpace(name) && duplicateNames.Contains(name.Trim());
	}
}
