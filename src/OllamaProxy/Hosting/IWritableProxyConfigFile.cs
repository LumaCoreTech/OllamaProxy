// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Hosting;

/// <summary>
/// The single operator-editable proxy configuration file the admin surface persists changes to. The
/// abstraction exists because that file's location depends on how the proxy is hosted: a foreground run
/// (console or container) keeps it beside the application as <c>appsettings.json</c> under the content
/// root, whereas the Windows Service runs out of a read-only install directory and must instead write
/// the operator copy under <c>%ProgramData%\OllamaProxy\appsettings.json</c>, the writable location the
/// installer provisions and grants the service account modify rights on. Consumers read and rewrite
/// "the operator config" through this seam rather than hard-coding a path, so the same admin code writes
/// to the correct, writable place in both hosting models, and always the very file the running proxy
/// reads back on its next recycle.
/// </summary>
/// <remarks>
/// This is the persistence source of record, deliberately distinct from the bound, env-merged
/// <see cref="Microsoft.Extensions.Options.IOptionsMonitor{TOptions}"/> view: reading here yields the
/// raw on-disk text with no environment-variable overlay, so a secret supplied only through an
/// environment variable can never be observed (and therefore never be written back) by a consumer that
/// persists what it reads.
/// </remarks>
interface IWritableProxyConfigFile
{
	/// <summary>
	/// Gets the absolute path of the operator configuration file, for diagnostics and operator-facing log
	/// messages. The file is not guaranteed to exist: under the Windows Service the operator copy is
	/// created on first write, before which <see cref="ReadAsync"/> reports its absence.
	/// </summary>
	string Path { get; }

	/// <summary>
	/// Reads the raw on-disk content of the operator configuration file, or reports its absence. The text
	/// is returned verbatim with no environment-variable overlay, so it is the safe persistence source: a
	/// secret supplied only through an environment variable is not present here and cannot leak into a
	/// rewrite.
	/// </summary>
	/// <param name="cancellationToken">A token to cancel the operation.</param>
	/// <returns>
	/// The file's content, or <see langword="null"/> when the file does not exist (a valid state under the
	/// Windows Service before the operator copy has first been written).
	/// </returns>
	/// <exception cref="IOException">The file exists but could not be read.</exception>
	Task<string?> ReadAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Writes the operator configuration file atomically, replacing any existing content. The write is
	/// all-or-nothing: a concurrent reader (the inner host rebuilding during a recycle, or another admin
	/// read) observes either the complete previous content or the complete new content, never a partially
	/// written file. Any directories on the path that do not yet exist are created first.
	/// </summary>
	/// <param name="content">The complete file content to persist.</param>
	/// <param name="cancellationToken">A token to cancel the operation.</param>
	/// <returns>A task that completes when the content has been durably written.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="content"/> is <see langword="null"/>.</exception>
	/// <exception cref="IOException">
	/// The file could not be written (for example the directory is read-only or the disk is
	/// full).
	/// </exception>
	Task WriteAsync(string content, CancellationToken cancellationToken);

	/// <summary>
	/// Deletes the operator configuration file if it exists; does nothing when it is already absent. This
	/// supports a faithful rollback of a first-ever write: when no file existed before a change, undoing that
	/// change means removing the file the write created, not leaving an empty or stale one behind.
	/// </summary>
	/// <param name="cancellationToken">A token to cancel the operation.</param>
	/// <returns>A task that completes when the file has been removed or confirmed absent.</returns>
	/// <exception cref="IOException">The file exists but could not be deleted.</exception>
	Task DeleteAsync(CancellationToken cancellationToken);
}
