// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.ComponentModel.DataAnnotations;

using OllamaProxy.Configuration;

namespace OllamaProxy.Tests.Configuration;

/// <summary>
/// Validation gate for <see cref="ReasoningDetailsCacheOptions"/>, grouped by the concern each rule guards.
/// </summary>
/// <remarks>
/// The <c>Validate()</c> method short-circuits when the round-trip is disabled, then checks two independent range
/// rules. The sections below follow that flow:
/// <list type="number">
///     <item>
///         <description>
///         General: disabled caching skips every rule (WhenDisabled); a fully configured block passes
///         (WhenFullyConfigured).
///         </description>
///     </item>
///     <item>
///         <description>
///         SlidingExpirationSeconds: the inclusive [1, 3600] range is accepted at both boundaries
///         (WhenAtBoundary); a value just outside either end is rejected (WhenOutOfRange).
///         </description>
///     </item>
///     <item>
///         <description>
///         MaxEntries: the inclusive [1, 65536] range is accepted at both boundaries (WhenAtBoundary); a value
///         just outside either end is rejected (WhenOutOfRange).
///         </description>
///     </item>
///     <item>
///         <description>Defaults: caching defaults to on, the sliding window to 300s, the cap to 1024.</description>
///     </item>
/// </list>
/// </remarks>
[Trait("Category", "Unit")]
public sealed class ReasoningDetailsCacheOptionsTests
{
	// Built from the public range constants so the expected text can never drift from the values the rule uses.
	private static readonly string SlidingExpirationRangeMessage =
		"Reasoning-details cache sliding expiration must be between " +
		$"{ReasoningDetailsCacheOptions.MinimumSlidingExpirationSeconds} and " +
		$"{ReasoningDetailsCacheOptions.MaximumSlidingExpirationSeconds} seconds.";

	private static readonly string MaxEntriesRangeMessage =
		"Reasoning-details cache maximum entries must be between " +
		$"{ReasoningDetailsCacheOptions.MinimumMaxEntries} and " +
		$"{ReasoningDetailsCacheOptions.MaximumMaxEntries}.";

	/// <summary>
	/// Creates a cache options instance that is enabled and otherwise valid, so a test can isolate the single
	/// rule it targets by mutating one property.
	/// </summary>
	/// <returns>An enabled, valid <see cref="ReasoningDetailsCacheOptions"/>.</returns>
	private static ReasoningDetailsCacheOptions EnabledOptions() => new()
		{ Enabled = true, SlidingExpirationSeconds = 300, MaxEntries = 1024 };

	/// <summary>
	/// Runs <see cref="ReasoningDetailsCacheOptions.Validate"/> and materializes the results.
	/// </summary>
	/// <param name="options">The options to validate.</param>
	/// <returns>The validation results.</returns>
	private static List<ValidationResult> Validate(ReasoningDetailsCacheOptions options) =>
		[.. options.Validate(new ValidationContext(options))];

	// --- 1. General ---

	/// <summary>
	/// Verifies that <see cref="ReasoningDetailsCacheOptions.Validate"/> reports nothing when the round-trip is
	/// disabled, even with otherwise invalid sizing, since a disabled cache is never built and ignores its knobs.
	/// </summary>
	[Fact]
	public void Validate_WhenDisabled_ReturnsNoErrors()
	{
		// Arrange: both range rules would fail, but Enabled is false so neither is checked.
		ReasoningDetailsCacheOptions options = new()
		{
			Enabled = false,
			SlidingExpirationSeconds = ReasoningDetailsCacheOptions.MinimumSlidingExpirationSeconds - 1,
			MaxEntries = ReasoningDetailsCacheOptions.MinimumMaxEntries - 1
		};

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		Assert.Empty(results);
	}

	/// <summary>
	/// Verifies that <see cref="ReasoningDetailsCacheOptions.Validate"/> accepts a fully configured, enabled
	/// cache whose sizing sits inside both ranges.
	/// </summary>
	[Fact]
	public void Validate_WhenFullyConfigured_ReturnsNoErrors()
	{
		// Arrange
		ReasoningDetailsCacheOptions options = EnabledOptions();

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		Assert.Empty(results);
	}

	// --- 2. SlidingExpirationSeconds ---

	/// <summary>
	/// Verifies that <see cref="ReasoningDetailsCacheOptions.Validate"/> accepts the sliding-expiration value at
	/// both inclusive range boundaries.
	/// </summary>
	/// <param name="slidingExpirationSeconds">The boundary value to validate.</param>
	[Theory]
	[InlineData(ReasoningDetailsCacheOptions.MinimumSlidingExpirationSeconds)]
	[InlineData(ReasoningDetailsCacheOptions.MaximumSlidingExpirationSeconds)]
	public void Validate_SlidingExpirationSeconds_WhenAtBoundary_ReturnsNoErrors(int slidingExpirationSeconds)
	{
		// Arrange
		ReasoningDetailsCacheOptions options = EnabledOptions();
		options.SlidingExpirationSeconds = slidingExpirationSeconds;

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		Assert.Empty(results);
	}

