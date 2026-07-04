// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Admin.Ui.Components;

/// <summary>
/// The colour family of a <see cref="Badge"/> pill. Each variant pairs a foreground/background colour with a
/// semantic role so callers pick by meaning (success, danger, …) rather than by raw colour, keeping the admin
/// surface's badge palette consistent from one component to the next.
/// </summary>
public enum BadgeVariant
{
	/// <summary>
	/// Neutral slate — informationally quiet labels such as a backend's operating mode.
	/// </summary>
	Neutral,

	/// <summary>
	/// Green — a positive, healthy state (e.g. an available model).
	/// </summary>
	Success,

	/// <summary>
	/// Red — a negative or error state (e.g. an unavailable model).
	/// </summary>
	Danger,

	/// <summary>
	/// Blue — a purely informational state (e.g. a discovered, not-yet-pinned model).
	/// </summary>
	Info,

	/// <summary>
	/// Amber — a caution state that needs attention but is not an error (e.g. a drifted pin).
	/// </summary>
	Warning
}
