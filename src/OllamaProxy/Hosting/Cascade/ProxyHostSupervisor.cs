// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Microsoft.Extensions.Options;

using OllamaProxy.Core;

namespace OllamaProxy.Hosting.Cascade;

/// <summary>
/// The production <see cref="IProxyHostSupervisor"/>: owns the lifecycle of the single live inner proxy host
/// and serializes every lifecycle transition through one gate, so a recycle never races a start or stop. On the
/// initial start a failure is handled per the configured policy: the foreground policy rethrows so the process
/// exits non-zero, while the daemon policy logs at <c>Critical</c> and stays resident so the chassis remains
/// reachable for a later recovering recycle. A live recycle validates a candidate on a non-binding server first
/// and only swaps when that succeeds, leaving the active host serving on any validation failure.
/// </summary>
sealed partial class ProxyHostSupervisor : IProxyHostSupervisor, IAsyncDisposable
{
	private readonly IProxyHostFactory            mFactory;
	private readonly bool                         mFailFastOnStartFailure;
	private readonly ILogger<ProxyHostSupervisor> mLogger;
	private readonly SemaphoreSlim                mGate = new(1, 1);

	private IHost? mActiveInstance;
	private bool   mDisposed;

	/// <summary>
	/// Initializes a new instance of the <see cref="ProxyHostSupervisor"/> class.
	/// </summary>
	/// <param name="factory">Builds the inner proxy host (dry-run candidates and the real host).</param>
	/// <param name="failFastOnStartFailure">
	/// When <see langword="true"/> (foreground policy), a failure to start the inner host on the initial start
	/// is rethrown so the process exits; when <see langword="false"/> (daemon policy), it is logged and the
	/// chassis stays resident.
	/// </param>
	/// <param name="logger">Records the supervisor's lifecycle and failures.</param>
	/// <exception cref="ArgumentNullException">
	/// <paramref name="factory"/> or <paramref name="logger"/> is <see langword="null"/>.
	/// </exception>
	public ProxyHostSupervisor(
		IProxyHostFactory            factory,
		bool                         failFastOnStartFailure,
		ILogger<ProxyHostSupervisor> logger)
	{
		ArgumentNullException.ThrowIfNull(factory);
		ArgumentNullException.ThrowIfNull(logger);

		mFactory = factory;
		mFailFastOnStartFailure = failFastOnStartFailure;
		mLogger = logger;
	}

	/// <inheritdoc/>
	// Read without the gate so a readiness probe never blocks behind an in-flight recycle. Volatile.Read pairs
	// with the Volatile.Write at every assignment site below to publish the latest reference across threads.
	public bool IsInnerHostActive => Volatile.Read(ref mActiveInstance) is not null;

	/// <inheritdoc/>
	// Read without the gate, mirroring IsInnerHostActive: the catalog read must never block behind an in-flight
	// recycle, and the router's GetModels returns an already-published immutable snapshot. The active reference
	// is taken with a single volatile read; if a concurrent recycle retires and disposes that host between the
	// read and the resolve, the disposed-provider access is caught and reported as "no live catalog", the same
	// not-serving signal as a null reference, so the caller treats both transient states identically.
	public IReadOnlyList<RegisteredModel>? GetLiveModels()
	{
		IHost? host = Volatile.Read(ref mActiveInstance);
		if (host is null)
		{
			return null;
		}

		try
		{
			return host.Services.GetRequiredService<IModelRouter>().GetModels();
		}
		catch (ObjectDisposedException)
		{
			// The host was retired and disposed by a concurrent recycle or stop after the volatile read; there
			// is no live catalog to read, which is the same outcome the caller handles for a null host.
			return null;
		}
	}

