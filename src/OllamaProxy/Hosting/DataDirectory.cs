// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Hosting;

/// <summary>
/// The default <see cref="IDataDirectory"/>: resolves relative artifact paths against a fixed base
/// directory chosen once at startup. In a foreground run the base is the executable's own directory
/// (<see cref="AppContext.BaseDirectory"/>), so artifacts land beside the binary: the same place for a
/// published console app or a container (where it equals the content root), and under <c>bin/</c> during
/// local <c>dotnet run</c>/IDE debugging rather than scattered into the source tree. Under the Windows
/// Service the base is <c>%ProgramData%\OllamaProxy\data</c>, the writable location the installer
/// provisions and grants the service account modify rights on.
/// </summary>
sealed class DataDirectory : IDataDirectory
{
	/// <summary>
	/// Initializes a new instance of the <see cref="DataDirectory"/> class.
	/// </summary>
	/// <param name="basePath">The absolute base directory relative artifact paths resolve against.</param>
	/// <exception cref="ArgumentException"><paramref name="basePath"/> is null, empty, or whitespace.</exception>
	public DataDirectory(string basePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(basePath);

		BasePath = basePath;
	}

	/// <inheritdoc/>
	public string BasePath { get; }

	/// <inheritdoc/>
	public string Resolve(string path)
	{
		ArgumentNullException.ThrowIfNull(path);

		return Path.IsPathRooted(path) ? path : Path.Combine(BasePath, path);
	}
}
