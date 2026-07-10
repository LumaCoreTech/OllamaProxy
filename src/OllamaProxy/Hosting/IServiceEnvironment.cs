// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Hosting;

/// <summary>
/// Abstracts the single environment probe the two-host cascade branches on: whether the process is running
/// under the Windows Service Control Manager (SCM). Every hosting decision that differs between a managed
/// service and a foreground run (console / container) — the content-root pin, the ProgramData configuration
/// overlays, the Event Log wiring, the writable data directory, and the fail-fast policy — keys off this one
/// flag. Introducing it as a seam lets those SCM-specific branches be exercised deterministically in tests,
/// which a direct call to the static <c>WindowsServiceHelpers.IsWindowsService()</c> would not allow.
/// </summary>
interface IServiceEnvironment
{
	/// <summary>
	/// Gets a value indicating whether the current process is hosted by the Windows Service Control Manager.
	/// </summary>
	/// <value>
	/// <see langword="true"/> when the process runs as a Windows Service;
	/// otherwise <see langword="false"/> for foreground hosting such as a console run or a container.
	/// </value>
	bool IsWindowsService { get; }
}
