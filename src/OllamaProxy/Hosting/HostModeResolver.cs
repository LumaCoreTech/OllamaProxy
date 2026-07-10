// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Hosting;

/// <summary>
/// Resolves the operator-configured <see cref="HostMode"/> into the effective run mode the outer chassis acts
/// on, folding <see cref="HostMode.Auto"/> into a concrete <see cref="HostMode.Daemon"/> or
/// <see cref="HostMode.Foreground"/> through the injected <see cref="IServiceEnvironment"/> seam rather than a
/// direct static probe. This is the central runtime decision that governs whether an inner-host start failure
/// kills the process or leaves it resident, so isolating it here (a) makes it deterministically testable across
/// both hosting models without the test process running under the Service Control Manager, and (b) keeps the
/// entry point free of the branching it previously hard-coded against <c>WindowsServiceHelpers.IsWindowsService()</c>.
/// </summary>
static class HostModeResolver
{
	/// <summary>
	/// Resolves the effective run mode: an explicit <see cref="HostMode.Daemon"/> or
	/// <see cref="HostMode.Foreground"/> is honored verbatim (the operator's intent overrides detection), while
	/// <see cref="HostMode.Auto"/> resolves to <see cref="HostMode.Daemon"/> under the Windows Service Control
	/// Manager and to <see cref="HostMode.Foreground"/> otherwise (console / container).
	/// </summary>
	/// <param name="configuredMode">The mode bound from configuration (<see cref="ChassisOptions.Mode"/>).</param>
	/// <param name="environment">The service-environment probe used to resolve <see cref="HostMode.Auto"/>.</param>
	/// <returns>
	/// The concrete run mode: never <see cref="HostMode.Auto"/>, always <see cref="HostMode.Daemon"/> or
	/// <see cref="HostMode.Foreground"/>.
	/// </returns>
	/// <exception cref="ArgumentNullException"><paramref name="environment"/> is <see langword="null"/>.</exception>
	public static HostMode Resolve(HostMode configuredMode, IServiceEnvironment environment)
	{
		ArgumentNullException.ThrowIfNull(environment);

		return configuredMode switch
		{
			HostMode.Daemon     => HostMode.Daemon,
			HostMode.Foreground => HostMode.Foreground,
			// Auto (and any future/unknown value defensively): a Windows Service is a managed daemon; everything
			// else (console, container) is foreground.
			var _ => environment.IsWindowsService ? HostMode.Daemon : HostMode.Foreground
		};
	}

	/// <summary>
	/// Convenience projection of <see cref="Resolve"/> onto the supervisor's fail-fast policy: the foreground
	/// mode fails fast (rethrows a start failure so the process exits non-zero), while the daemon mode does not
	/// (logs and stays resident so a later recycle can recover).
	/// </summary>
	/// <param name="configuredMode">The mode bound from configuration (<see cref="ChassisOptions.Mode"/>).</param>
	/// <param name="environment">The service-environment probe used to resolve <see cref="HostMode.Auto"/>.</param>
	/// <returns><see langword="true"/> to fail fast on an inner-host start failure; otherwise <see langword="false"/>.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="environment"/> is <see langword="null"/>.</exception>
	public static bool ShouldFailFastOnStartFailure(HostMode configuredMode, IServiceEnvironment environment) =>
		Resolve(configuredMode, environment) == HostMode.Foreground;
}
