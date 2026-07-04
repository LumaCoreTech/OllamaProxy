// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using OllamaProxy.Configuration;
using OllamaProxy.Core;
using OllamaProxy.Hosting.Cascade;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Tests.Hosting.Cascade;

// Inner-host lifecycle under the supervisor: from first start through shutdown to live recycle.
//
// These tests follow the single live inner host the supervisor owns, in the order the chassis drives it:
//
//   1. Construction: the supervisor guards its dependencies (WhenFactoryIsNull, WhenLoggerIsNull).
//
//   2. StartAsync: the happy path activates a host (WhenFactorySucceeds). A start failure then forks on the
//      configured policy — daemon stays resident and swallows (WhenInnerStartThrowsInDaemonMode), foreground
//      rethrows so the process exits (WhenInnerStartThrowsInForegroundMode). A faulted candidate is always
//      disposed (WhenInnerStartThrows_DisposesCandidate).
//
//   3. StopAsync: stops and disposes the active host (WhenActive), and is a no-op when nothing is active
//      (WhenNeverStarted).
//
//   4. RecycleAsync — the core safety property: a validated candidate swaps in (WhenDryRunSucceeds), while a
//      candidate that fails dry-run validation is discarded and the EXISTING host keeps serving
//      (WhenDryRunFails). The failure path surfaces the right errors for both options-validation and generic
//      faults (theory rows).
//
//   5. IsInnerHostActive — the readiness projection of every state above: false before a start
//      (WhenNeverStarted), true once a host is active (WhenStarted), false again when a daemon-mode start
//      failure leaves nothing serving (AfterDaemonStartFailure) or after a stop retires the host (AfterStop),
//      true after a validated recycle swaps in a replacement (AfterSuccessfulRecycle), and — the safety
//      property mirrored from section 4 — still true after a rejected recycle keeps the original serving
//      (AfterRejectedRecycle).
//
// For the shared fakes (FakeProxyHost, FakeProxyHostFactory) and CreateSut, see Helpers.
public sealed partial class ProxyHostSupervisorTests
{
	// --- 1. Construction ---

