// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Hosting;

namespace OllamaProxy.Tests.Hosting;

/// <summary>
/// Tests for <see cref="ChassisOptions"/> default values. The run <see cref="ChassisOptions.Mode"/> governs
/// whether a failure to start the inner proxy host is fatal, so its default (<see cref="HostMode.Auto"/>) is a
/// deliberate behavioral contract: a regression flipping it would silently change how a service or a foreground
/// run reacts to a start failure and must fail the build.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ChassisOptionsTests
{
	/// <summary>
	/// Verifies that <see cref="ChassisOptions.SectionName"/> binds the <c>Host</c> configuration section, the
	/// section the outer chassis reads its lifecycle settings from.
	/// </summary>
	[Fact]
	public void SectionName_IsHost()
	{
		// Act + Assert
		Assert.Equal("Host", ChassisOptions.SectionName);
	}

	/// <summary>
	/// Verifies that <see cref="ChassisOptions.Mode"/> defaults to <see cref="HostMode.Auto"/>, which resolves
	/// to a resident daemon under the SCM and a fail-fast foreground run otherwise.
	/// </summary>
	[Fact]
	public void Mode_ByDefault_IsAuto()
	{
		// Arrange + Act
		ChassisOptions options = new();

		// Assert
		Assert.Equal(HostMode.Auto, options.Mode);
	}
}
