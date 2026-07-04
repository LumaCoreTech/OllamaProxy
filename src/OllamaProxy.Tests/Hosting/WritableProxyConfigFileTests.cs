// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Hosting;

namespace OllamaProxy.Tests.Hosting;

/// <summary>
/// Tests for <see cref="WritableProxyConfigFile"/>, the default operator-configuration reader/writer bound to a
/// fixed absolute path. Each test writes through the real file system (an isolated temp directory torn down
/// afterwards) so the observable disk state — content written, atomic-swap temp files cleaned up, absence
/// reported as <see langword="null"/> — is asserted for real rather than mocked. The file is organized by member:
/// constructor guards and <see cref="WritableProxyConfigFile.Path"/>, then <c>ReadAsync</c>, <c>WriteAsync</c>,
/// and <c>DeleteAsync</c>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class WritableProxyConfigFileTests : IDisposable
{
	private readonly string mDirectory =
		Path.Combine(Path.GetTempPath(), $"ollamaproxy-writablecfg-{Guid.NewGuid():N}");

	/// <summary>
	/// Removes the isolated temp directory and every file written into it once a test completes.
	/// </summary>
	public void Dispose()
	{
		if (Directory.Exists(mDirectory)) Directory.Delete(mDirectory, recursive: true);
	}

	/// <summary>
	/// Builds an absolute path for a file inside this test's isolated temp directory. The directory itself is
	/// not created, so a test can exercise the "missing directory" paths deliberately.
	/// </summary>
	/// <param name="fileName">The file name to place under the temp directory.</param>
	/// <returns>The absolute path combining the temp directory and <paramref name="fileName"/>.</returns>
	private string PathFor(string fileName) => Path.Combine(mDirectory, fileName);

	/// <summary>
	/// Creates the isolated temp directory so a test that needs an existing parent directory can seed a file
	/// into it.
	/// </summary>
	private void EnsureDirectory() => Directory.CreateDirectory(mDirectory);

	#region Constructor & Path

	/// <summary>
	/// Verifies that the constructor preserves the configured path as the instance's observable
	/// <see cref="WritableProxyConfigFile.Path"/>.
	/// </summary>
	[Fact]
	public void Constructor_WhenPathIsValid_PreservesPath()
	{
		// Arrange
		string path = PathFor("appsettings.json");

		// Act
		WritableProxyConfigFile sut = new(path);

		// Assert
		Assert.Equal(path, sut.Path);
	}

	/// <summary>
	/// Verifies that the constructor rejects a <see langword="null"/> path before any instance state is created.
	/// </summary>
	[Fact]
	public void Constructor_WhenPathIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() => new WritableProxyConfigFile(null!));
		Assert.Equal("path", exception.ParamName);
	}

	/// <summary>
	/// Verifies that the constructor rejects an empty or whitespace path.
	/// </summary>
	/// <param name="path">The invalid path under test.</param>
	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void Constructor_WhenPathIsEmptyOrWhiteSpace_ThrowsArgumentException(string path)
	{
		// Act + Assert
		var exception = Assert.Throws<ArgumentException>(() => new WritableProxyConfigFile(path));
		Assert.Equal("path", exception.ParamName);
	}

	#endregion

	#region ReadAsync()

	/// <summary>
	/// Verifies that <see cref="WritableProxyConfigFile.ReadAsync"/> returns the full text of an existing file.
	/// </summary>
	[Fact]
	public async Task ReadAsync_WhenFileExists_ReturnsContent()
	{
		// Arrange: seed a file with known content into the (created) temp directory.
		EnsureDirectory();
		string path = PathFor("appsettings.json");
		const string content = """{"Proxy":{"ListenPort":11434}}""";
		await File.WriteAllTextAsync(path, content);
		WritableProxyConfigFile sut = new(path);

		// Act
		string? result = await sut.ReadAsync(CancellationToken.None);

		// Assert
		Assert.Equal(content, result);
	}

	/// <summary>
	/// Verifies that <see cref="WritableProxyConfigFile.ReadAsync"/> reports a missing file as
	/// <see langword="null"/>, the "operator copy does not exist yet" state that is normal before the first write.
	/// </summary>
	[Fact]
	public async Task ReadAsync_WhenFileMissing_ReturnsNull()
	{
		// Arrange: the directory exists but the file was never written.
		EnsureDirectory();
		WritableProxyConfigFile sut = new(PathFor("absent.json"));

		// Act
		string? result = await sut.ReadAsync(CancellationToken.None);

		// Assert
		Assert.Null(result);
	}

	/// <summary>
	/// Verifies that <see cref="WritableProxyConfigFile.ReadAsync"/> reports absence as <see langword="null"/>
	/// even when the parent directory itself is missing, not just the file.
	/// </summary>
	[Fact]
	public async Task ReadAsync_WhenDirectoryMissing_ReturnsNull()
	{
		// Arrange: neither the directory nor the file exists.
		WritableProxyConfigFile sut = new(PathFor("absent.json"));

		// Act
		string? result = await sut.ReadAsync(CancellationToken.None);

		// Assert
		Assert.Null(result);
	}

	#endregion

	#region WriteAsync()

	/// <summary>
	/// Verifies that <see cref="WritableProxyConfigFile.WriteAsync"/> persists the content to the target path,
	/// readable back verbatim.
	/// </summary>
	[Fact]
	public async Task WriteAsync_WhenDirectoryExists_WritesContent()
	{
		// Arrange
		EnsureDirectory();
		string path = PathFor("appsettings.json");
		const string content = """{"Proxy":{"ListenPort":11434}}""";
		WritableProxyConfigFile sut = new(path);

		// Act
		await sut.WriteAsync(content, CancellationToken.None);

		// Assert
		Assert.Equal(content, await File.ReadAllTextAsync(path));
	}

	/// <summary>
	/// Verifies that <see cref="WritableProxyConfigFile.WriteAsync"/> creates the parent directory when it is
	/// missing, so a foreground first run against a freshly cleaned content root does not fail.
	/// </summary>
	[Fact]
	public async Task WriteAsync_WhenDirectoryMissing_CreatesDirectoryAndWritesContent()
	{
		// Arrange: the parent directory does not exist yet — WriteAsync must create it defensively.
		string path = PathFor("appsettings.json");
		const string content = """{"Proxy":{"ListenPort":11435}}""";
		WritableProxyConfigFile sut = new(path);

		// Act
		await sut.WriteAsync(content, CancellationToken.None);

		// Assert
		Assert.True(Directory.Exists(mDirectory));
		Assert.Equal(content, await File.ReadAllTextAsync(path));
	}

	/// <summary>
	/// Verifies that <see cref="WritableProxyConfigFile.WriteAsync"/> replaces the existing file's content on a
	/// second write, the recycle case where the operator copy is rewritten.
	/// </summary>
	[Fact]
	public async Task WriteAsync_WhenFileExists_OverwritesContent()
	{
		// Arrange: an initial file that the second write must fully replace.
		EnsureDirectory();
		string path = PathFor("appsettings.json");
		WritableProxyConfigFile sut = new(path);
		await sut.WriteAsync("""{"Proxy":{"ListenPort":1}}""", CancellationToken.None);
		const string updated = """{"Proxy":{"ListenPort":2}}""";

		// Act
		await sut.WriteAsync(updated, CancellationToken.None);

		// Assert
		Assert.Equal(updated, await File.ReadAllTextAsync(path));
	}

	/// <summary>
	/// Verifies that <see cref="WritableProxyConfigFile.WriteAsync"/> leaves no <c>.tmp</c> sibling behind after
	/// a successful write, so the atomic write-then-rename strategy litters no partial-write files.
	/// </summary>
	[Fact]
	public async Task WriteAsync_WhenSuccessful_LeavesNoTempFile()
	{
		// Arrange
		EnsureDirectory();
		string path = PathFor("appsettings.json");
		WritableProxyConfigFile sut = new(path);

		// Act
		await sut.WriteAsync("""{"Proxy":{}}""", CancellationToken.None);

		// Assert: the target exists and is the only file — the temp sibling was renamed onto it, not left behind.
		Assert.Single(Directory.GetFiles(mDirectory));
		Assert.Empty(Directory.GetFiles(mDirectory, "*.tmp"));
	}

	/// <summary>
	/// Verifies that <see cref="WritableProxyConfigFile.WriteAsync"/> rejects <see langword="null"/> content
	/// before touching the file system.
	/// </summary>
	[Fact]
	public async Task WriteAsync_WhenContentIsNull_ThrowsArgumentNullException()
	{
		// Arrange
		WritableProxyConfigFile sut = new(PathFor("appsettings.json"));

		// Act + Assert
		var exception =
			await Assert.ThrowsAsync<ArgumentNullException>(() => sut.WriteAsync(null!, CancellationToken.None));
		Assert.Equal("content", exception.ParamName);
	}

	#endregion

	#region DeleteAsync()

	/// <summary>
	/// Verifies that <see cref="WritableProxyConfigFile.DeleteAsync"/> removes an existing file.
	/// </summary>
	[Fact]
	public async Task DeleteAsync_WhenFileExists_RemovesFile()
	{
		// Arrange: seed a file to be deleted.
		EnsureDirectory();
		string path = PathFor("appsettings.json");
		await File.WriteAllTextAsync(path, "{}");
		WritableProxyConfigFile sut = new(path);

		// Act
		await sut.DeleteAsync(CancellationToken.None);

		// Assert
		Assert.False(File.Exists(path));
	}

	/// <summary>
	/// Verifies that <see cref="WritableProxyConfigFile.DeleteAsync"/> is a no-op when the file is already
	/// absent, the "already absent" contract that needs no existence check.
	/// </summary>
	[Fact]
	public async Task DeleteAsync_WhenFileMissing_CompletesWithoutThrowing()
	{
		// Arrange: the directory exists but the file does not.
		EnsureDirectory();
		string path = PathFor("absent.json");
		WritableProxyConfigFile sut = new(path);

		// Act
		await sut.DeleteAsync(CancellationToken.None);

		// Assert: no throw, and the file remains absent.
		Assert.False(File.Exists(path));
	}

	/// <summary>
	/// Verifies that <see cref="WritableProxyConfigFile.DeleteAsync"/> honors a canceled token by throwing before
	/// touching the file system, leaving an existing file in place.
	/// </summary>
	[Fact]
	public async Task DeleteAsync_WhenCancelled_ThrowsAndLeavesFileInPlace()
	{
		// Arrange: an existing file plus an already-canceled token.
		EnsureDirectory();
		string path = PathFor("appsettings.json");
		await File.WriteAllTextAsync(path, "{}");
		WritableProxyConfigFile sut = new(path);
		using CancellationTokenSource cts = new();
		await cts.CancelAsync();

		// Act + Assert: cancellation is observed before the delete, so the file survives.
		await Assert.ThrowsAsync<OperationCanceledException>(() => sut.DeleteAsync(cts.Token));
		Assert.True(File.Exists(path));
	}

	#endregion
}
