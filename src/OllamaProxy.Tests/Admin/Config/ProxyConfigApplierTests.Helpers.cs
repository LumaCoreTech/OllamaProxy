// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Microsoft.Extensions.Logging.Abstractions;

using OllamaProxy.Admin.Config;
using OllamaProxy.Configuration;
using OllamaProxy.Core;
using OllamaProxy.Hosting.Cascade;

namespace OllamaProxy.Tests.Admin.Config;

/// <summary>
/// Shared fakes and setup helpers for <see cref="ProxyConfigApplierTests"/>. The fakes stand in for the
/// config writer and the inner-host supervisor so the applier's write-then-recycle-then-rollback orchestration
/// can be exercised without a real file system or a real host. The in-memory file double
/// (<see cref="FakeWritableProxyConfigFile"/>) is shared with the writer tests and here provides only the
/// rollback snapshot and restore surface, because the section rewrite itself goes through <see cref="FakeWriter"/>.
/// </summary>
public sealed partial class ProxyConfigApplierTests
{
	/// <summary>
	/// Builds a trivially valid desired state; its contents are irrelevant to the applier, which never inspects
	/// them and only forwards them to the writer.
	/// </summary>
	/// <returns>A desired <see cref="ProxyOptions"/> state.</returns>
	private static ProxyOptions DesiredState() => new()
	{
		Backends = { ["default"] = new BackendOptions { BaseUrl = "https://api.example.com/v1", ApiKey = "key-12345" } }
	};

	/// <summary>
	/// Creates an applier wired to the supplied fakes and a no-op logger.
	/// </summary>
	/// <param name="writer">The config writer double.</param>
	/// <param name="file">The in-memory file double providing the rollback snapshot and restore.</param>
	/// <param name="supervisor">The supervisor double providing the recycle outcome.</param>
	/// <returns>A configured <see cref="ProxyConfigApplier"/> ready to drive in a test.</returns>
	private static ProxyConfigApplier CreateSut(
		FakeWriter                  writer,
		FakeWritableProxyConfigFile file,
		FakeSupervisor              supervisor) => new(
		writer,
		file,
		supervisor,
		NullLogger<ProxyConfigApplier>.Instance);

	/// <summary>
	/// A test double for <see cref="IProxyConfigWriter"/> that records its invocation and can be configured to
	/// either succeed (mutating the shared file double so a later rollback has something to restore over) or to
	/// fault, modeling a failed write.
	/// </summary>
	internal sealed class FakeWriter : IProxyConfigWriter
	{
		/// <summary>
		/// The marker content a successful write stamps onto the file, so the post-write on-disk state is
		/// distinguishable from the pre-write snapshot a rollback restores.
		/// </summary>
		public const string WrittenContent = """{ "written": true }""";

		private readonly FakeWritableProxyConfigFile? mFileToMutateOnWrite;
		private readonly Exception?                   mWriteException;

		/// <summary>
		/// Initializes a new instance of the <see cref="FakeWriter"/> class.
		/// </summary>
		/// <param name="fileToMutateOnWrite">
		/// The file double to stamp with <see cref="WrittenContent"/> on a successful write, so the applier's
		/// pre-write snapshot and post-write state differ; <see langword="null"/> to leave the file untouched.
		/// </param>
		/// <param name="writeException">
		/// The exception the write should fault with, or <see langword="null"/> for a write that succeeds.
		/// </param>
		public FakeWriter(
			FakeWritableProxyConfigFile? fileToMutateOnWrite = null,
			Exception?                   writeException      = null)
		{
			mFileToMutateOnWrite = fileToMutateOnWrite;
			mWriteException = writeException;
		}

		/// <summary>
		/// Gets the number of times <see cref="WriteAsync"/> was invoked.
		/// </summary>
		public int WriteCount { get; private set; }

		/// <inheritdoc/>
		public Task WriteAsync(
			ProxyOptions      desiredState,
			CancellationToken cancellationToken)
		{
			WriteCount++;

			if (mWriteException is not null)
			{
				throw mWriteException;
			}

			// Stamp the file so the new on-disk content differs from the pre-write snapshot, modeling the real
			// writer's final mFile.WriteAsync. This is what lets a rollback assertion prove the snapshot (not the
			// new content) was restored.
			if (mFileToMutateOnWrite is not null)
			{
				return mFileToMutateOnWrite.WriteAsync(WrittenContent, cancellationToken);
			}

			return Task.CompletedTask;
		}
	}

	/// <summary>
	/// A test double for <see cref="IProxyHostSupervisor"/> that returns a scripted recycle outcome and records
	/// how many times the recycle was requested. The hosted-service lifecycle members are never exercised by the
	/// applier and throw if touched, signaling an unexpected new dependency rather than silently passing.
	/// </summary>
	internal sealed class FakeSupervisor : IProxyHostSupervisor
	{
		private readonly RecycleResult mRecycleResult;

		/// <summary>
		/// Initializes a new instance of the <see cref="FakeSupervisor"/> class.
		/// </summary>
		/// <param name="recycleResult">The outcome <see cref="RecycleAsync"/> should return.</param>
		public FakeSupervisor(RecycleResult recycleResult) => mRecycleResult = recycleResult;

		/// <summary>
		/// Gets the number of times <see cref="RecycleAsync"/> was invoked.
		/// </summary>
		public int RecycleCount { get; private set; }

		/// <inheritdoc/>
		public bool IsInnerHostActive =>
			throw new NotSupportedException("IsInnerHostActive is not used by the applier under test.");

		/// <inheritdoc/>
		public IReadOnlyList<RegisteredModel>? GetLiveModels() =>
			throw new NotSupportedException("GetLiveModels is not used by the applier under test.");

		/// <inheritdoc/>
		public Task<RecycleResult> RecycleAsync(CancellationToken cancellationToken)
		{
			RecycleCount++;

			return Task.FromResult(mRecycleResult);
		}

		/// <inheritdoc/>
		public Task StartAsync(CancellationToken cancellationToken) =>
			throw new NotSupportedException("StartAsync is not used by the applier under test.");

		/// <inheritdoc/>
		public Task StopAsync(CancellationToken cancellationToken) =>
			throw new NotSupportedException("StopAsync is not used by the applier under test.");
	}
}
