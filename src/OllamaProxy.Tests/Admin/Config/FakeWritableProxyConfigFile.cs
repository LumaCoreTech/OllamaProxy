// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Hosting;

namespace OllamaProxy.Tests.Admin.Config;

/// <summary>
/// An in-memory test double for <see cref="IWritableProxyConfigFile"/> shared by the config writer and
/// applier tests. It holds the file content in a field so a test can seed the "on-disk" state, observe what
/// was written or deleted, and inject a write failure — all without touching the real file system. The
/// writer tests use it as the live write target; the applier tests use it only for the rollback snapshot and
/// restore, because in the applier the actual section rewrite goes through a separate fake writer.
/// </summary>
sealed class FakeWritableProxyConfigFile : IWritableProxyConfigFile
{
	private string? mContent;

	/// <summary>
	/// Initializes a new instance of the <see cref="FakeWritableProxyConfigFile"/> class.
	/// </summary>
	/// <param name="initialContent">
	/// The seeded on-disk content, or <see langword="null"/> to model a file that does not exist yet (the
	/// first-write state under the Windows Service).
	/// </param>
	public FakeWritableProxyConfigFile(string? initialContent = null)
	{
		mContent = initialContent;
	}

	/// <summary>
	/// Gets the fixed absolute path reported by <see cref="Path"/>; its concrete value is irrelevant to the
	/// in-memory behavior and exists only so diagnostics-style assertions have a stable value.
	/// </summary>
	public string Path { get; init; } = @"C:\ProgramData\OllamaProxy\appsettings.json";

	/// <summary>
	/// Gets the current in-memory content, or <see langword="null"/> when the file is modeled as absent.
	/// </summary>
	public string? Content => mContent;

	/// <summary>
	/// Gets the content passed to the most recent successful <see cref="WriteAsync"/> call, or
	/// <see langword="null"/> when no write has succeeded.
	/// </summary>
	public string? LastWrittenContent { get; private set; }

	/// <summary>
	/// Gets the number of times <see cref="ReadAsync"/> was invoked.
	/// </summary>
	public int ReadCount { get; private set; }

	/// <summary>
	/// Gets the number of times <see cref="WriteAsync"/> completed successfully.
	/// </summary>
	public int WriteCount { get; private set; }

	/// <summary>
	/// Gets the number of times <see cref="DeleteAsync"/> was invoked.
	/// </summary>
	public int DeleteCount { get; private set; }

	/// <summary>
	/// Gets or sets an exception that <see cref="WriteAsync"/> faults with instead of writing, used to model a
	/// failed rollback restore (for example a read-only directory).
	/// </summary>
	public Exception? WriteException { get; set; }

	/// <summary>
	/// Gets or sets an exception that <see cref="DeleteAsync"/> faults with instead of deleting, used to model a
	/// failed rollback delete of a first-ever write (for example a locked or read-only file).
	/// </summary>
	public Exception? DeleteException { get; set; }

	/// <inheritdoc/>
	public Task<string?> ReadAsync(CancellationToken cancellationToken)
	{
		ReadCount++;

		return Task.FromResult(mContent);
	}

	/// <inheritdoc/>
	public Task WriteAsync(string content, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(content);

		if (WriteException is not null)
		{
			return Task.FromException(WriteException);
		}

		mContent = content;
		LastWrittenContent = content;
		WriteCount++;

		return Task.CompletedTask;
	}

	/// <inheritdoc/>
	public Task DeleteAsync(CancellationToken cancellationToken)
	{
		if (DeleteException is not null)
		{
			return Task.FromException(DeleteException);
		}

		mContent = null;
		DeleteCount++;

		return Task.CompletedTask;
	}
}
