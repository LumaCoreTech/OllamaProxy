// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Hosting;

namespace OllamaProxy.Tests.Hosting;

/// <summary>
/// Tests for <see cref="HostModeResolver"/>, the central decision that turns the operator-configured
/// <see cref="HostMode"/> and the hosting model into the effective run mode — the decision that governs whether
/// an inner-host start failure kills the process or leaves it resident. Both hosting models are driven through
/// the injected <see cref="FakeServiceEnvironment"/> seam so the full table is verified deterministically
/// without the test process running under the Service Control Manager.
/// </summary>
[Trait("Category", "Unit")]
public sealed class HostModeResolverTests
{
	/// <summary>
	/// Verifies that <see cref="HostMode.Auto"/> resolves to <see cref="HostMode.Daemon"/> under the Service
	/// Control Manager, so a Windows Service stays resident on an inner-host start failure.
	/// </summary>
	[Fact]
	public void Resolve_WhenAutoAndWindowsService_ResolvesToDaemon()
	{
		// Act
		HostMode resolved = HostModeResolver.Resolve(HostMode.Auto, FakeServiceEnvironment.Service);

		// Assert
		Assert.Equal(HostMode.Daemon, resolved);
	}

	/// <summary>
	/// Verifies that <see cref="HostMode.Auto"/> resolves to <see cref="HostMode.Foreground"/> off the Service
	/// Control Manager, so an interactive console / container run fails fast on an inner-host start failure.
	/// </summary>
	[Fact]
	public void Resolve_WhenAutoAndForeground_ResolvesToForeground()
	{
		// Act
		HostMode resolved = HostModeResolver.Resolve(HostMode.Auto, FakeServiceEnvironment.Foreground);

		// Assert
		Assert.Equal(HostMode.Foreground, resolved);
	}

	/// <summary>
	/// Verifies that an explicit <see cref="HostMode.Daemon"/> is honored verbatim even in a foreground process,
	/// the intent a Linux <c>systemd</c> unit sets to get daemon semantics without the SCM.
	/// </summary>
	[Fact]
	public void Resolve_WhenExplicitDaemonInForeground_StaysDaemon()
	{
		// Act
		HostMode resolved = HostModeResolver.Resolve(HostMode.Daemon, FakeServiceEnvironment.Foreground);

		// Assert: the operator's explicit intent overrides detection.
		Assert.Equal(HostMode.Daemon, resolved);
	}

	/// <summary>
	/// Verifies that an explicit <see cref="HostMode.Foreground"/> is honored verbatim even under the Service
	/// Control Manager, so an operator can force fail-fast semantics for a managed service if they choose.
	/// </summary>
	[Fact]
	public void Resolve_WhenExplicitForegroundUnderService_StaysForeground()
	{
		// Act
		HostMode resolved = HostModeResolver.Resolve(HostMode.Foreground, FakeServiceEnvironment.Service);

		// Assert: the operator's explicit intent overrides detection.
		Assert.Equal(HostMode.Foreground, resolved);
	}

	/// <summary>
	/// Verifies the fail-fast projection for <see cref="HostMode.Auto"/> under the Service Control Manager: the
	/// effective daemon mode stays resident (does not fail fast) so a managed service survives a start failure.
	/// </summary>
	[Fact]
	public void ShouldFailFastOnStartFailure_WhenAutoAndWindowsService_IsFalse() => Assert.False(
		HostModeResolver.ShouldFailFastOnStartFailure(HostMode.Auto, FakeServiceEnvironment.Service));

	/// <summary>
	/// Verifies the fail-fast projection for <see cref="HostMode.Auto"/> off the Service Control Manager: the
	/// effective foreground mode fails fast so an interactive run exits non-zero on a start failure.
	/// </summary>
	[Fact]
	public void ShouldFailFastOnStartFailure_WhenAutoAndForeground_IsTrue() => Assert.True(
		HostModeResolver.ShouldFailFastOnStartFailure(HostMode.Auto, FakeServiceEnvironment.Foreground));

	/// <summary>
	/// Verifies that an explicit <see cref="HostMode.Daemon"/> never fails fast, even in a foreground process,
	/// the <c>systemd</c> case where daemon semantics are wanted without the SCM.
	/// </summary>
	[Fact]
	public void ShouldFailFastOnStartFailure_WhenExplicitDaemonInForeground_IsFalse() => Assert.False(
		HostModeResolver.ShouldFailFastOnStartFailure(HostMode.Daemon, FakeServiceEnvironment.Foreground));

	/// <summary>
	/// Verifies that an explicit <see cref="HostMode.Foreground"/> fails fast, even under the Service Control
	/// Manager, so an operator can force fail-fast semantics for a managed service.
	/// </summary>
	[Fact]
	public void ShouldFailFastOnStartFailure_WhenExplicitForegroundUnderService_IsTrue() => Assert.True(
		HostModeResolver.ShouldFailFastOnStartFailure(HostMode.Foreground, FakeServiceEnvironment.Service));

	/// <summary>
	/// Verifies that <see cref="HostModeResolver.Resolve"/> rejects a <see langword="null"/> environment rather
	/// than silently defaulting a hosting decision this important.
	/// </summary>
	[Fact]
	public void Resolve_WhenEnvironmentIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() =>
			HostModeResolver.Resolve(HostMode.Auto, null!));
		Assert.Equal("environment", exception.ParamName);
	}
}
