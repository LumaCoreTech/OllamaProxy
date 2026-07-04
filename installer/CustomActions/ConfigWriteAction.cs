// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.CustomActions;

/// <summary>
/// The action the deferred writer takes for an existing or absent <c>appsettings.json</c>, as
/// resolved by <see cref="CustomActions.DecideConfigWriteAction"/>.
/// </summary>
enum ConfigWriteAction
{
	/// <summary>
	/// Keep the existing configuration untouched (an upgrade or a repair).
	/// </summary>
	Preserve,

	/// <summary>
	/// Write the wizard's configuration; there is no existing file to preserve.
	/// </summary>
	Write,

	/// <summary>
	/// Back up the existing configuration, then write the wizard's configuration.
	/// </summary>
	BackUpAndWrite
}
