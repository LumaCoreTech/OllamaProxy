// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.ComponentModel.DataAnnotations;

using OllamaProxy.Configuration;

namespace OllamaProxy.Tests.Configuration;

/// <summary>
/// Validation gate for <see cref="BackendOptions"/>, grouped by the concern each rule guards.
/// </summary>
/// <remarks>
/// <see cref="BackendOptions.Validate"/> checks the backend's provider-independent shape and recurses into its
/// nested options, so the sections below follow that flow:
/// <list type="number">
///     <item>
///         <description>
///         General: a fully configured backend passes (WhenFullyConfigured).
///         </description>
///     </item>
///     <item>
///         <description>
///         BaseUrl: missing is rejected (WhenMissing), a non-absolute URI is rejected (WhenNotAbsolute), and an
///         absolute URI passes (WhenAbsolute).
///         </description>
///     </item>
///     <item>
///         <description>
///         ContextLength: a non-positive value is rejected (WhenNotPositive); an unset or positive value passes
///         (WhenUnset, WhenPositive).
///         </description>
///     </item>
///     <item>
///         <description>
///         ModelPrefix: a blank or slash-bearing prefix is rejected (WhenBlank, WhenContainsSlash); an unset or
///         plain prefix passes (WhenUnset, WhenPlain).
///         </description>
///     </item>
///     <item>
///         <description>
///         ApiKey: missing is rejected (WhenMissing), a too-short key is rejected (WhenTooShort), and a key at the
///         minimum length passes (WhenAtMinimumLength).
///         </description>
///     </item>
///     <item>
///         <description>
///         Probing recursion: an out-of-range probing budget surfaces with a <c>Probing.</c>-prefixed member name
///         (WhenProbingInvalid).
///         </description>
///     </item>
///     <item>
///         <description>
///         Models recursion: a per-entry failure surfaces with a <c>Models[i].</c>-prefixed member name
///         (WhenModelEntryInvalid); an empty registry is allowed (WhenModelsEmpty).
///         </description>
///     </item>
///     <item>
///         <description>
///         Duplicate model names: two entries resolving to the same client-facing name are rejected
///         (WhenModelNamesDuplicate).
///         </description>
///     </item>
/// </list>
/// For DeepClone() coverage, see the DeepClone() anchor partial (BackendOptionsTests.cs).
/// </remarks>
public sealed partial class BackendOptionsTests
{
	/// <summary>
	/// Runs <see cref="BackendOptions.Validate"/> and materializes the results.
	/// </summary>
	/// <param name="options">The backend options to validate.</param>
	/// <returns>The validation results.</returns>
	private static List<ValidationResult> Validate(BackendOptions options) =>
		[.. options.Validate(new ValidationContext(options))];

	/// <summary>
	/// Builds a minimally valid backend (absolute base URL, sufficiently long API key, no models) so each test
	/// can perturb exactly one property and attribute a failure to that change.
	/// </summary>
	/// <returns>A backend that passes <see cref="BackendOptions.Validate"/>.</returns>
	private static BackendOptions ValidBackend() => new()
	{
		BaseUrl = "https://api.example.com/v1",
		ApiKey = "sk-abcdefgh"
	};

	// --- 1. General ---

	/// <summary>
	/// Verifies that a fully configured backend produces no validation errors, establishing the baseline the
	/// per-property tests perturb.
	/// </summary>
	[Fact]
	public void Validate_WhenFullyConfigured_ReturnsNoErrors()
	{
		// Arrange
		BackendOptions options = ValidBackend();

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		Assert.Empty(results);
	}

	// --- 2. BaseUrl ---

	/// <summary>
	/// Verifies that an absolute base URL passes the URL rule.
	/// </summary>
	[Fact]
	public void Validate_BaseUrl_WhenAbsolute_ReturnsNoBaseUrlError()
	{
		// Arrange
		BackendOptions options = ValidBackend();
		options.BaseUrl = "http://localhost:1234/v1";

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		Assert.DoesNotContain(results, r => r.MemberNames.Contains(nameof(BackendOptions.BaseUrl)));
	}

	/// <summary>
	/// Verifies that a missing base URL is rejected as required.
	/// </summary>
	/// <param name="baseUrl">The blank base URL under test.</param>
	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void Validate_BaseUrl_WhenMissing_Fails(string baseUrl)
	{
		// Arrange
		BackendOptions options = ValidBackend();
		options.BaseUrl = baseUrl;

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		ValidationResult error = Assert.Single(
			results,
			r => r.MemberNames.Contains(nameof(BackendOptions.BaseUrl)));
		Assert.Equal("Backend base URL is required.", error.ErrorMessage);
	}

