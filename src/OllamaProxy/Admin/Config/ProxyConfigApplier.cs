// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json;

using OllamaProxy.Configuration;
using OllamaProxy.Hosting;
using OllamaProxy.Hosting.Cascade;

namespace OllamaProxy.Admin.Config;

/// <summary>
/// The default <see cref="IProxyConfigApplier"/>: writes the desired configuration through the
/// <see cref="IProxyConfigWriter"/>, recycles the inner host through the <see cref="IProxyHostSupervisor"/>,
/// and rolls the file back to its prior content when the recycle's dry-run validation rejects the change. That
/// rollback keeps a rejected configuration from surviving on disk to fail a later restart.
/// </summary>
sealed partial class ProxyConfigApplier : IProxyConfigApplier
{
	private readonly IProxyConfigWriter          mWriter;
	private readonly IWritableProxyConfigFile    mFile;
	private readonly IProxyHostSupervisor        mSupervisor;
	private readonly ILogger<ProxyConfigApplier> mLogger;

	/// <summary>
	/// Initializes a new instance of the <see cref="ProxyConfigApplier"/> class.
	/// </summary>
	/// <param name="writer">Persists the desired configuration as the authoritative proxy section.</param>
	/// <param name="file">The operator file, read for the rollback snapshot and restored on a rejected change.</param>
	/// <param name="supervisor">Recycles the inner host onto the freshly written configuration.</param>
	/// <param name="logger">Records the apply outcome and any rollback.</param>
	/// <exception cref="ArgumentNullException">
	/// <paramref name="writer"/>, <paramref name="file"/>, <paramref name="supervisor"/>, or
	/// <paramref name="logger"/> is <see langword="null"/>.
	/// </exception>
	public ProxyConfigApplier(
		IProxyConfigWriter          writer,
		IWritableProxyConfigFile    file,
		IProxyHostSupervisor        supervisor,
		ILogger<ProxyConfigApplier> logger)
	{
		ArgumentNullException.ThrowIfNull(writer);
		ArgumentNullException.ThrowIfNull(file);
		ArgumentNullException.ThrowIfNull(supervisor);
		ArgumentNullException.ThrowIfNull(logger);

		mWriter = writer;
		mFile = file;
		mSupervisor = supervisor;
		mLogger = logger;
	}

	/// <inheritdoc/>
	public async Task<ApplyResult> ApplyAsync(
		ProxyOptions      desiredState,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(desiredState);

		// Snapshot the current file BEFORE writing, so a rejected recycle can be rolled back. A null snapshot
		// means there was no file yet (first-ever write under the Windows Service); rolling that back deletes
		// the file the write is about to create rather than restoring stale content.
		string? previousContent = await mFile.ReadAsync(cancellationToken).ConfigureAwait(false);

		try
		{
			await mWriter.WriteAsync(desiredState, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
		{
			// The write never landed a valid file, so there is nothing to recycle and nothing to roll back; the
			// previous configuration is still live and unchanged on disk.
			LogWriteFailed(mLogger, exception, mFile.Path);

			return ApplyResult.WriteFailed(exception.Message);
		}

		// The new file is on disk; rebuild and dry-run validate the inner host against it, swapping atomically
		// only if it validates. A rejected candidate leaves the live host serving the previous configuration.
		RecycleResult recycle = await mSupervisor.RecycleAsync(cancellationToken).ConfigureAwait(false);

		if (recycle.Success)
		{
			LogApplied(mLogger, mFile.Path);

			return ApplyResult.Applied;
		}

		// Rejected: the live host kept the previous configuration, but the bad file is still on disk and would be
		// loaded directly (with no dry-run) on the next restart. Restore the snapshot so disk matches what is
		// live. Rollback is best-effort: if it fails the operator at least has the validation errors and a log
		// pointer to fix the file by hand.
		await RollBackAsync(previousContent, cancellationToken).ConfigureAwait(false);

		string errors = string.Join("; ", recycle.ValidationErrors);
		LogRejected(mLogger, mFile.Path, errors);

		return ApplyResult.ValidationRejected(recycle.ValidationErrors);
	}

	/// <summary>
	/// Restores the operator file to its pre-write state after a rejected recycle: rewrites the captured
	/// content, or deletes the file when there was none. Failures are swallowed (logged) because a failed
	/// rollback must not mask the validation errors that are the operator's real, actionable problem.
	/// </summary>
	/// <param name="previousContent">The file content captured before the write, or <see langword="null"/> if absent.</param>
	/// <param name="cancellationToken">A token to cancel the operation.</param>
	/// <returns>A task that completes when the rollback has been attempted.</returns>
	private async Task RollBackAsync(string? previousContent, CancellationToken cancellationToken)
	{
		try
		{
			if (previousContent is null)
			{
				await mFile.DeleteAsync(cancellationToken).ConfigureAwait(false);
			}
			else
			{
				await mFile.WriteAsync(previousContent, cancellationToken).ConfigureAwait(false);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// A failed rollback is unfortunate but not the headline: the live proxy is still serving the previous
			// configuration. Log the path so the operator can reconcile the on-disk file with what is running.
			LogRollbackFailed(mLogger, exception, mFile.Path);
		}
	}

	/// <summary>
	/// Logs a failure to write the proxy configuration file.
	/// </summary>
	/// <param name="logger">The logger to write to.</param>
	/// <param name="exception">The exception thrown during the write.</param>
	/// <param name="path">The path of the configuration file.</param>
	[LoggerMessage(
		Level = LogLevel.Error,
		Message = "Failed to write the proxy configuration to {Path}; the previous configuration is unchanged.")]
	private static partial void LogWriteFailed(ILogger logger, Exception exception, string path);

	/// <summary>
	/// Logs a successful configuration apply and recycle.
	/// </summary>
	/// <param name="logger">The logger to write to.</param>
	/// <param name="path">The path of the configuration file.</param>
	[LoggerMessage(
		Level = LogLevel.Information,
		Message = "Applied a new proxy configuration to {Path} and recycled the inner host.")]
	private static partial void LogApplied(ILogger logger, string path);

	/// <summary>
	/// Logs that a configuration was rejected during recycle and rolled back.
	/// </summary>
	/// <param name="logger">The logger to write to.</param>
	/// <param name="path">The path of the configuration file.</param>
	/// <param name="errors">The validation errors that caused the rejection.</param>
	[LoggerMessage(
		Level = LogLevel.Warning,
		Message = "Proxy configuration rejected during recycle and rolled back at {Path}: {Errors}")]
	private static partial void LogRejected(ILogger logger, string path, string errors);

	/// <summary>
	/// Logs a failure to roll back a rejected configuration.
	/// </summary>
	/// <param name="logger">The logger to write to.</param>
	/// <param name="exception">The exception thrown during rollback.</param>
	/// <param name="path">The path of the configuration file.</param>
	[LoggerMessage(
		Level = LogLevel.Error,
		Message = "Failed to roll back the rejected proxy configuration at {Path}; the running proxy is unaffected, " +
		          "but the on-disk file may need manual correction.")]
	private static partial void LogRollbackFailed(ILogger logger, Exception exception, string path);
}
