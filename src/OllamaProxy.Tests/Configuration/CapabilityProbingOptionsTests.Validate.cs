// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.ComponentModel.DataAnnotations;

using OllamaProxy.Configuration;

namespace OllamaProxy.Tests.Configuration;

/// <summary>
/// Validation gate for <see cref="CapabilityProbingOptions"/>, grouped by the concern each rule guards.
/// </summary>
/// <remarks>
/// The <c>Validate()</c> method always checks <c>MaxConcurrentProbes</c>, then short-circuits when every probe is
/// disabled (the timeout and retry settings are irrelevant with nothing to probe), otherwise checks the four
/// per-attempt budget rules. The sections below follow that flow:
/// <list type="number">
///     <item>
///         <description>
///         General: the defaults pass (WhenDefault); disabling every probe skips the budget rules even when a
///         budget is out of range (WhenAllProbesDisabled); but MaxConcurrentProbes is still checked with every
///         probe disabled (WhenAllProbesDisabledAndConcurrencyInvalid).
///         </description>
///     </item>
///     <item>
///         <description>
///         MaxConcurrentProbes: below the minimum and above the maximum are rejected (WhenBelowMinimum,
///         WhenAboveMaximum); the range boundaries pass (WhenAtBoundaries).
///         </description>
///     </item>
///     <item>
///         <description>
///         TimeoutSeconds: below the minimum and above the maximum are rejected (WhenBelowMinimum,
///         WhenAboveMaximum).
///         </description>
///     </item>
///     <item>
///         <description>
///         InteractiveTimeoutSeconds: below the minimum and above the maximum are rejected (WhenBelowMinimum,
///         WhenAboveMaximum).
///         </description>
///     </item>
///     <item>
///         <description>
///         MaxProbeRetries: below the minimum and above the maximum are rejected (WhenBelowMinimum,
///         WhenAboveMaximum); zero (retries disabled) passes (WhenZero).
///         </description>
///     </item>
///     <item>
///         <description>
///         RetryBaseDelaySeconds: below the minimum and above the maximum are rejected (WhenBelowMinimum,
///         WhenAboveMaximum); zero (retry immediately) passes (WhenZero).
///         </description>
///     </item>
/// </list>
/// For DeepClone() coverage, see the DeepClone() anchor partial (CapabilityProbingOptionsTests.cs).
/// </remarks>
public sealed partial class CapabilityProbingOptionsTests
{
	/// <summary>
	/// Runs <see cref="CapabilityProbingOptions.Validate"/> and materializes the results.
	/// </summary>
	/// <param name="options">The options to validate.</param>
	/// <returns>The validation results.</returns>
	private static List<ValidationResult> Validate(CapabilityProbingOptions options) =>
		[.. options.Validate(new ValidationContext(options))];

	// --- 1. General ---

	/// <summary>
	/// Verifies that <see cref="CapabilityProbingOptions.Validate"/> accepts a default-constructed instance, so
	/// the shipped defaults are a valid configuration.
	/// </summary>
	[Fact]
	public void Validate_WhenDefault_ReturnsNoErrors()
	{
		// Arrange: the out-of-the-box defaults (all probes on, budgets within range).
		CapabilityProbingOptions options = new();

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		Assert.Empty(results);
	}

	/// <summary>
	/// Verifies that <see cref="CapabilityProbingOptions.Validate"/> skips the budget rules when every probe is
	/// disabled, since the timeout and retry settings are irrelevant with nothing to probe — an out-of-range
	/// timeout is left unchecked.
	/// </summary>
	[Fact]
	public void Validate_WhenAllProbesDisabled_SkipsBudgetRules()
	{
		// Arrange: every probe off, but an invalid timeout that would fail if the budget rules still ran.
		CapabilityProbingOptions options = new()
		{
			ProbeCompletion = false,
			ProbeTools = false,
			ProbeVision = false,
			ProbeEmbeddings = false,
			TimeoutSeconds = 0
		};

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert: the short-circuit skips the timeout check, so nothing is reported.
		Assert.Empty(results);
	}

	/// <summary>
	/// Verifies that <see cref="CapabilityProbingOptions.Validate"/> still checks <c>MaxConcurrentProbes</c> even
	/// when every probe is disabled, since discovery fan-out is governed by it regardless of which probes run.
	/// </summary>
	[Fact]
	public void Validate_WhenAllProbesDisabledAndConcurrencyInvalid_Fails()
	{
		// Arrange: every probe off, but an invalid concurrency that is checked before the short-circuit.
		CapabilityProbingOptions options = new()
		{
			ProbeCompletion = false,
			ProbeTools = false,
			ProbeVision = false,
			ProbeEmbeddings = false,
			MaxConcurrentProbes = 0
		};

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		ValidationResult result = Assert.Single(results);
		Assert.Equal("Maximum concurrent probes must be between 1 and 64.", result.ErrorMessage);
		Assert.Contains(nameof(CapabilityProbingOptions.MaxConcurrentProbes), result.MemberNames);
	}