	/// <summary>
	/// Verifies that a present but non-absolute base URL is rejected.
	/// </summary>
	[Fact]
	public void Validate_BaseUrl_WhenNotAbsolute_Fails()
	{
		// Arrange
		BackendOptions options = ValidBackend();
		options.BaseUrl = "not-a-uri";

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		ValidationResult error = Assert.Single(
			results,
			r => r.MemberNames.Contains(nameof(BackendOptions.BaseUrl)));
		Assert.Equal("Backend base URL must be an absolute URI.", error.ErrorMessage);
	}

	// --- 3. ContextLength ---

	/// <summary>
	/// Verifies that an unset context length passes, since the fallback is optional.
	/// </summary>
	[Fact]
	public void Validate_ContextLength_WhenUnset_ReturnsNoContextLengthError()
	{
		// Arrange
		BackendOptions options = ValidBackend();
		options.ContextLength = null;

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		Assert.DoesNotContain(results, r => r.MemberNames.Contains(nameof(BackendOptions.ContextLength)));
	}

	/// <summary>
	/// Verifies that a positive context length passes.
	/// </summary>
	[Fact]
	public void Validate_ContextLength_WhenPositive_ReturnsNoContextLengthError()
	{
		// Arrange
		BackendOptions options = ValidBackend();
		options.ContextLength = 8192;

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		Assert.DoesNotContain(results, r => r.MemberNames.Contains(nameof(BackendOptions.ContextLength)));
	}

	/// <summary>
	/// Verifies that a non-positive context length is rejected.
	/// </summary>
	/// <param name="contextLength">The non-positive context length under test.</param>
	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	public void Validate_ContextLength_WhenNotPositive_Fails(int contextLength)
	{
		// Arrange
		BackendOptions options = ValidBackend();
		options.ContextLength = contextLength;

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		ValidationResult error = Assert.Single(
			results,
			r => r.MemberNames.Contains(nameof(BackendOptions.ContextLength)));
		Assert.Equal("Backend context length must be greater than zero when specified.", error.ErrorMessage);
	}

	// --- 4. ModelPrefix ---

	/// <summary>
	/// Verifies that an unset model prefix passes, since the prefix is optional.
	/// </summary>
	[Fact]
	public void Validate_ModelPrefix_WhenUnset_ReturnsNoModelPrefixError()
	{
		// Arrange
		BackendOptions options = ValidBackend();
		options.ModelPrefix = null;

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		Assert.DoesNotContain(results, r => r.MemberNames.Contains(nameof(BackendOptions.ModelPrefix)));
	}

	/// <summary>
	/// Verifies that a plain (non-blank, slash-free) model prefix passes.
	/// </summary>
	[Fact]
	public void Validate_ModelPrefix_WhenPlain_ReturnsNoModelPrefixError()
	{
		// Arrange
		BackendOptions options = ValidBackend();
		options.ModelPrefix = "vllm";

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		Assert.DoesNotContain(results, r => r.MemberNames.Contains(nameof(BackendOptions.ModelPrefix)));
	}

	/// <summary>
	/// Verifies that a present but blank model prefix is rejected, since it would yield confusing client-facing
	/// names.
	/// </summary>
	[Fact]
	public void Validate_ModelPrefix_WhenBlank_Fails()
	{
		// Arrange: a non-null but whitespace prefix is present-yet-blank, the branch that rejects.
		BackendOptions options = ValidBackend();
		options.ModelPrefix = "   ";

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		ValidationResult error = Assert.Single(
			results,
			r => r.MemberNames.Contains(nameof(BackendOptions.ModelPrefix)));
		Assert.Equal(
			"Backend model prefix must be non-blank and must not contain '/' when specified.",
			error.ErrorMessage);
	}

	/// <summary>
	/// Verifies that a model prefix embedding the '/' separator is rejected, since it would produce ambiguous
	/// prefixed names.
	/// </summary>
	[Fact]
	public void Validate_ModelPrefix_WhenContainsSlash_Fails()
	{
		// Arrange
		BackendOptions options = ValidBackend();
		options.ModelPrefix = "team/vllm";

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		ValidationResult error = Assert.Single(
			results,
			r => r.MemberNames.Contains(nameof(BackendOptions.ModelPrefix)));
		Assert.Equal(
			"Backend model prefix must be non-blank and must not contain '/' when specified.",
			error.ErrorMessage);
	}

	// --- 5. ApiKey ---

