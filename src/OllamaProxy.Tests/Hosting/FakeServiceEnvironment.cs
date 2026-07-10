// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Hosting;

namespace OllamaProxy.Tests.Hosting;

/// <summary>
/// A test double for <see cref="IServiceEnvironment"/> that reports a fixed hosting model, so the SCM-specific
/// and foreground branches of the hosting composition can both be driven deterministically without the test
/// process actually running under the Windows Service Control Manager.
/// </summary>
sealed class FakeServiceEnvironment : IServiceEnvironment
{
	/// <summary>
	/// Initializes a new instance of the <see cref="FakeServiceEnvironment"/> class.
	/// </summary>
	/// <param name="isWindowsService">The fixed value <see cref="IsWindowsService"/> reports.</param>
	public FakeServiceEnvironment(bool isWindowsService)
	{
		IsWindowsService = isWindowsService;
	}

	/// <summary>
	/// A pre-built environment reporting the Windows Service (SCM) hosting model.
	/// </summary>
	public static FakeServiceEnvironment Service { get; } = new(isWindowsService: true);

	/// <summary>
	/// A pre-built environment reporting the foreground (console / container) hosting model.
	/// </summary>
	public static FakeServiceEnvironment Foreground { get; } = new(isWindowsService: false);

	/// <inheritdoc/>
	public bool IsWindowsService { get; }
}
