// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Microsoft.Extensions.Hosting.WindowsServices;

namespace OllamaProxy.Hosting;

/// <summary>
/// The production <see cref="IServiceEnvironment"/>: delegates the Service Control Manager probe to the
/// framework's <see cref="WindowsServiceHelpers.IsWindowsService"/>. Stateless, so a single shared
/// <see cref="Instance"/> serves every call site; the hosting extensions fall back to it when no environment
/// is supplied, keeping production behavior identical to a direct static call while leaving a seam for tests.
/// </summary>
sealed class WindowsServiceEnvironment : IServiceEnvironment
{
	/// <summary>
	/// The shared, stateless instance used as the default environment throughout the hosting composition.
	/// </summary>
	public static WindowsServiceEnvironment Instance { get; } = new();

	/// <inheritdoc/>
	public bool IsWindowsService => WindowsServiceHelpers.IsWindowsService();
}
