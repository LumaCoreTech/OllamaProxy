// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Admin.Ui.Components;

/// <summary>
/// Translates a resolved <see cref="ModelCapabilities"/> set into the ordered capability chips the
/// <see cref="CapabilityChips"/> component renders. A confirmed capability is a solid chip; a supported capability
/// whose probe stayed inconclusive is a supported-but-unconfirmed chip (carrying a "?"); an unsupported optional
/// capability (tools, vision, embeddings) whose probe stayed inconclusive is an off-but-unconfirmed "?" chip. A
/// capability that was conclusively probed as unsupported produces no chip, so the cell shows what is known rather
/// than a list of negatives. The unconfirmed chips are what stop a failed probe from reading as a measured fact.
/// </summary>
/// <remarks>
/// The chip-building logic lives here rather than in the component's code-behind so it can be unit-tested as a
/// pure function without rendering the component. <see cref="CapabilityChips"/> is a thin renderer over the
/// chips this builder returns.
/// </remarks>
static class CapabilityChipBuilder
{
	/// <summary>
	/// Builds the chip descriptors for a capability set, omitting any capability that was conclusively probed as
	/// unsupported so the rendered list never reads as a series of negatives.
	/// </summary>
	/// <param name="capabilities">The capabilities to translate into chips.</param>
	/// <returns>The chips to render, in fixed capability order; empty when nothing is supported or unconfirmed.</returns>
	public static IReadOnlyList<CapabilityChip> BuildChips(ModelCapabilities capabilities)
	{
		(string Label, bool Supported, bool Inconclusive)[] candidates =
		[
			("completion",
			 capabilities.SupportsCompletion,
			 capabilities.Inconclusive.HasFlag(InconclusiveCapabilities.Completion)),
			("tools",
			 capabilities.SupportsTools,
			 capabilities.Inconclusive.HasFlag(InconclusiveCapabilities.Tools)),
			("vision",
			 capabilities.SupportsVision,
			 capabilities.Inconclusive.HasFlag(InconclusiveCapabilities.Vision)),
			("embeddings",
			 capabilities.SupportsEmbeddings,
			 capabilities.Inconclusive.HasFlag(InconclusiveCapabilities.Embeddings))
		];

		List<CapabilityChip> chips = [];

		foreach ((string label, bool supported, bool inconclusive) in candidates)
		{
			// A capability earns a chip when it is supported (a positive fact: confirmed, or fail-open-unconfirmed
			// for completion) or when its probe was inconclusive (an honest "unknown"); a measured-unsupported
			// capability is omitted so the cell never reads as noes.
			if (!supported && !inconclusive) continue;

			// Two visual kinds reach this point: a confirmed capability (solid green) and a capability whose probe
			// stayed inconclusive (dashed "?": unverified). Invariant: only completion is ever supported AND
			// inconclusive at once, because completion is the sole fail-open capability — an inconclusive completion
			// probe keeps SupportsCompletion at its true baseline, whereas an inconclusive tools/vision/embeddings
			// probe leaves the optional flag at its conservative false. The configuration and provider-metadata
			// paths set Inconclusive to None, so they never produce a supported-and-inconclusive optional capability
			// either. See ModelCapabilities.InconclusiveCapabilities and OpenAiCompatibleProvider.DetermineCapabilitiesAsync.
			string cssClass = (supported, inconclusive) switch
			{
				(true, false) => "cap-chip cap-chip-supported",
				(true, true)  => "cap-chip cap-chip-supported-unconfirmed",
				var _         => "cap-chip cap-chip-inconclusive"
			};

			// Page-neutral wording (no "on the Backends page") so the same chip reads correctly wherever it is
			// rendered, including on the Backends page itself. The phrasing mirrors the inconclusive-probe logs so
			// the UI and the logs tell the operator the same thing. The (true, true) arm speaks in fail-open terms
			// unconditionally, which is safe: per the invariant above, only completion — the fail-open capability —
			// ever reaches it.
			string title = (supported, inconclusive) switch
			{
				(true, false) => $"Supports {label} (confirmed by probe or backend metadata).",
				(true, true) =>
					$"The {label} probe stayed inconclusive (it timed out or kept failing), so this is " +
					"unconfirmed — the model is kept capable anyway (fail-open) and stays exposed. Pin the model " +
					"to set its capabilities explicitly.",
				var _ =>
					$"The {label} probe stayed inconclusive (it timed out or kept failing), so this is " +
					"unconfirmed — the model may still support it. Pin the model to set its capabilities explicitly."
			};

			chips.Add(new CapabilityChip(cssClass, title, inconclusive ? $"{label} ?" : label));
		}

		return chips;
	}
}
