// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Microsoft.Extensions.Logging.Abstractions;

using OllamaProxy.Admin.Config;
using OllamaProxy.Hosting.Cascade;

namespace OllamaProxy.Tests.Admin.Config;

/// <summary>
/// Tests for <see cref="ProxyConfigApplier"/>: write, recycle, and roll back on reject so disk never keeps a
/// rejected config.
/// </summary>
/// <remarks>
/// These tests follow <c>ApplyAsync</c> through its three terminal outcomes, escalating from the happy path to
/// the failure modes that justify the rollback machinery:
/// <list type="number">
///     <item>
///         <description>
///         Applied: the writer persists and the recycle validates, so the change goes live
///         (WhenWriteSucceedsAndRecycleSucceeds).
///         </description>
///     </item>
///     <item>
///         <description>
///         ValidationRejected — the property the whole design exists for: the recycle rejects the candidate, so
///         the previously written snapshot is restored over the rejected file (WhenRecycleRejects_RestoresPreviousFile)
///         and a first-ever write is rolled back by DELETING the file rather than leaving a rejected one behind
///         (WhenRecycleRejects_AndNoPreviousFile_DeletesFile). A failed rollback is swallowed so the operator
///         still gets the validation errors (WhenRollbackFails_StillReportsValidationErrors).
///         </description>
///     </item>
///     <item>
///         <description>
///         WriteFailed: the write itself faults, so no recycle is attempted and there is nothing to roll back
///         (WhenWriteFails_DoesNotRecycle).
///         </description>
///     </item>
/// </list>
/// Constructor guards and the null-desired-state guard close the file. For the section-rewrite writer this
/// orchestration drives, see <see cref="ProxyConfigWriterTests"/>; for the validated recycle it consumes, see
/// <see cref="OllamaProxy.Tests.Hosting.Cascade.ProxyHostSupervisorTests"/>.
/// </remarks>
[Trait("Category", "Unit")]
public sealed partial class ProxyConfigApplierTests
{
	#region ApplyAsync

	// --- 1. Applied ---

	/// <summary>
	/// Verifies that when the write succeeds and the recycle validates, <see cref="ProxyConfigApplier.ApplyAsync"/>
	/// reports success, writes exactly once, recycles exactly once, and never rolls back.
	/// </summary>
	[Fact]
	public async Task ApplyAsync_WhenWriteSucceedsAndRecycleSucceeds_ReportsAppliedAndDoesNotRollBack()
	{
		// Arrange: a file seeded with prior content, a writer that stamps new content, and a validating recycle.
		FakeWritableProxyConfigFile file = new(initialContent: "{ \"previous\": true }");
		FakeWriter writer = new(fileToMutateOnWrite: file);
		FakeSupervisor supervisor = new(RecycleResult.Succeeded);
		ProxyConfigApplier sut = CreateSut(writer, file, supervisor);

		// Act
		ApplyResult result = await sut.ApplyAsync(
			                     DesiredState(),
			                     CancellationToken.None);

		// Assert: applied outcome, one write, one recycle.
		Assert.Equal(ApplyOutcome.Applied, result.Outcome);
		Assert.True(result.Success);
		Assert.Empty(result.Errors);
		Assert.Equal(1, writer.WriteCount);
		Assert.Equal(1, supervisor.RecycleCount);

		// The new content stands; no rollback restore or delete happened. The single write is the writer's own
		// stamp (DeleteCount stays 0, and the file still holds the written marker, not the previous snapshot).
		Assert.Equal(0, file.DeleteCount);
		Assert.Equal(FakeWriter.WrittenContent, file.Content);
	}

	// --- 2. ValidationRejected ---

