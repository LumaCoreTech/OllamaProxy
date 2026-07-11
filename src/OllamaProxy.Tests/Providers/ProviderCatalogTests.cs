// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Configuration;
using OllamaProxy.Providers;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Tests.Providers;

/// <summary>
/// Tests for <see cref="ProviderCatalog"/>, the frozen index that answers every provider-metadata question from
/// the registered <see cref="ProviderDescriptor"/>s. The sections cover the construction guards (null and
/// duplicate provider types), the support probe, the two metadata lookups that fall back for unknown types
/// (<see cref="ProviderCatalog.DefaultModeFor"/>, <see cref="ProviderCatalog.DefaultBaseUrlFor"/>), the
/// display-name reverse lookup, and <see cref="ProviderCatalog.ResolveMode"/>, which prefers a backend's explicit
/// mode over the provider default.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ProviderCatalogTests
{
	// --- 1. Construction ---

	/// <summary>
	/// Verifies that <see cref="ProviderCatalog"/> rejects a <see langword="null"/> descriptor sequence, since it
	/// has nothing to index.
	/// </summary>
	[Fact]
	public void Constructor_WhenDescriptorsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() => new ProviderCatalog(null!));
		Assert.Equal("descriptors", exception.ParamName);
	}

	/// <summary>
	/// Verifies that <see cref="ProviderCatalog"/> rejects two descriptors declaring the same provider type
	/// (compared case-insensitively), since that would make adapter selection ambiguous.
	/// </summary>
	[Fact]
	public void Constructor_WhenDuplicateProviderType_ThrowsInvalidOperationException()
	{
		// Arrange: two descriptors whose provider types differ only by case collide under the OrdinalIgnoreCase key.
		ProviderDescriptor[] descriptors =
		[
			new("alpha", "Alpha Provider", OperatingMode.Explicit, "https://alpha.example/v1"),
			new("ALPHA", "Alpha Duplicate", OperatingMode.PlugAndPlay, "https://dup.example/v1")
		];

		// Act + Assert
		var exception = Assert.Throws<InvalidOperationException>(() => new ProviderCatalog(descriptors));
		Assert.Equal(
			"More than one provider descriptor is registered for provider type 'ALPHA'.",
			exception.Message);
	}

	/// <summary>
	/// Verifies that <see cref="ProviderCatalog.Providers"/> preserves the registration order of the descriptors,
	/// the sequence the picker and duplicate scan both rely on.
	/// </summary>
	[Fact]
	public void Providers_WhenConstructed_PreservesRegistrationOrder()
	{
		// Arrange
		ProviderCatalog sut = CreateCatalog();

		// Act + Assert: two descriptors in the order they were registered.
		Assert.Collection(
			sut.Providers,
			descriptor => Assert.Equal("alpha", descriptor.ProviderType),
			descriptor => Assert.Equal("beta", descriptor.ProviderType));
	}

	// --- 2. IsSupported ---

	/// <summary>
	/// Verifies that <see cref="ProviderCatalog.IsSupported"/> recognizes a registered type case-insensitively and
	/// rejects unregistered or <see langword="null"/> input.
	/// </summary>
	/// <param name="providerType">The provider-type discriminator passed to the probe.</param>
	/// <param name="expected">Whether the catalog is expected to report the type as supported.</param>
	[Theory]
	[InlineData("alpha", true)]  // registered type
	[InlineData("ALPHA", true)]  // registered, matched case-insensitively
	[InlineData("gamma", false)] // unregistered type
	[InlineData("", false)]      // empty input
	[InlineData(null, false)]    // null input
	public void IsSupported_ForGivenProviderType_ReturnsExpected(string? providerType, bool expected)
	{
		// Arrange
		ProviderCatalog sut = CreateCatalog();

		// Act
		bool result = sut.IsSupported(providerType);

		// Assert
		Assert.Equal(expected, result);
	}

	// --- 3. DefaultModeFor ---

	/// <summary>
	/// Verifies that <see cref="ProviderCatalog.DefaultModeFor"/> returns a registered provider's default mode
	/// (matched case-insensitively) and falls back to the conservative <see cref="OperatingMode.Explicit"/> for
	/// unregistered or <see langword="null"/> input.
	/// </summary>
	/// <param name="providerType">The provider-type discriminator passed to the lookup.</param>
	/// <param name="expected">The operating mode the lookup is expected to return.</param>
	[Theory]
	[InlineData("alpha", OperatingMode.Explicit)]   // registered => its own default mode
	[InlineData("beta", OperatingMode.PlugAndPlay)] // second registered type resolves independently
	[InlineData("BETA", OperatingMode.PlugAndPlay)] // registered, matched case-insensitively
	[InlineData("gamma", OperatingMode.Explicit)]   // unregistered => conservative Explicit fallback
	[InlineData(null, OperatingMode.Explicit)]      // null => conservative Explicit fallback
	public void DefaultModeFor_ForGivenProviderType_ReturnsExpectedMode(
		string?       providerType,
		OperatingMode expected)
	{
		// Arrange
		ProviderCatalog sut = CreateCatalog();

		// Act
		OperatingMode result = sut.DefaultModeFor(providerType);

		// Assert
		Assert.Equal(expected, result);
	}

	// --- 4. DefaultBaseUrlFor ---

	/// <summary>
	/// Verifies that <see cref="ProviderCatalog.DefaultBaseUrlFor"/> returns a registered provider's canonical URL
	/// (matched case-insensitively) and falls back to an empty "no prefill" string for unregistered or
	/// <see langword="null"/> input.
	/// </summary>
	/// <param name="providerType">The provider-type discriminator passed to the lookup.</param>
	/// <param name="expected">The base URL the lookup is expected to return.</param>
	[Theory]
	[InlineData("alpha", "https://alpha.example/v1")] // registered => its canonical URL
	[InlineData("BeTa", "https://beta.example/v1")]   // registered, matched case-insensitively
	[InlineData("gamma", "")]                         // unregistered => empty "no prefill"
	[InlineData(null, "")]                            // null => empty "no prefill"
	public void DefaultBaseUrlFor_ForGivenProviderType_ReturnsExpectedUrl(
		string? providerType,
		string  expected)
	{
		// Arrange
		ProviderCatalog sut = CreateCatalog();

		// Act
		string result = sut.DefaultBaseUrlFor(providerType);

		// Assert
		Assert.Equal(expected, result);
	}

	// --- 5. DisplayNameFor ---

	/// <summary>
	/// Verifies that the display-name lookup returns a registered provider's label (matched case-insensitively),
	/// echoes an unregistered provider type verbatim, and resolves empty or <see langword="null"/> input to an
	/// empty string.
	/// </summary>
	/// <param name="providerType">The provider-type discriminator passed to the lookup.</param>
	/// <param name="expected">The display label the lookup is expected to return.</param>
	[Theory]
	[InlineData("alpha", "Alpha Provider")] // registered type => its display name
	[InlineData("ALPHA", "Alpha Provider")] // registered, matched case-insensitively (upper)
	[InlineData("BeTa", "Beta Provider")]   // registered, matched case-insensitively (mixed)
	[InlineData("beta", "Beta Provider")]   // second registered type resolves independently
	[InlineData("gamma", "gamma")]          // unregistered type => returned verbatim (label is display-only)
	[InlineData("", "")]                    // empty input => empty label
	[InlineData(null, "")]                  // null input => empty label
	public void DisplayNameFor_ForGivenProviderType_ReturnsExpectedLabel(string? providerType, string expected)
	{
		// Arrange
		ProviderCatalog sut = CreateCatalog();

		// Act
		string result = sut.DisplayNameFor(providerType);

		// Assert
		Assert.Equal(expected, result);
	}

	// --- 6. ResolveMode ---

	/// <summary>
	/// Verifies that <see cref="ProviderCatalog.ResolveMode"/> honors a backend's explicit
	/// <see cref="BackendOptions.Mode"/> in preference to the provider default.
	/// </summary>
	[Fact]
	public void ResolveMode_WhenBackendModeSet_ReturnsBackendMode()
	{
		// Arrange: alpha defaults to Explicit, but the backend explicitly asks for Hybrid.
		ProviderCatalog sut = CreateCatalog();
		BackendOptions backend = new() { ProviderType = "alpha", Mode = OperatingMode.Hybrid };

		// Act
		OperatingMode result = sut.ResolveMode(backend);

		// Assert: the explicit backend choice wins over the provider default.
		Assert.Equal(OperatingMode.Hybrid, result);
	}

	/// <summary>
	/// Verifies that <see cref="ProviderCatalog.ResolveMode"/> falls back to the provider's default mode when the
	/// backend leaves <see cref="BackendOptions.Mode"/> unset.
	/// </summary>
	[Fact]
	public void ResolveMode_WhenBackendModeUnset_ReturnsProviderDefault()
	{
		// Arrange: beta defaults to PlugAndPlay, and the backend expresses no preference.
		ProviderCatalog sut = CreateCatalog();
		BackendOptions backend = new() { ProviderType = "beta", Mode = null };

		// Act
		OperatingMode result = sut.ResolveMode(backend);

		// Assert
		Assert.Equal(OperatingMode.PlugAndPlay, result);
	}

	/// <summary>
	/// Verifies that <see cref="ProviderCatalog.ResolveMode"/> rejects a <see langword="null"/> backend.
	/// </summary>
	[Fact]
	public void ResolveMode_WhenBackendNull_ThrowsArgumentNullException()
	{
		// Arrange
		ProviderCatalog sut = CreateCatalog();

		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() => sut.ResolveMode(null!));
		Assert.Equal("backend", exception.ParamName);
	}

	/// <summary>
	/// Creates a catalog seeded with two neutral provider descriptors so the lookups have both a first and a
	/// second registered type to resolve, alongside room for unregistered and empty inputs. The <c>alpha</c>
	/// descriptor defaults to <see cref="OperatingMode.Explicit"/> and <c>beta</c> to
	/// <see cref="OperatingMode.PlugAndPlay"/> so the mode lookups have two distinct answers to distinguish.
	/// </summary>
	/// <returns>A <see cref="ProviderCatalog"/> holding the <c>alpha</c> and <c>beta</c> descriptors.</returns>
	private static ProviderCatalog CreateCatalog() => new(
	[
		new ProviderDescriptor("alpha", "Alpha Provider", OperatingMode.Explicit, "https://alpha.example/v1"),
		new ProviderDescriptor("beta", "Beta Provider", OperatingMode.PlugAndPlay, "https://beta.example/v1")
	]);
}