	/// <summary>
	/// Verifies that an API key at exactly the minimum length passes the length check.
	/// </summary>
	[Fact]
	public void Validate_ApiKey_WhenAtMinimumLength_ReturnsNoApiKeyError()
	{
		// Arrange: exactly MinimumApiKeyLength characters is the lower boundary that must pass.
		BackendOptions options = ValidBackend();
		options.ApiKey = new string('k', BackendOptions.MinimumApiKeyLength);

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		Assert.DoesNotContain(results, r => r.MemberNames.Contains(nameof(BackendOptions.ApiKey)));
	}

	/// <summary>
	/// Verifies that a missing API key is rejected as required.
	/// </summary>
	[Fact]
	public void Validate_ApiKey_WhenMissing_Fails()
	{
		// Arrange
		BackendOptions options = ValidBackend();
		options.ApiKey = string.Empty;

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		ValidationResult error = Assert.Single(
			results,
			r => r.MemberNames.Contains(nameof(BackendOptions.ApiKey)));
		Assert.Equal(
			"Backend API key is required. Provide it via configuration or an environment variable.",
			error.ErrorMessage);
	}

	/// <summary>
	/// Verifies that an API key shorter than the minimum length is rejected.
	/// </summary>
	[Fact]
	public void Validate_ApiKey_WhenTooShort_Fails()
	{
		// Arrange: one character below the minimum is the boundary that must fail.
		BackendOptions options = ValidBackend();
		options.ApiKey = new string('k', BackendOptions.MinimumApiKeyLength - 1);

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		ValidationResult error = Assert.Single(
			results,
			r => r.MemberNames.Contains(nameof(BackendOptions.ApiKey)));
		Assert.Equal(
			$"Backend API key must be at least {BackendOptions.MinimumApiKeyLength} characters long.",
			error.ErrorMessage);
	}

	// --- 6. Probing recursion ---

	/// <summary>
	/// Verifies that an out-of-range probing budget surfaces from the recursive validation with its member name
	/// prefixed by <c>Probing.</c>, so the operator sees which nested setting failed.
	/// </summary>
	[Fact]
	public void Validate_Probing_WhenInvalid_SurfacesPrefixedMemberName()
	{
		// Arrange: a zero concurrency is below the probing minimum, so the nested validator reports it.
		BackendOptions options = ValidBackend();
		options.Probing.MaxConcurrentProbes = 0;

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert: the failure is attributed to the nested member via the "Probing." prefix.
		string prefixed = $"{nameof(BackendOptions.Probing)}.{nameof(CapabilityProbingOptions.MaxConcurrentProbes)}";
		Assert.Single(results, r => r.MemberNames.Contains(prefixed));
	}

	// --- 7. Models recursion ---

	/// <summary>
	/// Verifies that an empty model registry is allowed, including for a backend that exposes nothing else.
	/// </summary>
	[Fact]
	public void Validate_Models_WhenEmpty_ReturnsNoErrors()
	{
		// Arrange: the default registry is empty; the baseline is otherwise valid.
		BackendOptions options = ValidBackend();

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		Assert.Empty(results);
	}

	/// <summary>
	/// Verifies that a per-entry model failure surfaces from the recursive validation with its member name
	/// prefixed by <c>Models[i].</c>, since the options pipeline does not descend into collection members on its
	/// own.
	/// </summary>
	[Fact]
	public void Validate_Models_WhenEntryInvalid_SurfacesIndexedPrefixedMemberName()
	{
		// Arrange: an entry with a blank name fails its own Name rule, which must bubble up indexed.
		BackendOptions options = ValidBackend();
		options.Models.Add(new ModelRegistrationOptions { Name = string.Empty });

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		string prefixed = $"{nameof(BackendOptions.Models)}[0].{nameof(ModelRegistrationOptions.Name)}";
		Assert.Single(results, r => r.MemberNames.Contains(prefixed));
	}

	// --- 8. Duplicate model names ---

	/// <summary>
	/// Verifies that two registry entries resolving to the same client-facing name (compared case-insensitively)
	/// are rejected, so the clash surfaces at startup instead of one entry silently winning in the catalog.
	/// </summary>
	[Fact]
	public void Validate_Models_WhenNamesDuplicate_Fails()
	{
		// Arrange: two entries whose names differ only by case collide under the OrdinalIgnoreCase catalog key.
		BackendOptions options = ValidBackend();
		options.Models.Add(new ModelRegistrationOptions { Name = "gemma2" });
		options.Models.Add(new ModelRegistrationOptions { Name = "GEMMA2" });

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert: the duplicate is reported once, attributed to the Models collection.
		ValidationResult error = Assert.Single(
			results,
			r => r.ErrorMessage!.Contains("registered more than once"));
		Assert.Contains(nameof(BackendOptions.Models), error.MemberNames);
	}
}
