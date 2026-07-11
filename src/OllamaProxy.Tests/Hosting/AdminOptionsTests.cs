// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.ComponentModel.DataAnnotations;

using OllamaProxy.Admin.Config;
using OllamaProxy.Hosting;

namespace OllamaProxy.Tests.Hosting;

/// <summary>
/// Tests for <see cref="AdminOptions"/> default values and listener validation. The default tests lock
/// deliberate, security-relevant defaults: the admin surface ships enabled but bound to <c>localhost</c>, so
/// a regression silently flipping either the on/off gate or the listening address would change the product's
/// security posture and must fail the build. The default API-key persistence policy is also locked because it
/// controls whether secrets sit on disk. The validation tests lock the listener guard that rejects a DNS host
/// name Kestrel would silently expand to "bind every interface".
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
	/// Verifies that <see cref="AdminOptions.ListenUrl"/> defaults to the <c>localhost</c> chassis port, keeping
	/// the admin surface off all external interfaces until an operator deliberately rebinds it.
	/// </summary>
	[Fact]
	public void ListenUrl_ByDefault_IsLocalhostChassisPort()
	{
		// Arrange + Act
		AdminOptions options = new();

		// Assert
		Assert.Equal("http://localhost:11435", options.ListenUrl);
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

	// --- ListenUrl validation ---

	/// <summary>
	/// Verifies that an interface-specific listener (the default <c>localhost</c> address) passes validation,
	/// so a fresh install starts without reconfiguration.
	/// </summary>
	[Fact]
	public void Validate_WhenListenUrlInterfaceSpecific_ReportsNoListenUrlError()
	{
		// Arrange
		AdminOptions options = new() { ListenUrl = "http://localhost:11435" };

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		Assert.DoesNotContain(results, r => r.MemberNames.Contains(nameof(AdminOptions.ListenUrl)));
	}

	/// <summary>
	/// Verifies that a DNS host name — which Kestrel would silently expand to "bind every interface" instead
	/// of resolving — is rejected on the admin listener, so the over-exposure fails fast at startup.
	/// </summary>
	/// <param name="listenUrl">The DNS-host listener URL expected to fail validation.</param>
	[Theory]
	[InlineData("http://my-server:11435")]
	[InlineData("http://admin.internal.lan:11435")]
	public void Validate_WhenListenUrlIsDnsHostName_ReportsListenUrlError(string listenUrl)
	{
		// Arrange
		AdminOptions options = new() { ListenUrl = listenUrl };

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		Assert.Contains(results, r => r.MemberNames.Contains(nameof(AdminOptions.ListenUrl)));
	}

	/// <summary>
	/// Validates the given <see cref="AdminOptions"/> instance and returns any validation errors.
	/// </summary>
	/// <param name="options">The <see cref="AdminOptions"/> instance to validate.</param>
	/// <returns>A list of <see cref="ValidationResult"/> objects representing validation errors.</returns>
	private static List<ValidationResult> Validate(AdminOptions options)
	{
		List<ValidationResult> results = [];

		Validator.TryValidateObject(
			options,
			new ValidationContext(options),
			results,
			validateAllProperties: true);

		return results;
	}
}
