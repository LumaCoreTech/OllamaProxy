// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Diagnostics;
using OllamaProxy.Admin.Editing;
using OllamaProxy.Configuration;

namespace OllamaProxy.Admin.Ui.Components.Backends;

/// <summary>
/// Translates a backend's draft state and effective operating mode into the header copy the
/// <see cref="BackendCard"/> renders: the display name shown in the collapsed row, the mode badge label, and
/// the mode's tooltip description.
/// </summary>
/// <remarks>
/// The mapping logic lives here rather than in the component's code-behind so it can be unit-tested as a pure
/// function without rendering the component. <see cref="BackendCard"/> is a thin renderer over the strings this
/// presenter returns. The provider-family label is intentionally not here: it is a pass-through to the injected
/// <c>IProviderCatalog</c> rather than a self-contained mapping.
/// </remarks>
static class BackendCardPresenter
{
	/// <summary>
	/// Gets a backend's display name for the card header, falling back to a placeholder when the operator
	/// has not named it yet.
	/// </summary>
	/// <param name="backend">The backend whose name is being rendered.</param>
	/// <returns>The trimmed backend name, or a placeholder when it is blank.</returns>
	public static string DisplayName(DesiredBackend backend) =>
		string.IsNullOrWhiteSpace(backend.Name) ? "(unnamed backend)" : backend.Name;

	/// <summary>
	/// Maps a backend's effective operating mode to its human-readable badge label.
	/// </summary>
	/// <param name="mode">The effective operating mode.</param>
	/// <returns>The badge label for the mode.</returns>
	/// <exception cref="UnreachableException">
	/// <paramref name="mode"/> is not a defined <see cref="OperatingMode"/> value.
	/// </exception>
	public static string ModeLabel(OperatingMode mode) => mode switch
	{
		OperatingMode.PlugAndPlay => "Plug-and-play",
		OperatingMode.Hybrid      => "Hybrid",
		OperatingMode.Explicit    => "Explicit",
		var _                     => throw new UnreachableException($"Unhandled operating mode '{mode}'.")
	};

	/// <summary>
	/// Maps a backend's effective operating mode to a short tooltip description.
	/// </summary>
	/// <param name="mode">The effective operating mode.</param>
	/// <returns>The tooltip description for the mode.</returns>
	/// <exception cref="UnreachableException">
	/// <paramref name="mode"/> is not a defined <see cref="OperatingMode"/> value.
	/// </exception>
	public static string ModeDescription(OperatingMode mode) => mode switch
	{
		OperatingMode.PlugAndPlay =>
			"Every model the backend reports is exposed automatically. Registry pins have no effect.",

		OperatingMode.Hybrid =>
			"Discovered models are exposed automatically, and registry entries override their settings or stand alone.",

		OperatingMode.Explicit =>
			"Only pinned registry models are exposed. Discovered models are listed as candidates.",

		var _ => throw new UnreachableException($"Unhandled operating mode '{mode}'.")
	};
}
