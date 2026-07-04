// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Hosting;

/// <summary>
/// Resolves the base directory the proxy writes its mutable, runtime-produced artifacts beneath,
/// currently the request traces. (The operator-editable <c>appsettings.json</c> is <i>not</i> resolved
/// here: it must sit where the configuration system reloads it from, so it has its own seam,
/// <see cref="IWritableProxyConfigFile"/>.) The abstraction exists because the trace location depends on
/// how the proxy is hosted: a foreground run (console or container) writes beside the executable
/// (<see cref="AppContext.BaseDirectory"/>), whereas the Windows Service runs out of a read-only Program
/// Files install and must write under <c>%ProgramData%\OllamaProxy\data</c> instead. Consumers resolve
/// their configured (possibly relative) path through <see cref="Resolve"/> rather than hard-coding a
/// base, so the same code writes to the correct, writable place in every hosting model.
/// </summary>
interface IDataDirectory
{
	/// <summary>
	/// Gets the absolute base directory that relative artifact paths are resolved against.
	/// </summary>
	string BasePath { get; }

	/// <summary>
	/// Resolves a configured path to an absolute one. An already-rooted path is honored verbatim (so an
	/// operator can still pin an explicit absolute location), while a relative path is combined with
	/// <see cref="BasePath"/>.
	/// </summary>
	/// <param name="path">The configured path, either relative to <see cref="BasePath"/> or absolute.</param>
	/// <returns>The absolute path the artifact should be written to.</returns>
	string Resolve(string path);
}
