// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Hosting;

/// <summary>
/// The default <see cref="IWritableProxyConfigFile"/>: reads and rewrites the operator configuration at a
/// fixed absolute path chosen once at startup. In a foreground run that path is
/// <c>&lt;ContentRoot&gt;\appsettings.json</c> (preserving the "files live beside the app" behavior for
/// console and container hosting); under the Windows Service it is
/// <c>%ProgramData%\OllamaProxy\appsettings.json</c>, the writable operator copy the installer provisions
/// outside the read-only install directory. The caller resolves which path applies (see
/// <see cref="CascadeHostingExtensions"/>) and supplies it here, so this type carries no hosting-model
/// knowledge of its own.
/// </summary>
sealed class WritableProxyConfigFile : IWritableProxyConfigFile
{
	/// <summary>
	/// Initializes a new instance of the <see cref="WritableProxyConfigFile"/> class.
	/// </summary>
	/// <param name="path">The absolute path of the operator configuration file this instance reads and writes.</param>
	/// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/>, empty, or white-space.</exception>
	public WritableProxyConfigFile(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);

		Path = path;
	}

	/// <inheritdoc/>
	public string Path { get; }

	/// <inheritdoc/>
	public async Task<string?> ReadAsync(CancellationToken cancellationToken)
	{
		try
		{
			return await File.ReadAllTextAsync(Path, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
		{
			// "The operator copy does not exist yet" is a normal state under the Windows Service before the
			// first write, not an error. Reading it back as absence (rather than catching the broader
			// IOException) keeps a genuine read fault, such as a locked or unreadable file, propagating.
			return null;
		}
	}

	/// <inheritdoc/>
	public async Task WriteAsync(string content, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(content);

		// Under the Windows Service the ProgramData configuration folder is provisioned by the installer, but
		// creating it defensively keeps a foreground first run (or a freshly cleaned content root) from failing
		// on a missing directory. CreateDirectory is idempotent when the directory already exists.
		string? directory = System.IO.Path.GetDirectoryName(Path);
		if (!string.IsNullOrEmpty(directory))
		{
			Directory.CreateDirectory(directory);
		}

		// Write the new content to a sibling temp file first, then atomically replace the target. A concurrent
		// reader (most importantly the inner host rebuilding its configuration during a recycle) therefore
		// observes either the complete old file or the complete new file, never a half-written one. The temp
		// file shares the target's directory to guarantee the same volume, which is what lets File.Move act as
		// an atomic rename rather than a non-atomic copy.
		string tempPath = $"{Path}.{Guid.NewGuid():N}.tmp";
		try
		{
			await File.WriteAllTextAsync(tempPath, content, cancellationToken).ConfigureAwait(false);
			File.Move(tempPath, Path, overwrite: true);
		}
		finally
		{
			// On success the temp file no longer exists (it was renamed onto the target); on any failure before
			// the move it lingers, so delete it to leave no partial-write litter behind. Existence is checked
			// because Move already consumed it on the success path.
			if (File.Exists(tempPath))
			{
				File.Delete(tempPath);
			}
		}
	}

	/// <inheritdoc/>
	public Task DeleteAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		// File.Delete is a no-op when the path does not exist, which is exactly the "already absent" contract;
		// no existence check is needed. Deletion is synchronous in the BCL, so the completed task is returned
		// rather than introducing a pointless thread hop.
		File.Delete(Path);

		return Task.CompletedTask;
	}
}
