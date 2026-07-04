// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Configuration;
using OllamaProxy.Providers;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Tests.Providers;

/// <summary>
/// Tests for <see cref="ProviderCatalog.DisplayNameFor"/>, the reverse lookup that turns a backend's stored
/// provider-type discriminator into the human-facing label shown as a pill in the admin backend-card header.
/// The cases cover a registered type (resolved case-insensitively), an unregistered type (returned verbatim),
/// and the empty and <see langword="null"/> inputs that both resolve to an empty string.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ProviderCatalogTests
{
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

	/// <summary>
	/// Creates a catalog seeded with two neutral provider descriptors so the lookup has both a first and a
	/// second registered type to resolve, alongside room for unregistered and empty inputs. The default mode and
	/// base URL are irrelevant to the display-name lookup and are set to arbitrary valid values.
	/// </summary>
	/// <returns>A <see cref="ProviderCatalog"/> holding the <c>alpha</c> and <c>beta</c> descriptors.</returns>
	private static ProviderCatalog CreateCatalog() => new(
	[
		new ProviderDescriptor("alpha", "Alpha Provider", OperatingMode.Explicit, "https://alpha.example/v1"),
		new ProviderDescriptor("beta", "Beta Provider", OperatingMode.PlugAndPlay, "https://beta.example/v1")
	]);
}
