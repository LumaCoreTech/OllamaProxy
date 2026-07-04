// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Hosting;

/// <summary>
/// Selects how the outer chassis treats a failure to start the inner proxy host. The mode captures the
/// operator's <em>intent</em> (managed daemon versus interactive foreground) which cannot be derived from
/// the operating system: a Linux <c>systemd</c> unit runs in the foreground just like a console session yet
/// wants daemon semantics. Defaults to <see cref="Auto"/>, which resolves to <see cref="Daemon"/> under the
/// Windows Service Control Manager and to <see cref="Foreground"/> otherwise.
/// </summary>
enum HostMode
{
	/// <summary>
	/// Resolve the effective mode from the hosting environment: <see cref="Daemon"/> when running under the
	/// Windows Service Control Manager, <see cref="Foreground"/> otherwise. This is the default and the right
	/// choice for a Windows Service, which is detected automatically without any extra configuration.
	/// </summary>
	Auto,

	/// <summary>
	/// Managed-service semantics: a failure to start the inner proxy host is logged at <c>Critical</c> but the
	/// outer chassis stays resident, so the supervisor remains reachable and a later recycle can recover. Set
	/// this explicitly for a Linux <c>systemd</c> service, which runs in the foreground yet must not exit on a
	/// transient misconfiguration.
	/// </summary>
	Daemon,

	/// <summary>
	/// Interactive-foreground semantics: a failure to start the inner proxy host is logged at <c>Critical</c>
	/// and then rethrown, so the process exits with a non-zero code. This is the least surprising behavior for a
	/// developer at a console or a <c>docker run -it</c> session, where a dead proxy should fail loudly.
	/// </summary>
	Foreground
}
