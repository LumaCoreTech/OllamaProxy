// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Configuration;

/// <summary>
/// Selects how a single backend's exposed model list is assembled. Each backend carries its own mode
/// (see <see cref="BackendOptions.Mode"/>); all three modes run on the same machinery and differ only
/// in whether discovered models, registered models, or both are published for that backend.
/// </summary>
public enum OperatingMode
{
	/// <summary>
	/// Every model the backend reports is exposed automatically, using detected capabilities. Ideal for
	/// providers that advertise rich capability metadata, and the zero-configuration default for them.
	/// </summary>
	PlugAndPlay,

	/// <summary>
	/// A blend of automatic exposure and an explicit registry: the backend's discovered models are
	/// published, and any matching registry entry overrides their pinned settings. Registry entries with
	/// no discovered counterpart are published as well.
	/// </summary>
	Hybrid,

	/// <summary>
	/// Only models listed in the backend's registry are exposed; discovered models are ignored. This
	/// yields a fully pinned, reproducible surface and is the conservative default for providers that
	/// report little or no capability metadata. A backend in this mode with an empty registry is valid:
	/// it simply contributes no models.
	/// </summary>
	Explicit
}
