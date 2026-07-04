// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Hosting;

/// <summary>
/// Binds the <c>Host</c> section of <c>hostsettings.json</c>, configuring the outer chassis lifecycle that
/// anchors the process (the Service Control Manager or a foreground shell) and supervises the recyclable
/// inner proxy host. It is intentionally separate from <see cref="AdminOptions"/>: the run <see cref="Mode"/>
/// is a process-lifecycle concern peer to the admin endpoint, not a property of it.
/// </summary>
sealed class ChassisOptions
{
	/// <summary>
	/// The configuration section name this options object binds to.
	/// </summary>
	public const string SectionName = "Host";

	/// <summary>
	/// Gets or sets the run mode that governs how a failure to start the inner proxy host is handled. Defaults
	/// to <see cref="HostMode.Auto"/>, which resolves to <see cref="HostMode.Daemon"/> under the Windows Service
	/// Control Manager and to <see cref="HostMode.Foreground"/> otherwise.
	/// </summary>
	public HostMode Mode { get; set; } = HostMode.Auto;
}
