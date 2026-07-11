// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.ComponentModel.DataAnnotations;

namespace OllamaProxy.Configuration;

/// <summary>
/// Tunes the transport behavior shared by every outbound backend <see cref="HttpClient"/>: how long a
/// pooled connection may live before it is retired, and how long a single TCP connect attempt may take
/// before it is abandoned. Both knobs exist because the default <c>SocketsHttpHandler</c> keeps
/// connections alive indefinitely and waits on the OS-level SYN timeout, which is the wrong behavior for
/// a proxy that talks to DNS-named cloud backends:
/// <list type="number">
///     <item>
///         <description>
///         A finite <see cref="PooledConnectionLifetimeSeconds"/> makes the pool re-resolve DNS
///         periodically, so a backend whose IP address changed (failover, load-balancer rotation, a new
///         A-record) is picked up without a process restart.
///         </description>
///     </item>
///     <item>
///         <description>
///         A finite <see cref="ConnectTimeoutSeconds"/> caps how long a connect to a dead or black-holed
///         IP hangs, so when a hostname resolves to several addresses the handler fails over to the next
///         one quickly instead of stalling on the OS default SYN timeout.
///         </description>
///     </item>
/// </list>
/// The defaults are safe for the common case; override them via configuration or the
/// <c>OllamaProxy__Connection__PooledConnectionLifetimeSeconds</c> /
/// <c>OllamaProxy__Connection__ConnectTimeoutSeconds</c> environment variables in a constrained or
/// unusually latent environment.
/// </summary>
public sealed class BackendConnectionOptions : IValidatableObject
{
	/// <summary>
	/// The configuration section name this options object binds to, relative to <see cref="ProxyOptions"/>.
	/// </summary>
	public const string SectionName = "Connection";

	/// <summary>
	/// The smallest accepted value for <see cref="PooledConnectionLifetimeSeconds"/>.
	/// </summary>
	public const int MinimumPooledConnectionLifetimeSeconds = 1;

	/// <summary>
	/// The largest accepted value for <see cref="PooledConnectionLifetimeSeconds"/> (one hour).
	/// </summary>
	public const int MaximumPooledConnectionLifetimeSeconds = 3600;

	/// <summary>
	/// The smallest accepted value for <see cref="ConnectTimeoutSeconds"/>.
	/// </summary>
	public const int MinimumConnectTimeoutSeconds = 1;

	/// <summary>
	/// The largest accepted value for <see cref="ConnectTimeoutSeconds"/> (two minutes).
	/// </summary>
	public const int MaximumConnectTimeoutSeconds = 120;

	/// <summary>
	/// Gets or sets how long, in seconds, a pooled connection may be reused before the handler retires it
	/// and opens a fresh one (re-resolving DNS in the process). Defaults to <c>120</c> (two minutes): long
	/// enough that a busy backend keeps its connections warm and avoids a reconnect on every request, yet
	/// short enough that a DNS change (failover, load-balancer rotation) is honored within a couple of
	/// minutes without a restart. Must be between <see cref="MinimumPooledConnectionLifetimeSeconds"/> and
	/// <see cref="MaximumPooledConnectionLifetimeSeconds"/>.
	/// </summary>
	public int PooledConnectionLifetimeSeconds { get; set; } = 120;

	/// <summary>
	/// Gets or sets how long, in seconds, a single TCP connect attempt may take before it is abandoned.
	/// Defaults to <c>10</c>: long enough for a healthy connect across the public internet, yet short
	/// enough that when a hostname resolves to several addresses and one is dead, the handler fails over
	/// to the next address quickly instead of stalling on the OS default SYN timeout (often ~20 s or
	/// more). Must be between <see cref="MinimumConnectTimeoutSeconds"/> and
	/// <see cref="MaximumConnectTimeoutSeconds"/>.
	/// </summary>
	public int ConnectTimeoutSeconds { get; set; } = 10;

	/// <summary>
	/// Gets the pooled-connection lifetime as a <see cref="TimeSpan"/>, derived from
	/// <see cref="PooledConnectionLifetimeSeconds"/>.
	/// </summary>
	public TimeSpan PooledConnectionLifetime => TimeSpan.FromSeconds(PooledConnectionLifetimeSeconds);

	/// <summary>
	/// Gets the connect timeout as a <see cref="TimeSpan"/>, derived from <see cref="ConnectTimeoutSeconds"/>.
	/// </summary>
	public TimeSpan ConnectTimeout => TimeSpan.FromSeconds(ConnectTimeoutSeconds);

	/// <inheritdoc/>
	public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
	{
		if (PooledConnectionLifetimeSeconds is
		    < MinimumPooledConnectionLifetimeSeconds or
		    > MaximumPooledConnectionLifetimeSeconds)
		{
			yield return new ValidationResult(
				$"Backend pooled-connection lifetime must be between {MinimumPooledConnectionLifetimeSeconds} " +
				$"and {MaximumPooledConnectionLifetimeSeconds} seconds.",
				[nameof(PooledConnectionLifetimeSeconds)]);
		}

		if (ConnectTimeoutSeconds is < MinimumConnectTimeoutSeconds or > MaximumConnectTimeoutSeconds)
		{
			yield return new ValidationResult(
				$"Backend connect timeout must be between {MinimumConnectTimeoutSeconds} " +
				$"and {MaximumConnectTimeoutSeconds} seconds.",
				[nameof(ConnectTimeoutSeconds)]);
		}
	}
}