	// --- 2. MaxConcurrentProbes ---

	/// <summary>
	/// Verifies that <see cref="CapabilityProbingOptions.Validate"/> rejects a concurrency below the minimum,
	/// since at least one model must be probed at a time.
	/// </summary>
	[Fact]
	public void Validate_MaxConcurrentProbes_WhenBelowMinimum_Fails()
	{
		// Arrange
		CapabilityProbingOptions options = new() { MaxConcurrentProbes = 0 };

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		ValidationResult result = Assert.Single(results);
		Assert.Equal("Maximum concurrent probes must be between 1 and 64.", result.ErrorMessage);
		Assert.Contains(nameof(CapabilityProbingOptions.MaxConcurrentProbes), result.MemberNames);
	}

	/// <summary>
	/// Verifies that <see cref="CapabilityProbingOptions.Validate"/> rejects a concurrency above the maximum,
	/// the upper bound that guards a backend against too many simultaneous in-flight probes.
	/// </summary>
	[Fact]
	public void Validate_MaxConcurrentProbes_WhenAboveMaximum_Fails()
	{
		// Arrange
		CapabilityProbingOptions options = new() { MaxConcurrentProbes = 65 };

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		ValidationResult result = Assert.Single(results);
		Assert.Equal("Maximum concurrent probes must be between 1 and 64.", result.ErrorMessage);
		Assert.Contains(nameof(CapabilityProbingOptions.MaxConcurrentProbes), result.MemberNames);
	}

	/// <summary>
	/// Verifies that <see cref="CapabilityProbingOptions.Validate"/> accepts both inclusive range boundaries for
	/// <c>MaxConcurrentProbes</c>, so the documented minimum and maximum are themselves valid.
	/// </summary>
	[Fact]
	public void Validate_MaxConcurrentProbes_WhenAtBoundaries_ReturnsNoErrors()
	{
		// Arrange + Act: both the inclusive minimum (1) and maximum (64) must validate cleanly.
		List<ValidationResult> atMinimum = Validate(
			new CapabilityProbingOptions { MaxConcurrentProbes = CapabilityProbingOptions.MinimumMaxConcurrentProbes });
		List<ValidationResult> atMaximum = Validate(
			new CapabilityProbingOptions
				{ MaxConcurrentProbes = CapabilityProbingOptions.MaximumMaxConcurrentProbes });

		// Assert
		Assert.Empty(atMinimum);
		Assert.Empty(atMaximum);
	}

	// --- 3. TimeoutSeconds ---

	/// <summary>
	/// Verifies that <see cref="CapabilityProbingOptions.Validate"/> rejects a startup timeout below the minimum,
	/// since a probe attempt must be given a non-zero window to complete.
	/// </summary>
	[Fact]
	public void Validate_TimeoutSeconds_WhenBelowMinimum_Fails()
	{
		// Arrange: a probe is enabled (the default) so the budget rules run.
		CapabilityProbingOptions options = new() { TimeoutSeconds = 0 };

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		ValidationResult result = Assert.Single(results);
		Assert.Equal("Probe timeout must be between 1 and 120 seconds.", result.ErrorMessage);
		Assert.Contains(nameof(CapabilityProbingOptions.TimeoutSeconds), result.MemberNames);
	}

	/// <summary>
	/// Verifies that <see cref="CapabilityProbingOptions.Validate"/> rejects a startup timeout above the maximum,
	/// the upper bound that keeps a single startup probe from stalling discovery indefinitely.
	/// </summary>
	[Fact]
	public void Validate_TimeoutSeconds_WhenAboveMaximum_Fails()
	{
		// Arrange
		CapabilityProbingOptions options = new() { TimeoutSeconds = 121 };

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		ValidationResult result = Assert.Single(results);
		Assert.Equal("Probe timeout must be between 1 and 120 seconds.", result.ErrorMessage);
		Assert.Contains(nameof(CapabilityProbingOptions.TimeoutSeconds), result.MemberNames);
	}

	// --- 4. InteractiveTimeoutSeconds ---

	/// <summary>
	/// Verifies that <see cref="CapabilityProbingOptions.Validate"/> rejects an interactive timeout below the
	/// minimum, since the operator-triggered probe must also be given a non-zero window.
	/// </summary>
	[Fact]
	public void Validate_InteractiveTimeoutSeconds_WhenBelowMinimum_Fails()
	{
		// Arrange
		CapabilityProbingOptions options = new() { InteractiveTimeoutSeconds = 0 };

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		ValidationResult result = Assert.Single(results);
		Assert.Equal("Interactive probe timeout must be between 1 and 300 seconds.", result.ErrorMessage);
		Assert.Contains(nameof(CapabilityProbingOptions.InteractiveTimeoutSeconds), result.MemberNames);
	}