	/// <summary>
	/// Verifies that <see cref="ReasoningDetailsCacheOptions.Validate"/> rejects a sliding-expiration value just
	/// outside either end of the inclusive range, since a sub-second window churns and an overlong one pins memory.
	/// </summary>
	/// <param name="slidingExpirationSeconds">The out-of-range value to validate.</param>
	[Theory]
	[InlineData(ReasoningDetailsCacheOptions.MinimumSlidingExpirationSeconds - 1)]
	[InlineData(ReasoningDetailsCacheOptions.MaximumSlidingExpirationSeconds + 1)]
	public void Validate_SlidingExpirationSeconds_WhenOutOfRange_Fails(int slidingExpirationSeconds)
	{
		// Arrange: MaxEntries stays valid, so the single error can only come from the sliding-expiration rule.
		ReasoningDetailsCacheOptions options = EnabledOptions();
		options.SlidingExpirationSeconds = slidingExpirationSeconds;

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		ValidationResult result = Assert.Single(results);
		Assert.Equal(SlidingExpirationRangeMessage, result.ErrorMessage);
		Assert.Contains(nameof(ReasoningDetailsCacheOptions.SlidingExpirationSeconds), result.MemberNames);
	}

	// --- 3. MaxEntries ---

	/// <summary>
	/// Verifies that <see cref="ReasoningDetailsCacheOptions.Validate"/> accepts the maximum-entries value at both
	/// inclusive range boundaries.
	/// </summary>
	/// <param name="maxEntries">The boundary value to validate.</param>
	[Theory]
	[InlineData(ReasoningDetailsCacheOptions.MinimumMaxEntries)]
	[InlineData(ReasoningDetailsCacheOptions.MaximumMaxEntries)]
	public void Validate_MaxEntries_WhenAtBoundary_ReturnsNoErrors(int maxEntries)
	{
		// Arrange
		ReasoningDetailsCacheOptions options = EnabledOptions();
		options.MaxEntries = maxEntries;

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		Assert.Empty(results);
	}

	/// <summary>
	/// Verifies that <see cref="ReasoningDetailsCacheOptions.Validate"/> rejects a maximum-entries value just
	/// outside either end of the inclusive range, since a zero cap holds nothing and an overlarge one defeats the
	/// bound.
	/// </summary>
	/// <param name="maxEntries">The out-of-range value to validate.</param>
	[Theory]
	[InlineData(ReasoningDetailsCacheOptions.MinimumMaxEntries - 1)]
	[InlineData(ReasoningDetailsCacheOptions.MaximumMaxEntries + 1)]
	public void Validate_MaxEntries_WhenOutOfRange_Fails(int maxEntries)
	{
		// Arrange: SlidingExpirationSeconds stays valid, so the single error can only come from the max-entries rule.
		ReasoningDetailsCacheOptions options = EnabledOptions();
		options.MaxEntries = maxEntries;

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		ValidationResult result = Assert.Single(results);
		Assert.Equal(MaxEntriesRangeMessage, result.ErrorMessage);
		Assert.Contains(nameof(ReasoningDetailsCacheOptions.MaxEntries), result.MemberNames);
	}

	// --- 4. Defaults ---

	/// <summary>
	/// Verifies that <see cref="ReasoningDetailsCacheOptions.Enabled"/> defaults to <see langword="true"/>, so the
	/// reasoning-details round-trip is active unless an operator opts out.
	/// </summary>
	[Fact]
	public void Enabled_WhenDefault_IsTrue()
	{
		// Arrange
		ReasoningDetailsCacheOptions options = new();

		// Act
		bool enabled = options.Enabled;

		// Assert
		Assert.True(enabled);
	}

	/// <summary>
	/// Verifies that <see cref="ReasoningDetailsCacheOptions.SlidingExpirationSeconds"/> defaults to the documented
	/// five-minute window.
	/// </summary>
	[Fact]
	public void SlidingExpirationSeconds_WhenDefault_Is300()
	{
		// Arrange
		ReasoningDetailsCacheOptions options = new();

		// Act
		int slidingExpirationSeconds = options.SlidingExpirationSeconds;

		// Assert
		Assert.Equal(300, slidingExpirationSeconds);
	}

	/// <summary>
	/// Verifies that <see cref="ReasoningDetailsCacheOptions.MaxEntries"/> defaults to the documented cap of 1024
	/// retained blobs.
	/// </summary>
	[Fact]
	public void MaxEntries_WhenDefault_Is1024()
	{
		// Arrange
		ReasoningDetailsCacheOptions options = new();

		// Act
		int maxEntries = options.MaxEntries;

		// Assert
		Assert.Equal(1024, maxEntries);
	}
}