	/// <inheritdoc/>
	public async Task StartAsync(CancellationToken cancellationToken)
	{
		await mGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			IHost candidate = mFactory.CreateProxyHost(useDryRunServer: false);
			try
			{
				await candidate.StartAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				await SafeDisposeAsync(candidate).ConfigureAwait(false);
				LogStartFailure(mLogger, ex);

				if (mFailFastOnStartFailure)
				{
					// Foreground policy: surface the failure so the host start fails and the process exits.
					throw;
				}

				// Daemon policy: stay resident so the chassis remains reachable and a later recycle can recover.
				return;
			}

			Volatile.Write(ref mActiveInstance, candidate);
		}
		finally
		{
			mGate.Release();
		}
	}

	/// <inheritdoc/>
	public async Task StopAsync(CancellationToken cancellationToken)
	{
		await mGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (mActiveInstance is null)
			{
				return;
			}

			await SafeStopAsync(mActiveInstance, cancellationToken).ConfigureAwait(false);
			await SafeDisposeAsync(mActiveInstance).ConfigureAwait(false);
			Volatile.Write(ref mActiveInstance, null);
		}
		finally
		{
			mGate.Release();
		}
	}

	/// <inheritdoc/>
	public async Task<RecycleResult> RecycleAsync(CancellationToken cancellationToken)
	{
		await mGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			// 1. Validate a candidate on a non-binding server: this performs the full DI build, options
			//    validation, and startup discovery without contending for the proxy port the live host holds.
			IHost dryRun = mFactory.CreateProxyHost(useDryRunServer: true);
			try
			{
				await dryRun.StartAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				await SafeDisposeAsync(dryRun).ConfigureAwait(false);

				// The active host was never touched and keeps serving; report why the candidate was rejected.
				return RecycleResult.Failed(ExtractValidationErrors(ex));
			}

			// The dry-run has served its purpose (validation only); stop and discard it.
			await SafeStopAsync(dryRun, cancellationToken).ConfigureAwait(false);
			await SafeDisposeAsync(dryRun).ConfigureAwait(false);

			// 2. Build and start the real host. The dry-run already validated the configuration, so the only
			//    new failure mode here is binding the real port, which requires the previous host to release
			//    it first, opening a brief unbound window.
			// TODO(prewarm): start the real host on a scratch port and flip the binding to remove the gap.
			IHost replacement = mFactory.CreateProxyHost(useDryRunServer: false);

			IHost? previous = mActiveInstance;
			Volatile.Write(ref mActiveInstance, null);
			if (previous is not null)
			{
				await SafeStopAsync(previous, cancellationToken).ConfigureAwait(false);
				await SafeDisposeAsync(previous).ConfigureAwait(false);
			}

			try
			{
				await replacement.StartAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				await SafeDisposeAsync(replacement).ConfigureAwait(false);

				// The previous host has already been released, so the proxy is now offline until the next
				// successful recycle. This is the accepted edge of the fixed-port swap (see RecycleAsync docs).
				LogBindFailure(mLogger, ex);

				return RecycleResult.Failed(ExtractValidationErrors(ex));
			}

			// 3. The replacement is live and is now the active host.
			Volatile.Write(ref mActiveInstance, replacement);
			return RecycleResult.Succeeded;
		}
		finally
		{
			mGate.Release();
		}
	}

	/// <inheritdoc/>
	public async ValueTask DisposeAsync()
	{
		if (mDisposed)
		{
			return;
		}

		mDisposed = true;

		if (mActiveInstance is not null)
		{
			await SafeDisposeAsync(mActiveInstance).ConfigureAwait(false);
			Volatile.Write(ref mActiveInstance, null);
		}

		mGate.Dispose();
	}

	/// <summary>
	/// Stops a host, swallowing and logging any non-cancellation error so a faulty stop never aborts the
	/// surrounding lifecycle transition (the host is discarded immediately afterward regardless).
	/// </summary>
	/// <param name="host">The host to stop.</param>
	/// <param name="cancellationToken">A token to cancel the stop.</param>
	private async Task SafeStopAsync(IHost host, CancellationToken cancellationToken)
	{
		try
		{
			await host.StopAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			LogStopError(mLogger, ex);
		}
	}

	/// <summary>
	/// Disposes a host through its richest disposal contract (<see cref="IAsyncDisposable"/> preferred),
	/// swallowing and logging any error so disposal never aborts the surrounding lifecycle transition.
	/// </summary>
	/// <param name="host">The host to dispose.</param>
	private async Task SafeDisposeAsync(IHost host)
	{
		try
		{
			switch (host)
			{
				case IAsyncDisposable asyncDisposable:
					await asyncDisposable.DisposeAsync().ConfigureAwait(false);
					break;

				case IDisposable disposable:
					disposable.Dispose();
					break;
			}
		}
		catch (Exception ex)
		{
			LogDisposeError(mLogger, ex);
		}
	}

	/// <summary>
	/// Extracts human-readable validation errors from a failed candidate start: an
	/// <see cref="OptionsValidationException"/> carries one entry per failed rule, while any other exception is
	/// reduced to its message.
	/// </summary>
	/// <param name="exception">The exception a candidate's start threw.</param>
	/// <returns>The validation errors to report on the failed <see cref="RecycleResult"/>.</returns>
	private static string[] ExtractValidationErrors(Exception exception) =>
		exception is OptionsValidationException optionsValidation && optionsValidation.Failures.Any()
			? optionsValidation.Failures.ToArray()
			: [exception.Message];

	/// <summary>
	/// Logs a critical failure when a validated inner proxy host failed to bind, leaving the proxy offline.
	/// </summary>
	/// <param name="logger">The logger to write to.</param>
	/// <param name="exception">The exception thrown during binding.</param>
	[LoggerMessage(
		Level = LogLevel.Critical,
		Message =
			"The validated inner proxy host failed to bind; the proxy is offline until the next successful recycle.")]
	private static partial void LogBindFailure(ILogger logger, Exception exception);

	/// <summary>
	/// Logs an error that occurred while stopping an inner proxy host.
	/// </summary>
	/// <param name="logger">The logger to write to.</param>
	/// <param name="exception">The exception thrown while stopping the host.</param>
	[LoggerMessage(
		Level = LogLevel.Error,
		Message = "Error stopping an inner proxy host; it will be disposed regardless.")]
	private static partial void LogStopError(ILogger logger, Exception exception);

	/// <summary>
	/// Logs a critical failure when the inner proxy host failed to start.
	/// </summary>
	/// <param name="logger">The logger to write to.</param>
	/// <param name="exception">The exception thrown during start.</param>
	[LoggerMessage(
		Level = LogLevel.Critical,
		Message = "The inner proxy host failed to start.")]
	private static partial void LogStartFailure(ILogger logger, Exception exception);

	/// <summary>
	/// Logs an error that occurred while disposing an inner proxy host.
	/// </summary>
	/// <param name="logger">The logger to write to.</param>
	/// <param name="exception">The exception thrown during disposal.</param>
	[LoggerMessage(Level = LogLevel.Error, Message = "Error disposing an inner proxy host.")]
	private static partial void LogDisposeError(ILogger logger, Exception exception);
}