	/// <summary>
	/// Verifies the core safety property: when the recycle rejects the candidate, the pre-write snapshot is
	/// restored over the rejected file and the rejection's validation errors are surfaced, so a rejected
	/// configuration never survives on disk to fail a later restart.
	/// </summary>
	[Fact]
	public async Task ApplyAsync_WhenRecycleRejects_RestoresPreviousFile()
	{
		// Arrange: a file with known previous content; the writer overwrites it; the recycle then rejects.
		const string previous = "{ \"previous\": true }";
		FakeWritableProxyConfigFile file = new(initialContent: previous);
		FakeWriter writer = new(fileToMutateOnWrite: file);
		FakeSupervisor supervisor = new(RecycleResult.Failed(["ApiKey is required.", "Unknown backend 'x'."]));
		ProxyConfigApplier sut = CreateSut(writer, file, supervisor);

		// Act
		ApplyResult result = await sut.ApplyAsync(
			                     DesiredState(),
			                     CancellationToken.None);

		// Assert: rejected outcome carrying the recycle's errors verbatim.
		Assert.Equal(ApplyOutcome.ValidationRejected, result.Outcome);
		Assert.False(result.Success);
		Assert.Equal(["ApiKey is required.", "Unknown backend 'x'."], result.Errors);

		// The rollback restored the previous snapshot over the rejected write (the file no longer holds the
		// writer's marker), and the file was restored by writing — not deleting — because a previous file existed.
		Assert.Equal(previous, file.Content);
		Assert.Equal(0, file.DeleteCount);
	}

	/// <summary>
	/// Verifies that when there was no file before the change (the first-write state under the Windows Service)
	/// and the recycle rejects, the rollback DELETES the file the write created rather than leaving a rejected
	/// configuration on disk.
	/// </summary>
	[Fact]
	public async Task ApplyAsync_WhenRecycleRejects_AndNoPreviousFile_DeletesFile()
	{
		// Arrange: NO initial content (absent file); the writer creates it; the recycle then rejects.
		FakeWritableProxyConfigFile file = new(initialContent: null);
		FakeWriter writer = new(fileToMutateOnWrite: file);
		FakeSupervisor supervisor = new(RecycleResult.Failed(["bad config"]));
		ProxyConfigApplier sut = CreateSut(writer, file, supervisor);

		// Act
		ApplyResult result = await sut.ApplyAsync(
			                     DesiredState(),
			                     CancellationToken.None);

		// Assert: rejected, and the rollback removed the file (faithful undo of a first-ever write) so nothing
		// rejected lingers on disk.
		Assert.Equal(ApplyOutcome.ValidationRejected, result.Outcome);
		Assert.Equal(1, file.DeleteCount);
		Assert.Null(file.Content);
	}

	/// <summary>
	/// Verifies that a rollback that itself fails does not mask the validation errors: the result still reports
	/// <see cref="ApplyOutcome.ValidationRejected"/> with the recycle's errors, because the live proxy is
	/// unaffected and those errors are the operator's actionable problem.
	/// </summary>
	[Fact]
	public async Task ApplyAsync_WhenRollbackFails_StillReportsValidationErrors()
	{
		// Arrange: a previous file exists, the recycle rejects, and the restore write then faults (e.g. the
		// directory turned read-only). The applier must swallow the rollback failure and still surface the
		// validation errors.
		FakeWritableProxyConfigFile file = new(initialContent: "{ \"previous\": true }");
		FakeWriter writer = new(); // does not touch the file; the restore path is what we fault
		FakeSupervisor supervisor = new(RecycleResult.Failed(["rejected reason"]));
		ProxyConfigApplier sut = CreateSut(writer, file, supervisor);

		// The restore goes through the file's WriteAsync; arm it to throw on that call.
		file.WriteException = new IOException("config directory is read-only");

		// Act
		ApplyResult result = await sut.ApplyAsync(
			                     DesiredState(),
			                     CancellationToken.None);

		// Assert: the validation errors win over the swallowed rollback failure.
		Assert.Equal(ApplyOutcome.ValidationRejected, result.Outcome);
		Assert.Equal(["rejected reason"], result.Errors);
	}

	// --- 3. WriteFailed ---