	/// <summary>
	/// Verifies that <see cref="CapabilityProbingOptions.Validate"/> rejects an interactive timeout above the
	/// maximum, the upper bound that caps how long an operator-triggered probe may wait for a cold-loading model.
	/// </summary>
	[Fact]
	public void Validate_InteractiveTimeoutSeconds_WhenAboveMaximum_Fails()
	{
		// Arrange
		CapabilityProbingOptions options = new() { InteractiveTimeoutSeconds = 301 };

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		ValidationResult result = Assert.Single(results);
		Assert.Equal("Interactive probe timeout must be between 1 and 300 seconds.", result.ErrorMessage);
		Assert.Contains(nameof(CapabilityProbingOptions.InteractiveTimeoutSeconds), result.MemberNames);
	}

	// --- 5. MaxProbeRetries ---

	/// <summary>
	/// Verifies that <see cref="CapabilityProbingOptions.Validate"/> rejects a negative retry count, since a
	/// probe cannot be retried a negative number of times.
	/// </summary>
	[Fact]
	public void Validate_MaxProbeRetries_WhenBelowMinimum_Fails()
	{
		// Arrange
		CapabilityProbingOptions options = new() { MaxProbeRetries = -1 };

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		ValidationResult result = Assert.Single(results);
		Assert.Equal("Maximum probe retries must be between 0 and 10.", result.ErrorMessage);
		Assert.Contains(nameof(CapabilityProbingOptions.MaxProbeRetries), result.MemberNames);
	}

	/// <summary>
	/// Verifies that <see cref="CapabilityProbingOptions.Validate"/> rejects a retry count above the maximum,
	/// the upper bound that keeps a stubborn backend from being retried without end.
	/// </summary>
	[Fact]
	public void Validate_MaxProbeRetries_WhenAboveMaximum_Fails()
	{
		// Arrange
		CapabilityProbingOptions options = new() { MaxProbeRetries = 11 };

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		ValidationResult result = Assert.Single(results);
		Assert.Equal("Maximum probe retries must be between 0 and 10.", result.ErrorMessage);
		Assert.Contains(nameof(CapabilityProbingOptions.MaxProbeRetries), result.MemberNames);
	}

	/// <summary>
	/// Verifies that <see cref="CapabilityProbingOptions.Validate"/> accepts a retry count of zero, the inclusive
	/// minimum that disables retries (a single attempt).
	/// </summary>
	[Fact]
	public void Validate_MaxProbeRetries_WhenZero_ReturnsNoErrors()
	{
		// Arrange: zero is a valid "retries disabled" setting, not an out-of-range value.
		CapabilityProbingOptions options = new() { MaxProbeRetries = 0 };

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		Assert.Empty(results);
	}

	// --- 6. RetryBaseDelaySeconds ---

	/// <summary>
	/// Verifies that <see cref="CapabilityProbingOptions.Validate"/> rejects a negative retry base delay, since a
	/// backoff cannot wait a negative number of seconds.
	/// </summary>
	[Fact]
	public void Validate_RetryBaseDelaySeconds_WhenBelowMinimum_Fails()
	{
		// Arrange
		CapabilityProbingOptions options = new() { RetryBaseDelaySeconds = -1 };

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		ValidationResult result = Assert.Single(results);
		Assert.Equal("Retry base delay must be between 0 and 30 seconds.", result.ErrorMessage);
		Assert.Contains(nameof(CapabilityProbingOptions.RetryBaseDelaySeconds), result.MemberNames);
	}

	/// <summary>
	/// Verifies that <see cref="CapabilityProbingOptions.Validate"/> rejects a retry base delay above the
	/// maximum, the upper bound that keeps the exponential backoff from starting absurdly high.
	/// </summary>
	[Fact]
	public void Validate_RetryBaseDelaySeconds_WhenAboveMaximum_Fails()
	{
		// Arrange
		CapabilityProbingOptions options = new() { RetryBaseDelaySeconds = 31 };

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		ValidationResult result = Assert.Single(results);
		Assert.Equal("Retry base delay must be between 0 and 30 seconds.", result.ErrorMessage);
		Assert.Contains(nameof(CapabilityProbingOptions.RetryBaseDelaySeconds), result.MemberNames);
	}

	/// <summary>
	/// Verifies that <see cref="CapabilityProbingOptions.Validate"/> accepts a retry base delay of zero, the
	/// inclusive minimum that retries immediately with no backoff.
	/// </summary>
	[Fact]
	public void Validate_RetryBaseDelaySeconds_WhenZero_ReturnsNoErrors()
	{
		// Arrange: zero is a valid "retry immediately" setting, not an out-of-range value.
		CapabilityProbingOptions options = new() { RetryBaseDelaySeconds = 0 };

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		Assert.Empty(results);
	}
}
