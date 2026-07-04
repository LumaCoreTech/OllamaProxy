// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Admin.Config;
using OllamaProxy.Hosting;

namespace OllamaProxy.Tests.Hosting;

/// <summary>
/// Tests for <see cref="AdminOptions"/> default values. These lock deliberate, security-relevant defaults: the
/// admin surface ships enabled but bound to <c>localhost</c>, so a regression silently flipping either the
/// on/off gate or the listening address would change the product's security posture and must fail the build.
/// The default API-key persistence policy is also locked because it controls whether secrets sit on disk.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AdminOptionsTests
{
	/// <summary>
	/// Verifies that <see cref="AdminOptions.Enabled"/> defaults to <see langword="true"/> so a fresh install is
	/// manageable immediately, without the operator having to opt in to the admin surface.
	/// </summary>
	[Fact]
	public void Enabled_ByDefault_IsTrue()
	{
		// Arrange + Act
		AdminOptions options = new();

		// Assert
		Assert.True(options.Enabled);
	}

	/// <summary>
	/// Verifies that <see cref="AdminOptions.Url"/> defaults to the <c>localhost</c> chassis port, keeping the
	/// admin surface off all external interfaces until an operator deliberately rebinds it.
	/// </summary>
	[Fact]
	public void Url_ByDefault_IsLocalhostChassisPort()
	{
		// Arrange + Act
		AdminOptions options = new();

		// Assert
		Assert.Equal("http://localhost:11435", options.Url);
	}

	/// <summary>
	/// Verifies that <see cref="AdminOptions.ApiKeyPersistencePolicy"/> defaults to writing keys into the file,
	/// preserving the self-contained out-of-box behavior. A deployment that wants environment-only secrets must
	/// opt in explicitly.
	/// </summary>
	[Fact]
	public void ApiKeyPersistencePolicy_ByDefault_IsWriteToFile()
	{
		// Arrange + Act
		AdminOptions options = new();

		// Assert
		Assert.Equal(ApiKeyPersistencePolicy.WriteToFile, options.ApiKeyPersistencePolicy);
	}
}
