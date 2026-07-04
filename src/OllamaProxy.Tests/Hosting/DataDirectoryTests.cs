// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Hosting;

namespace OllamaProxy.Tests.Hosting;

/// <summary>
/// Tests for <see cref="DataDirectory"/>, the fixed-base path resolver used for proxy runtime artifacts.
/// </summary>
[Trait("Category", "Unit")]
public sealed class DataDirectoryTests
{
	private const string BasePathSegment       = "data";
	private const string ExternalPathSegment   = "external";
	private const string TraceDirectorySegment = "traces";
	private const string TraceFileName         = "flow.json";

	#region Constructor

	/// <summary>
	/// Verifies that the constructor preserves the configured base path as the resolver's observable base path.
	/// </summary>
	[Fact]
	public void Constructor_WhenBasePathIsValid_PreservesBasePath()
	{
		// Arrange
		string basePath = CreateRootedPath(BasePathSegment);

		// Act
		DataDirectory sut = new(basePath);

		// Assert
		Assert.Equal(basePath, sut.BasePath);
	}

	/// <summary>
	/// Verifies that the constructor rejects a <see langword="null"/> base path before any resolver state is created.
	/// </summary>
	[Fact]
	public void Constructor_WhenBasePathIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() => new DataDirectory(null!));
		Assert.Equal("basePath", exception.ParamName);
	}

	/// <summary>
	/// Verifies that the constructor rejects an empty or whitespace base path.
	/// </summary>
	/// <param name="basePath">
	/// The invalid base path under test.
	/// </param>
	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void Constructor_WhenBasePathIsEmptyOrWhiteSpace_ThrowsArgumentException(string basePath)
	{
		// Act + Assert
		var exception = Assert.Throws<ArgumentException>(() => new DataDirectory(basePath));
		Assert.Equal("basePath", exception.ParamName);
	}

	#endregion

	#region Resolve()

	/// <summary>
	/// Verifies that <see cref="DataDirectory.Resolve"/> combines relative artifact paths with the configured base
	/// path.
	/// </summary>
	[Fact]
	public void Resolve_WhenPathIsRelative_ReturnsPathUnderBasePath()
	{
		// Arrange
		string basePath = CreateRootedPath(BasePathSegment);
		DataDirectory sut = new(basePath);
		string relativePath = Path.Combine(TraceDirectorySegment, TraceFileName);

		// Act
		string result = sut.Resolve(relativePath);

		// Assert
		Assert.Equal(Path.Combine(basePath, relativePath), result);
	}

	/// <summary>
	/// Verifies that <see cref="DataDirectory.Resolve"/> passes rooted paths through unchanged so callers can opt out
	/// of base-path resolution for already absolute destinations.
	/// </summary>
	[Fact]
	public void Resolve_WhenPathIsRooted_ReturnsPathUnchanged()
	{
		// Arrange
		DataDirectory sut = new(CreateRootedPath(BasePathSegment));
		string rootedPath = CreateRootedPath(ExternalPathSegment, TraceFileName);

		// Act
		string result = sut.Resolve(rootedPath);

		// Assert
		Assert.Equal(rootedPath, result);
	}

	/// <summary>
	/// Verifies that <see cref="DataDirectory.Resolve"/> rejects a <see langword="null"/> artifact path.
	/// </summary>
	[Fact]
	public void Resolve_WhenPathIsNull_ThrowsArgumentNullException()
	{
		// Arrange
		DataDirectory sut = new(CreateRootedPath(BasePathSegment));

		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() => sut.Resolve(null!));
		Assert.Equal("path", exception.ParamName);
	}

	#endregion

	/// <summary>
	/// Creates a rooted path from neutral path segments without touching the file system.
	/// </summary>
	/// <param name="segments">
	/// The path segments to append under the current temporary directory.
	/// </param>
	/// <returns>
	/// A rooted path built from the temporary directory and the supplied path segments.
	/// </returns>
	private static string CreateRootedPath(params string[] segments) =>
		Path.GetFullPath(Path.Combine([Path.GetTempPath(), .. segments]));
}