	/// <summary>
	/// Verifies that the constructor rejects a <see langword="null"/> factory.
	/// </summary>
	[Fact]
	public void Constructor_WhenFactoryIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() =>
			new ProxyHostSupervisor(null!, failFastOnStartFailure: false, NullLogger<ProxyHostSupervisor>.Instance));
		Assert.Equal("factory", exception.ParamName);
	}

	/// <summary>
	/// Verifies that the constructor rejects a <see langword="null"/> logger.
	/// </summary>
	[Fact]
	public void Constructor_WhenLoggerIsNull_ThrowsArgumentNullException()
	{
		// Arrange
		FakeProxyHostFactory factory = new();

		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() =>
			new ProxyHostSupervisor(factory, failFastOnStartFailure: false, null!));
		Assert.Equal("logger", exception.ParamName);
	}

	// --- 2. StartAsync ---

	/// <summary>
	/// Verifies that <see cref="ProxyHostSupervisor.StartAsync"/> builds exactly one real host and starts it,
	/// leaving it active and not disposed.
	/// </summary>
	[Fact]
	public async Task StartAsync_WhenFactorySucceeds_ActivatesProxyHost()
	{
		// Arrange
		FakeProxyHost host = new();
		FakeProxyHostFactory factory = new();
		factory.EnqueueRealHost(host);
		ProxyHostSupervisor sut = CreateSut(factory);

		// Act
		await sut.StartAsync(CancellationToken.None);

		// Assert: the single real host was started and is held active (not stopped, not disposed). No dry-run
		// build happens on a plain start — that is exclusive to a recycle.
		Assert.Equal(1, factory.RealHostRequestCount);
		Assert.Equal(0, factory.DryRunHostRequestCount);
		Assert.Equal(1, host.StartCallCount);
		Assert.False(host.StopCalled);
		Assert.False(host.Disposed);
	}

	/// <summary>
	/// Verifies that under the daemon policy a failure to start the inner host is swallowed so the chassis stays
	/// resident, leaving no active host behind.
	/// </summary>
	[Fact]
	public async Task StartAsync_WhenInnerStartThrowsInDaemonMode_StaysResidentAndDoesNotThrow()
	{
		// Arrange: a host whose start faults, under the daemon policy (failFastOnStartFailure: false).
		FakeProxyHost faulting = new(new InvalidOperationException("backend unreachable"));
		FakeProxyHostFactory factory = new();
		factory.EnqueueRealHost(faulting);
		ProxyHostSupervisor sut = CreateSut(factory, failFastOnStartFailure: false);

		// Act: the daemon policy must not propagate the start failure (the SCM anchor stays up).
		await sut.StartAsync(CancellationToken.None);

		// Assert: the faulted candidate was disposed and nothing remains active — a later recycle can recover.
		Assert.Equal(1, faulting.StartCallCount);
		Assert.True(faulting.Disposed);
	}

	/// <summary>
	/// Verifies that under the foreground policy a failure to start the inner host is rethrown so the host start
	/// fails and the process exits non-zero.
	/// </summary>
	[Fact]
	public async Task StartAsync_WhenInnerStartThrowsInForegroundMode_Rethrows()
	{
		// Arrange: the same faulting host, but under the foreground policy (failFastOnStartFailure: true).
		var startFailure = new InvalidOperationException("backend unreachable");
		FakeProxyHost faulting = new(startFailure);
		FakeProxyHostFactory factory = new();
		factory.EnqueueRealHost(faulting);
		ProxyHostSupervisor sut = CreateSut(factory, failFastOnStartFailure: true);

		// Act + Assert: the original failure propagates unchanged, and the candidate is disposed on the way out.
		var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			                sut.StartAsync(CancellationToken.None));
		Assert.Same(startFailure, exception);
		Assert.True(faulting.Disposed);
	}

	// --- 3. StopAsync ---

	/// <summary>
	/// Verifies that <see cref="ProxyHostSupervisor.StopAsync"/> stops and disposes the active host.
	/// </summary>
	[Fact]
	public async Task StopAsync_WhenActive_StopsAndDisposesInstance()
	{
		// Arrange: a started supervisor holding one active host.
		FakeProxyHost host = new();
		FakeProxyHostFactory factory = new();
		factory.EnqueueRealHost(host);
		ProxyHostSupervisor sut = CreateSut(factory);
		await sut.StartAsync(CancellationToken.None);

		// Act
		await sut.StopAsync(CancellationToken.None);

		// Assert: the active host was stopped and then disposed.
		Assert.Equal(1, host.StopCallCount);
		Assert.True(host.Disposed);
	}

	/// <summary>
	/// Verifies that <see cref="ProxyHostSupervisor.StopAsync"/> is a no-op when no host was ever started, so a
	/// failed daemon start followed by shutdown does not fault.
	/// </summary>
	[Fact]
	public async Task StopAsync_WhenNeverStarted_DoesNothing()
	{
		// Arrange: a supervisor that was never started (no hosts seeded, none requested).
		FakeProxyHostFactory factory = new();
		ProxyHostSupervisor sut = CreateSut(factory);

		// Act
		await sut.StopAsync(CancellationToken.None);

		// Assert: nothing was built and the call completed without faulting.
		Assert.Equal(0, factory.RealHostRequestCount);
		Assert.Equal(0, factory.DryRunHostRequestCount);
	}

	// --- 4. RecycleAsync ---

	/// <summary>
	/// Verifies that a recycle whose dry-run candidate validates stops and disposes the previous host, starts
	/// the freshly built replacement, and reports success.
	/// </summary>
	[Fact]
	public async Task RecycleAsync_WhenDryRunSucceeds_SwapsToNewInstance()
	{
		// Arrange: an initially active host, plus a dry-run candidate and a replacement that both start cleanly.
		FakeProxyHost original = new();
		FakeProxyHost dryRun = new();
		FakeProxyHost replacement = new();
		FakeProxyHostFactory factory = new();
		factory.EnqueueRealHost(original);
		factory.EnqueueDryRunHost(dryRun);
		factory.EnqueueRealHost(replacement);
		ProxyHostSupervisor sut = CreateSut(factory);
		await sut.StartAsync(CancellationToken.None);

		// Act
		RecycleResult result = await sut.RecycleAsync(CancellationToken.None);

		// Assert: success with no errors.
		Assert.True(result.Success);
		Assert.Empty(result.ValidationErrors);

		// The dry-run candidate was started for validation, then stopped and disposed (never kept active).
		Assert.Equal(1, dryRun.StartCallCount);
		Assert.True(dryRun.Disposed);

		// The previous host was retired: stopped and disposed.
		Assert.Equal(1, original.StopCallCount);
		Assert.True(original.Disposed);

		// The replacement is the new active host: started, still running, not disposed.
		Assert.Equal(1, replacement.StartCallCount);
		Assert.False(replacement.Disposed);

		// Exactly two real builds (original + replacement) and one dry-run build occurred.
		Assert.Equal(2, factory.RealHostRequestCount);
		Assert.Equal(1, factory.DryRunHostRequestCount);
	}

	/// <summary>
	/// Verifies the core safety property: when the dry-run candidate fails validation, the candidate is
	/// discarded, no replacement is built, and the existing host keeps serving — and that the reported errors
	/// match the failure kind (options-validation failures listed individually, a generic fault reduced to its
	/// message).
	/// </summary>
	/// <param name="failureKind">Which kind of failure the dry-run candidate raises.</param>
	/// <param name="expectedErrors">The validation errors the result is expected to carry.</param>
	[Theory]
	[MemberData(nameof(DryRunFailureCases))]
	public async Task RecycleAsync_WhenDryRunFails_KeepsExistingInstanceRunning(
		DryRunFailureKind failureKind,
		string[]          expectedErrors)
	{
		// Arrange: an active host, and a dry-run candidate that faults with the scripted failure kind.
		FakeProxyHost original = new();
		FakeProxyHost faultingDryRun = new(CreateDryRunFailure(failureKind));
		FakeProxyHostFactory factory = new();
		factory.EnqueueRealHost(original);
		factory.EnqueueDryRunHost(faultingDryRun);
		ProxyHostSupervisor sut = CreateSut(factory);
		await sut.StartAsync(CancellationToken.None);

		// Act
		RecycleResult result = await sut.RecycleAsync(CancellationToken.None);

		// Assert: failure carrying the expected, kind-specific errors.
		Assert.False(result.Success);
		Assert.Equal(expectedErrors, result.ValidationErrors);

		// The faulted candidate was disposed; no replacement real host was built (only the original).
		Assert.True(faultingDryRun.Disposed);
		Assert.Equal(1, factory.RealHostRequestCount);
		Assert.Equal(1, factory.DryRunHostRequestCount);

		// The safety property: the original host was NEVER touched — still running, never stopped or disposed.
		Assert.Equal(1, original.StartCallCount);
		Assert.False(original.StopCalled);
		Assert.False(original.Disposed);
	}

	/// <summary>
	/// Provides the two dry-run failure variants: an options-validation exception (whose individual failures are
	/// surfaced) and a generic exception (reduced to its message).
	/// </summary>
	public static TheoryData<DryRunFailureKind, string[]> DryRunFailureCases => new()
	{
		{ DryRunFailureKind.OptionsValidation, ["At least one backend must be configured.", "ApiKey is required."] },
		{ DryRunFailureKind.Generic, ["catastrophic discovery failure"] }
	};

	/// <summary>
	/// Builds the exception a dry-run candidate should fault with for the given <paramref name="failureKind"/>.
	/// </summary>
	/// <param name="failureKind">The kind of failure to construct.</param>
	/// <returns>The exception the fake dry-run host will throw from its start.</returns>
	private static Exception CreateDryRunFailure(DryRunFailureKind failureKind) => failureKind switch
	{
		DryRunFailureKind.OptionsValidation => new OptionsValidationException(
			nameof(ProxyOptions),
			typeof(ProxyOptions),
			["At least one backend must be configured.", "ApiKey is required."]),
		DryRunFailureKind.Generic => new InvalidOperationException("catastrophic discovery failure"),
		var _ => throw new ArgumentOutOfRangeException(nameof(failureKind), failureKind, "Unhandled failure kind.")
	};

	// --- 5. IsInnerHostActive ---

	/// <summary>
	/// Verifies that <see cref="ProxyHostSupervisor.IsInnerHostActive"/> is <see langword="false"/> before the
	/// supervisor has ever started, so a readiness probe reports not-ready until the first host is activated.
	/// </summary>
	[Fact]
	public void IsInnerHostActive_WhenNeverStarted_ReturnsFalse()
	{
		// Arrange: a supervisor that was never started (no hosts seeded, none requested).
		FakeProxyHostFactory factory = new();
		ProxyHostSupervisor sut = CreateSut(factory);

		// Act + Assert: nothing is active before the first start.
		Assert.False(sut.IsInnerHostActive);
	}

	/// <summary>
	/// Verifies that <see cref="ProxyHostSupervisor.IsInnerHostActive"/> is <see langword="true"/> once a host
	/// has been started and activated.
	/// </summary>
	[Fact]
	public async Task IsInnerHostActive_WhenStarted_ReturnsTrue()
	{
		// Arrange: a started supervisor holding one active host.
		FakeProxyHost host = new();
		FakeProxyHostFactory factory = new();
		factory.EnqueueRealHost(host);
		ProxyHostSupervisor sut = CreateSut(factory);
		await sut.StartAsync(CancellationToken.None);

		// Act + Assert: the activated host makes the proxy ready.
		Assert.True(sut.IsInnerHostActive);
	}

	/// <summary>
	/// Verifies that <see cref="ProxyHostSupervisor.IsInnerHostActive"/> is <see langword="false"/> after a
	/// daemon-mode start failure: the chassis stays resident (liveness), but with no host serving the proxy is
	/// not ready.
	/// </summary>
	[Fact]
	public async Task IsInnerHostActive_AfterDaemonStartFailure_ReturnsFalse()
	{
		// Arrange: a host whose start faults, under the daemon policy that swallows the failure and stays up.
		FakeProxyHost faulting = new(new InvalidOperationException("backend unreachable"));
		FakeProxyHostFactory factory = new();
		factory.EnqueueRealHost(faulting);
		ProxyHostSupervisor sut = CreateSut(factory, failFastOnStartFailure: false);
		await sut.StartAsync(CancellationToken.None);

		// Act + Assert: the chassis is alive but nothing is serving, so readiness is false — the distinction
		// liveness alone cannot express.
		Assert.False(sut.IsInnerHostActive);
	}

	/// <summary>
	/// Verifies that <see cref="ProxyHostSupervisor.IsInnerHostActive"/> is <see langword="false"/> after the
	/// active host has been stopped during shutdown.
	/// </summary>
	[Fact]
	public async Task IsInnerHostActive_AfterStop_ReturnsFalse()
	{
		// Arrange: a started supervisor whose active host is then stopped (the shutdown path).
		FakeProxyHost host = new();
		FakeProxyHostFactory factory = new();
		factory.EnqueueRealHost(host);
		ProxyHostSupervisor sut = CreateSut(factory);
		await sut.StartAsync(CancellationToken.None);
		await sut.StopAsync(CancellationToken.None);

		// Act + Assert: the retired host leaves nothing active.
		Assert.False(sut.IsInnerHostActive);
	}

	/// <summary>
	/// Verifies that <see cref="ProxyHostSupervisor.IsInnerHostActive"/> is <see langword="true"/> after a
	/// validated recycle swaps the original host for a freshly built replacement.
	/// </summary>
	[Fact]
	public async Task IsInnerHostActive_AfterSuccessfulRecycle_ReturnsTrue()
	{
		// Arrange: an initially active host, plus a dry-run candidate and a replacement that both start cleanly.
		FakeProxyHost original = new();
		FakeProxyHost dryRun = new();
		FakeProxyHost replacement = new();
		FakeProxyHostFactory factory = new();
		factory.EnqueueRealHost(original);
		factory.EnqueueDryRunHost(dryRun);
		factory.EnqueueRealHost(replacement);
		ProxyHostSupervisor sut = CreateSut(factory);
		await sut.StartAsync(CancellationToken.None);
		await sut.RecycleAsync(CancellationToken.None);

		// Act + Assert: the swapped-in replacement keeps the proxy ready.
		Assert.True(sut.IsInnerHostActive);
	}

	/// <summary>
	/// Verifies the readiness side of the recycle safety property: when a recycle is rejected at dry-run
	/// validation, the original host keeps serving, so <see cref="ProxyHostSupervisor.IsInnerHostActive"/>
	/// remains <see langword="true"/> (counterpart to <see cref="RecycleAsync_WhenDryRunFails_KeepsExistingInstanceRunning"/>
	/// ).
	/// </summary>
	[Fact]
	public async Task IsInnerHostActive_AfterRejectedRecycle_ReturnsTrue()
	{
		// Arrange: an active host, and a dry-run candidate that faults so the recycle is rejected.
		FakeProxyHost original = new();
		FakeProxyHost faultingDryRun = new(new InvalidOperationException("catastrophic discovery failure"));
		FakeProxyHostFactory factory = new();
		factory.EnqueueRealHost(original);
		factory.EnqueueDryRunHost(faultingDryRun);
		ProxyHostSupervisor sut = CreateSut(factory);
		await sut.StartAsync(CancellationToken.None);
		RecycleResult result = await sut.RecycleAsync(CancellationToken.None);

		// Act + Assert: the rejected recycle never touched the original, so readiness is preserved.
		Assert.False(result.Success);
		Assert.True(sut.IsInnerHostActive);
	}

	// --- 6. GetLiveModels — the live-catalog read the chassis admin surface uses ---

	/// <summary>
	/// Verifies that <see cref="ProxyHostSupervisor.GetLiveModels"/> returns <see langword="null"/> before any
	/// host has started — the "proxy is not serving" signal the admin surface renders as a transient message.
	/// </summary>
	[Fact]
	public void GetLiveModels_WhenNeverStarted_ReturnsNull()
	{
		// Arrange: a supervisor that was never started.
		FakeProxyHostFactory factory = new();
		ProxyHostSupervisor sut = CreateSut(factory);

		// Act + Assert: with no active host there is no catalog to read.
		Assert.Null(sut.GetLiveModels());
	}

	/// <summary>
	/// Verifies that <see cref="ProxyHostSupervisor.GetLiveModels"/> returns the active inner host's catalog,
	/// read through that host's <see cref="IModelRouter"/>.
	/// </summary>
	[Fact]
	public async Task GetLiveModels_WhenStarted_ReturnsActiveHostCatalog()
	{
		// Arrange: a started supervisor whose active host exposes a router over a known catalog.
		RegisteredModel model = new("gpt-4o", "cloud", "gpt-4o", ModelCapabilities.CompletionOnly, 128_000);
		FakeProxyHost host = CreateHostWithCatalog([model]);
		FakeProxyHostFactory factory = new();
		factory.EnqueueRealHost(host);
		ProxyHostSupervisor sut = CreateSut(factory);
		await sut.StartAsync(CancellationToken.None);

		// Act: read the live catalog.
		IReadOnlyList<RegisteredModel>? models = sut.GetLiveModels();

		// Assert: the active host's catalog is surfaced verbatim.
		Assert.NotNull(models);
		Assert.Equal(model, Assert.Single(models));
	}

	/// <summary>
	/// Verifies that <see cref="ProxyHostSupervisor.GetLiveModels"/> returns <see langword="null"/> again after
	/// the active host is stopped during shutdown — the catalog is gone with the host.
	/// </summary>
	[Fact]
	public async Task GetLiveModels_AfterStop_ReturnsNull()
	{
		// Arrange: a started supervisor whose active host is then stopped.
		RegisteredModel model = new("gpt-4o", "cloud", "gpt-4o", ModelCapabilities.CompletionOnly, 128_000);
		FakeProxyHost host = CreateHostWithCatalog([model]);
		FakeProxyHostFactory factory = new();
		factory.EnqueueRealHost(host);
		ProxyHostSupervisor sut = CreateSut(factory);
		await sut.StartAsync(CancellationToken.None);
		await sut.StopAsync(CancellationToken.None);

		// Act + Assert: the retired host leaves no catalog to read.
		Assert.Null(sut.GetLiveModels());
	}
}
