// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Microsoft.Extensions.Options;

using OllamaProxy.Configuration;
using OllamaProxy.Providers;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Tests.Providers;

/// <summary>
/// Tests for <see cref="ProviderTypeValidateOptions"/>, the startup validator that rejects unsupported provider
/// discriminators before routing can select an adapter that does not exist.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ProviderTypeValidateOptionsTests
{
	private const string CloudBackendName    = "cloud";
	private const string LocalBackendName    = "local";
	private const string VllmProviderType    = "vllm";
	private const string UnknownProviderType = "unknown";
	private const string TestApiKey          = "test-key";

	#region Constructor

	/// <summary>
	/// Verifies that the constructor rejects a <see langword="null"/> provider catalog.
	/// </summary>
	[Fact]
	public void Constructor_WhenCatalogIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() => new ProviderTypeValidateOptions(null!));
		Assert.Equal("catalog", exception.ParamName);
	}

	#endregion

	#region Validate()

	/// <summary>
	/// Verifies that validation succeeds when the neutral default provider and every configured backend provider are
	/// registered in the provider catalog.
	/// </summary>
	[Fact]
	public void Validate_WhenDefaultAndBackendsAreSupported_Succeeds()
	{
		// Arrange
		ProviderTypeValidateOptions sut = new(CreateCatalog(BackendOptions.DefaultProviderType, VllmProviderType));
		ProxyOptions options = CreateOptions(
			(CloudBackendName, BackendOptions.DefaultProviderType),
			(LocalBackendName, VllmProviderType));

		// Act
		ValidateOptionsResult result = sut.Validate(Options.DefaultName, options);

		// Assert
		Assert.Same(ValidateOptionsResult.Success, result);
	}

	/// <summary>
	/// Verifies that validation fails when the catalog no longer contains the neutral provider type used by backend
	/// options that omit an explicit provider discriminator.
	/// </summary>
	[Fact]
	public void Validate_WhenDefaultProviderIsUnsupported_FailsWithDefaultProviderMessage()
	{
		// Arrange
		ProviderTypeValidateOptions sut = new(CreateCatalog(VllmProviderType));
		ProxyOptions options = CreateOptions((LocalBackendName, VllmProviderType));

		// Act
		ValidateOptionsResult result = sut.Validate(Options.DefaultName, options);

		// Assert
		AssertFailedWith(
			result,
			$"The default provider type '{BackendOptions.DefaultProviderType}' does not resolve to a registered " +
			"provider. A provider adapter for it must be registered.");
	}

	/// <summary>
	/// Verifies that validation fails when a configured backend names a provider type that no registered provider
	/// descriptor supports.
	/// </summary>
	[Fact]
	public void Validate_WhenBackendProviderIsUnsupported_FailsWithBackendProviderMessage()
	{
		// Arrange
		ProviderTypeValidateOptions sut = new(CreateCatalog(BackendOptions.DefaultProviderType, VllmProviderType));
		ProxyOptions options = CreateOptions((CloudBackendName, UnknownProviderType));

		// Act
		ValidateOptionsResult result = sut.Validate(Options.DefaultName, options);

		// Assert
		AssertFailedWith(
			result,
			$"Backend '{CloudBackendName}' uses provider type '{UnknownProviderType}', which is not supported. " +
			$"Supported provider types: {BackendOptions.DefaultProviderType}, {VllmProviderType}.");
	}

	/// <summary>
	/// Verifies that validation rejects a <see langword="null"/> options instance before consulting the provider
	/// catalog.
	/// </summary>
	[Fact]
	public void Validate_WhenOptionsIsNull_ThrowsArgumentNullException()
	{
		// Arrange
		ProviderTypeValidateOptions sut = new(CreateCatalog(BackendOptions.DefaultProviderType));

		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() => sut.Validate(Options.DefaultName, null!));
		Assert.Equal("options", exception.ParamName);
	}

	#endregion

	/// <summary>
	/// Creates a provider catalog with deterministic descriptors for the supplied provider-type discriminators.
	/// </summary>
	/// <param name="providerTypes">
	/// The provider-type discriminators to register in order.
	/// </param>
	/// <returns>
	/// A provider catalog containing one descriptor per supplied provider type.
	/// </returns>
	private static ProviderCatalog CreateCatalog(params string[] providerTypes) => new(
		providerTypes.Select(providerType => new ProviderDescriptor(
			providerType,
			$"{providerType} Provider",
			OperatingMode.Explicit,
			$"https://{providerType}.example/v1")));

	/// <summary>
	/// Creates proxy options with valid backend shells for the supplied backend/provider pairs.
	/// </summary>
	/// <param name="backends">
	/// The backend names and provider-type discriminators to place in the options map.
	/// </param>
	/// <returns>
	/// A proxy options instance whose backend map preserves the supplied entries.
	/// </returns>
	private static ProxyOptions CreateOptions(params (string Name, string ProviderType)[] backends)
	{
		Dictionary<string, BackendOptions> backendOptions = new(StringComparer.OrdinalIgnoreCase);
		foreach ((string name, string providerType) in backends)
		{
			backendOptions.Add(
				name,
				new BackendOptions
				{
					BaseUrl = $"https://{name}.example/v1",
					ApiKey = TestApiKey,
					ProviderType = providerType
				});
		}

		return new ProxyOptions { Backends = backendOptions };
	}

	/// <summary>
	/// Verifies the complete observable state of a failed validation result.
	/// </summary>
	/// <param name="result">The validation result under test.</param>
	/// <param name="expectedFailure">The single expected failure message.</param>
	private static void AssertFailedWith(ValidateOptionsResult result, string expectedFailure)
	{
		Assert.False(result.Succeeded);
		Assert.True(result.Failed);
		Assert.False(result.Skipped);
		Assert.Equal([expectedFailure], result.Failures);
		Assert.Equal(expectedFailure, result.FailureMessage);
	}
}
