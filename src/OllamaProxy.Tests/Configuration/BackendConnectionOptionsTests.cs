// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.ComponentModel.DataAnnotations;

using OllamaProxy.Configuration;

namespace OllamaProxy.Tests.Configuration;

/// <summary>
/// Validation gate for <see cref="BackendConnectionOptions"/>, grouped by the concern each rule guards.
/// </summary>
/// <remarks>
/// The <c>Validate()</c> method checks two independent range rules and exposes two derived
/// <see cref="TimeSpan"/> accessors. The sections below follow that flow:
/// <list type="number">
///     <item>
///         <description>General: a fully configured block passes (WhenFullyConfigured).</description>
///     </item>
///     <item>
///         <description>
///         PooledConnectionLifetimeSeconds: the inclusive [1, 3600] range is accepted at both boundaries
///         (WhenAtBoundary); a value just outside either end is rejected (WhenOutOfRange).
///         </description>
///     </item>
///     <item>
///         <description>
///         ConnectTimeoutSeconds: the inclusive [1, 120] range is accepted at both boundaries
///         (WhenAtBoundary); a value just outside either end is rejected (WhenOutOfRange).
///         </description>
///     </item>
///     <item>
///         <description>
///         Derived accessors and defaults: the TimeSpan accessors mirror the second values; defaults are
///         120s lifetime and 10s connect timeout.
///         </description>
///     </item>
/// </list>
/// </remarks>
[Trait("Category", "Unit")]
public sealed class BackendConnectionOptionsTests
{
	// Built from the public range constants so the expected text can never drift from the values the rule uses.
	private static readonly string PooledLifetimeRangeMessage =
		"Backend pooled-connection lifetime must be between " +
		$"{BackendConnectionOptions.MinimumPooledConnectionLifetimeSeconds} and " +
		$"{BackendConnectionOptions.MaximumPooledConnectionLifetimeSeconds} seconds.";

	private static readonly string ConnectTimeoutRangeMessage =
		"Backend connect timeout must be between " +
		$"{BackendConnectionOptions.MinimumConnectTimeoutSeconds} and " +
		$"{BackendConnectionOptions.MaximumConnectTimeoutSeconds} seconds.";

	/// <summary>
	/// Creates a valid options instance so a test can isolate the single rule it targets by mutating one property.
	/// </summary>
	/// <returns>A valid <see cref="BackendConnectionOptions"/>.</returns>
	private static BackendConnectionOptions ValidOptions() => new()
	{
		PooledConnectionLifetimeSeconds = 120,
		ConnectTimeoutSeconds = 10
	};

	/// <summary>
	/// Runs <see cref="BackendConnectionOptions.Validate"/> and materializes the results.
	/// </summary>
	/// <param name="options">The options to validate.</param>
	/// <returns>The validation results.</returns>
	private static List<ValidationResult> Validate(BackendConnectionOptions options) =>
		[.. options.Validate(new ValidationContext(options))];

	// --- 1. General ---

	/// <summary>
	/// Verifies that <see cref="BackendConnectionOptions.Validate"/> reports nothing for a fully configured,
	/// in-range block.
	/// </summary>
	[Fact]
	public void Validate_WhenFullyConfigured_ReturnsNoErrors()
	{
		// Arrange
		BackendConnectionOptions options = ValidOptions();

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		Assert.Empty(results);
	}

	// --- 2. PooledConnectionLifetimeSeconds ---

	/// <summary>
	/// Verifies that <see cref="BackendConnectionOptions.Validate"/> accepts the inclusive lifetime range at
	/// both boundaries.
	/// </summary>
	/// <param name="seconds">The boundary value to accept.</param>
	[Theory]
	[InlineData(BackendConnectionOptions.MinimumPooledConnectionLifetimeSeconds)]
	[InlineData(BackendConnectionOptions.MaximumPooledConnectionLifetimeSeconds)]
	public void Validate_PooledConnectionLifetimeSeconds_WhenAtBoundary_ReturnsNoErrors(int seconds)
	{
		// Arrange
		BackendConnectionOptions options = ValidOptions();
		options.PooledConnectionLifetimeSeconds = seconds;

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		Assert.Empty(results);
	}

