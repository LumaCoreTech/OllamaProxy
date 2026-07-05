// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Admin.Ui.Components;

/// <summary>
/// A single rendered capability chip: its CSS class (visual kind), hover tooltip, and visible label text.
/// Produced by <see cref="CapabilityChipBuilder.BuildChips"/> and rendered one-per-<c>&lt;span&gt;</c> by the
/// <see cref="CapabilityChips"/> component.
/// </summary>
/// <param name="CssClass">The space-separated CSS classes selecting the chip's visual kind.</param>
/// <param name="Title">The hover tooltip explaining what the chip means.</param>
/// <param name="Text">The visible label, with a trailing "?" for an unconfirmed capability.</param>
readonly record struct CapabilityChip(string CssClass, string Title, string Text);