	/// <summary>
	/// Verifies that when the write itself faults, the applier reports <see cref="ApplyOutcome.WriteFailed"/>,
	/// never attempts a recycle, and performs no rollback — there is nothing on disk to undo.
	/// </summary>
	[Fact]
	public async Task ApplyAsync_WhenWriteFails_DoesNotRecycle()
	{
		// Arrange: the writer faults; the supervisor would record a recycle if (wrongly) called.
		FakeWritableProxyConfigFile file = new(initialContent: "{ \"previous\": true }");
		FakeWriter writer = new(writeException: new IOException("disk full"));
		FakeSupervisor supervisor = new(RecycleResult.Succeeded);
		ProxyConfigApplier sut = CreateSut(writer, file, supervisor);

		// Act
		ApplyResult result = await sut.ApplyAsync(
			                     DesiredState(),
			                     CancellationToken.None);

		// Assert: write-failed outcome carrying the exception message, and the recycle was never attempted.
		Assert.Equal(ApplyOutcome.WriteFailed, result.Outcome);
		Assert.False(result.Success);
		Assert.Equal(["disk full"], result.Errors);
		Assert.Equal(0, supervisor.RecycleCount);
	}

	// --- Argument guards ---

	/// <summary>
	/// Verifies that <see cref="ProxyConfigApplier.ApplyAsync"/> rejects a <see langword="null"/> desired state.
	/// </summary>
	[Fact]
	public async Task ApplyAsync_WhenDesiredStateIsNull_ThrowsArgumentNullException()
	{
		// Arrange
		FakeWritableProxyConfigFile file = new();
		FakeWriter writer = new();
		FakeSupervisor supervisor = new(RecycleResult.Succeeded);
		ProxyConfigApplier sut = CreateSut(writer, file, supervisor);

		// Act + Assert
		var exception = await Assert.ThrowsAsync<ArgumentNullException>(() => sut.ApplyAsync(
			                null!,
			                CancellationToken.None));
		Assert.Equal("desiredState", exception.ParamName);
	}

	/// <summary>
	/// Verifies that the constructor rejects a <see langword="null"/> writer.
	/// </summary>
	[Fact]
	public void Constructor_WhenWriterIsNull_ThrowsArgumentNullException()
	{
		// Arrange
		FakeWritableProxyConfigFile file = new();
		FakeSupervisor supervisor = new(RecycleResult.Succeeded);

		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() => new ProxyConfigApplier(
			null!,
			file,
			supervisor,
			NullLogger<ProxyConfigApplier>.Instance));
		Assert.Equal("writer", exception.ParamName);
	}

	/// <summary>
	/// Verifies that the constructor rejects a <see langword="null"/> file.
	/// </summary>
	[Fact]
	public void Constructor_WhenFileIsNull_ThrowsArgumentNullException()
	{
		// Arrange
		FakeWriter writer = new();
		FakeSupervisor supervisor = new(RecycleResult.Succeeded);

		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() => new ProxyConfigApplier(
			writer,
			null!,
			supervisor,
			NullLogger<ProxyConfigApplier>.Instance));
		Assert.Equal("file", exception.ParamName);
	}

	/// <summary>
	/// Verifies that the constructor rejects a <see langword="null"/> supervisor.
	/// </summary>
	[Fact]
	public void Constructor_WhenSupervisorIsNull_ThrowsArgumentNullException()
	{
		// Arrange
		FakeWriter writer = new();
		FakeWritableProxyConfigFile file = new();

		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() => new ProxyConfigApplier(
			writer,
			file,
			null!,
			NullLogger<ProxyConfigApplier>.Instance));
		Assert.Equal("supervisor", exception.ParamName);
	}

	/// <summary>
	/// Verifies that the constructor rejects a <see langword="null"/> logger.
	/// </summary>
	[Fact]
	public void Constructor_WhenLoggerIsNull_ThrowsArgumentNullException()
	{
		// Arrange
		FakeWriter writer = new();
		FakeWritableProxyConfigFile file = new();
		FakeSupervisor supervisor = new(RecycleResult.Succeeded);

		// Act + Assert
		var exception =
			Assert.Throws<ArgumentNullException>(() => new ProxyConfigApplier(
				writer,
				file,
				supervisor,
				null!));
		Assert.Equal("logger", exception.ParamName);
	}

	#endregion
}
