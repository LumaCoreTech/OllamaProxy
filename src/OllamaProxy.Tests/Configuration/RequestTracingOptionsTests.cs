// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.ComponentModel.DataAnnotations;

using OllamaProxy.Configuration;

namespace OllamaProxy.Tests.Configuration;

// Validation gate for the request-tracing options, grouped by the concern each rule guards.
//
// The Validate() method short-circuits when tracing is disabled, then checks three independent rules.
// The sections below follow that flow:
//
//   1. General      : disabled tracing skips every rule (WhenDisabled); a fully configured block passes
//                     (WhenFullyConfigured).
//   2. Directory    : a blank directory is rejected when enabled (WhenBlankAndEnabled).
//   3. MaxFiles     : a non-positive file cap is rejected (WhenNotPositive).
//   4. MaxBodyBytes : null means "no cap" and is accepted (WhenNull); a positive cap is accepted
//                     (WhenPositive); a non-positive cap is rejected (WhenNotPositive).
//   5. Defaults     : the body cap defaults to null (unbounded) and attachment redaction defaults to on.
//
// For DeepClone() coverage, see the DeepClone() companion partial (RequestTracingOptionsTests.DeepClone.cs).
[Trait("Category", "Unit")]
public sealed partial class RequestTracingOptionsTests
{
	/// <summary>
	/// Creates a tracing options instance that is enabled and otherwise valid, so a test can isolate the
	/// single rule it targets by mutating one property.
	/// </summary>
	/// <returns>An enabled, valid <see cref="RequestTracingOptions"/>.</returns>
	private static RequestTracingOptions EnabledOptions() =>
		new() { Enabled = true, Directory = "traces", MaxFiles = 100 };

	/// <summary>
	/// Runs <see cref="RequestTracingOptions.Validate"/> and materializes the results.
	/// </summary>
	/// <param name="options">The options to validate.</param>
	/// <returns>The validation results.</returns>
	private static List<ValidationResult> Validate(RequestTracingOptions options) =>
		[.. options.Validate(new ValidationContext(options))];

	// --- 1. General ---

	/// <summary>
	/// Verifies that <see cref="RequestTracingOptions.Validate"/> reports nothing when tracing is
	/// disabled, even with otherwise invalid settings, since a disabled tracer ignores its configuration.
	/// </summary>
	[Fact]
	public void Validate_WhenDisabled_ReturnsNoErrors()
	{
		// Arrange: every rule would fail, but Enabled is false so none are checked.
		RequestTracingOptions options = new()
			{ Enabled = false, Directory = "", MaxFiles = 0, MaxBodyBytes = -1 };

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		Assert.Empty(results);
	}

	/// <summary>
	/// Verifies that <see cref="RequestTracingOptions.Validate"/> accepts a fully configured, enabled
	/// tracing block with a positive body cap.
	/// </summary>
	[Fact]
	public void Validate_WhenFullyConfigured_ReturnsNoErrors()
	{
		// Arrange
		RequestTracingOptions options = EnabledOptions();
		options.MaxBodyBytes = 1024;

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		Assert.Empty(results);
	}

	// --- 2. Directory ---

	/// <summary>
	/// Verifies that <see cref="RequestTracingOptions.Validate"/> rejects a blank directory when tracing
	/// is enabled, since there would be nowhere to write the trace files.
	/// </summary>
	[Fact]
	public void Validate_Directory_WhenBlankAndEnabled_Fails()
	{
		// Arrange
		RequestTracingOptions options = EnabledOptions();
		options.Directory = "   ";

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		ValidationResult result = Assert.Single(results);
		Assert.Equal("Request tracing directory must be non-blank when tracing is enabled.", result.ErrorMessage);
		Assert.Contains(nameof(RequestTracingOptions.Directory), result.MemberNames);
	}

	// --- 3. MaxFiles ---

	/// <summary>
	/// Verifies that <see cref="RequestTracingOptions.Validate"/> rejects a non-positive file cap, since
	/// the directory ring buffer must retain at least one file.
	/// </summary>
	[Fact]
	public void Validate_MaxFiles_WhenNotPositive_Fails()
	{
		// Arrange
		RequestTracingOptions options = EnabledOptions();
		options.MaxFiles = 0;

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		ValidationResult result = Assert.Single(results);
		Assert.Equal("Request tracing file limit must be greater than zero.", result.ErrorMessage);
		Assert.Contains(nameof(RequestTracingOptions.MaxFiles), result.MemberNames);
	}

	// --- 4. MaxBodyBytes ---

	/// <summary>
	/// Verifies that <see cref="RequestTracingOptions.Validate"/> accepts a <see langword="null"/> body
	/// cap, the opt-in "no limit" setting that captures bodies in full.
	/// </summary>
	[Fact]
	public void Validate_MaxBodyBytes_WhenNull_ReturnsNoErrors()
	{
		// Arrange: null is the default and means "no cap" — it must validate cleanly.
		RequestTracingOptions options = EnabledOptions();
		options.MaxBodyBytes = null;

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		Assert.Empty(results);
	}

	/// <summary>
	/// Verifies that <see cref="RequestTracingOptions.Validate"/> accepts a positive body cap, the
	/// bounded-capture setting.
	/// </summary>
	[Fact]
	public void Validate_MaxBodyBytes_WhenPositive_ReturnsNoErrors()
	{
		// Arrange
		RequestTracingOptions options = EnabledOptions();
		options.MaxBodyBytes = 1;

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		Assert.Empty(results);
	}

	/// <summary>
	/// Verifies that <see cref="RequestTracingOptions.Validate"/> rejects a present but non-positive body
	/// cap, since a zero or negative limit would capture nothing.
	/// </summary>
	/// <param name="maxBodyBytes">The invalid body cap to validate.</param>
	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	public void Validate_MaxBodyBytes_WhenNotPositive_Fails(int maxBodyBytes)
	{
		// Arrange
		RequestTracingOptions options = EnabledOptions();
		options.MaxBodyBytes = maxBodyBytes;

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		ValidationResult result = Assert.Single(results);
		Assert.Equal("Request tracing body byte limit must be greater than zero when set.", result.ErrorMessage);
		Assert.Contains(nameof(RequestTracingOptions.MaxBodyBytes), result.MemberNames);
	}

	// --- 5. Defaults ---

	/// <summary>
	/// Verifies that <see cref="RequestTracingOptions.MaxBodyBytes"/> defaults to <see langword="null"/>,
	/// so a fresh tracing configuration captures bodies in full unless a cap is opted into.
	/// </summary>
	[Fact]
	public void MaxBodyBytes_WhenDefault_IsNull()
	{
		// Arrange
		RequestTracingOptions options = new();

		// Act
		int? maxBodyBytes = options.MaxBodyBytes;

		// Assert
		Assert.Null(maxBodyBytes);
	}

	/// <summary>
	/// Verifies that <see cref="RequestTracingOptions.RedactAttachments"/> defaults to
	/// <see langword="true"/>, so attachments are replaced with metadata unless verbatim capture is
	/// explicitly requested.
	/// </summary>
	[Fact]
	public void RedactAttachments_WhenDefault_IsTrue()
	{
		// Arrange
		RequestTracingOptions options = new();

		// Act
		bool redactAttachments = options.RedactAttachments;

		// Assert
		Assert.True(redactAttachments);
	}
}