	/// <summary>
	/// Verifies that <see cref="BackendConnectionOptions.Validate"/> rejects a lifetime just outside either
	/// end of the inclusive range, reporting the offending member and message.
	/// </summary>
	/// <param name="seconds">The out-of-range value to reject.</param>
	[Theory]
	[InlineData(BackendConnectionOptions.MinimumPooledConnectionLifetimeSeconds - 1)]
	[InlineData(BackendConnectionOptions.MaximumPooledConnectionLifetimeSeconds + 1)]
	public void Validate_PooledConnectionLifetimeSeconds_WhenOutOfRange_ReturnsError(int seconds)
	{
		// Arrange
		BackendConnectionOptions options = ValidOptions();
		options.PooledConnectionLifetimeSeconds = seconds;

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		ValidationResult result = Assert.Single(results);
		Assert.Equal(PooledLifetimeRangeMessage, result.ErrorMessage);
		Assert.Equal(
			nameof(BackendConnectionOptions.PooledConnectionLifetimeSeconds),
			Assert.Single(result.MemberNames));
	}

	// --- 3. ConnectTimeoutSeconds ---

	/// <summary>
	/// Verifies that <see cref="BackendConnectionOptions.Validate"/> accepts the inclusive connect-timeout
	/// range at both boundaries.
	/// </summary>
	/// <param name="seconds">The boundary value to accept.</param>
	[Theory]
	[InlineData(BackendConnectionOptions.MinimumConnectTimeoutSeconds)]
	[InlineData(BackendConnectionOptions.MaximumConnectTimeoutSeconds)]
	public void Validate_ConnectTimeoutSeconds_WhenAtBoundary_ReturnsNoErrors(int seconds)
	{
		// Arrange
		BackendConnectionOptions options = ValidOptions();
		options.ConnectTimeoutSeconds = seconds;

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		Assert.Empty(results);
	}

	/// <summary>
	/// Verifies that <see cref="BackendConnectionOptions.Validate"/> rejects a connect timeout just outside
	/// either end of the inclusive range, reporting the offending member and message.
	/// </summary>
	/// <param name="seconds">The out-of-range value to reject.</param>
	[Theory]
	[InlineData(BackendConnectionOptions.MinimumConnectTimeoutSeconds - 1)]
	[InlineData(BackendConnectionOptions.MaximumConnectTimeoutSeconds + 1)]
	public void Validate_ConnectTimeoutSeconds_WhenOutOfRange_ReturnsError(int seconds)
	{
		// Arrange
		BackendConnectionOptions options = ValidOptions();
		options.ConnectTimeoutSeconds = seconds;

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		ValidationResult result = Assert.Single(results);
		Assert.Equal(ConnectTimeoutRangeMessage, result.ErrorMessage);
		Assert.Equal(nameof(BackendConnectionOptions.ConnectTimeoutSeconds), Assert.Single(result.MemberNames));
	}

	// --- 4. Derived accessors and defaults ---

	/// <summary>
	/// Verifies that <see cref="BackendConnectionOptions.PooledConnectionLifetime"/> mirrors the seconds value.
	/// </summary>
	[Fact]
	public void PooledConnectionLifetime_ReturnsSecondsAsTimeSpan()
	{
		// Arrange
		BackendConnectionOptions options = new() { PooledConnectionLifetimeSeconds = 90 };

		// Act
		TimeSpan lifetime = options.PooledConnectionLifetime;

		// Assert
		Assert.Equal(TimeSpan.FromSeconds(90), lifetime);
	}

	/// <summary>
	/// Verifies that <see cref="BackendConnectionOptions.ConnectTimeout"/> mirrors the seconds value.
	/// </summary>
	[Fact]
	public void ConnectTimeout_ReturnsSecondsAsTimeSpan()
	{
		// Arrange
		BackendConnectionOptions options = new() { ConnectTimeoutSeconds = 5 };

		// Act
		TimeSpan connectTimeout = options.ConnectTimeout;

		// Assert
		Assert.Equal(TimeSpan.FromSeconds(5), connectTimeout);
	}

	/// <summary>
	/// Verifies that a freshly constructed <see cref="BackendConnectionOptions"/> carries the documented
	/// defaults: a 120-second pooled-connection lifetime and a 10-second connect timeout.
	/// </summary>
	[Fact]
	public void Constructor_Defaults_UsesSafeConnectionTuning()
	{
		// Arrange + Act
		BackendConnectionOptions options = new();

		// Assert
		Assert.Equal(120, options.PooledConnectionLifetimeSeconds);
		Assert.Equal(10, options.ConnectTimeoutSeconds);
	}
}
