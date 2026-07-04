// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Core;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Tests.Core;

/// <summary>
/// Tests for <see cref="ModelRouter"/>, the lock-free catalog behind a volatile snapshot. The file is
/// organized by member:
/// <list type="number">
///     <item>
///         <description><see cref="ModelRouter.Initialize"/> — one-time publication and its guards.</description>
///     </item>
///     <item>
///         <description><see cref="ModelRouter.GetModels"/> — empty before init, name-sorted after.</description>
///     </item>
///     <item>
///         <description><see cref="ModelRouter.TryResolve"/> — case/<c>:latest</c>-tolerant lookup and guards.</description>
///     </item>
/// </list>
/// </summary>
[Trait("Category", "Unit")]
public sealed class ModelRouterTests
{
	private static ModelCapabilities Caps => ModelCapabilities.CompletionOnly;

	private static RegisteredModel Model(string name) => new(name, "backend", name, Caps, ContextLength: 8192);

	#region Initialize()

	/// <summary>
	/// Verifies that <see cref="ModelRouter.Initialize"/> publishes the supplied models so they become
	/// readable through <see cref="ModelRouter.GetModels"/>.
	/// </summary>
	[Fact]
	public void Initialize_WhenCalledOnce_PublishesCatalog()
	{
		// Arrange
		ModelRouter sut = new();

		// Act
		sut.Initialize([Model("alpha")]);

		// Assert
		RegisteredModel single = Assert.Single(sut.GetModels());
		Assert.Equal("alpha", single.Name);
	}

	/// <summary>
	/// Verifies that <see cref="ModelRouter.Initialize"/> throws when invoked a second time, since the
	/// catalog is meant to be published exactly once during startup.
	/// </summary>
	[Fact]
	public void Initialize_WhenCalledTwice_ThrowsInvalidOperationException()
	{
		// Arrange
		ModelRouter sut = new();
		sut.Initialize([Model("alpha")]);

		// Act + Assert
		var exception =
			Assert.Throws<InvalidOperationException>(() => sut.Initialize([Model("beta")]));
		Assert.Equal("The model router has already been initialized.", exception.Message);
	}

	/// <summary>
	/// Verifies that <see cref="ModelRouter.Initialize"/> rejects a <see langword="null"/> model
	/// sequence.
	/// </summary>
	[Fact]
	public void Initialize_WhenModelsIsNull_ThrowsArgumentNullException()
	{
		// Arrange
		ModelRouter sut = new();

		// Act + Assert
		var exception =
			Assert.Throws<ArgumentNullException>(() => sut.Initialize(null!));
		Assert.Equal("models", exception.ParamName);
	}

	#endregion

	#region GetModels()

	/// <summary>
	/// Verifies that <see cref="ModelRouter.GetModels"/> returns an empty catalog before initialization.
	/// </summary>
	[Fact]
	public void GetModels_BeforeInitialize_ReturnsEmpty()
	{
		// Arrange
		ModelRouter sut = new();

		// Act
		IReadOnlyList<RegisteredModel> models = sut.GetModels();

		// Assert
		Assert.Empty(models);
	}

	/// <summary>
	/// Verifies that <see cref="ModelRouter.GetModels"/> returns the catalog sorted by name regardless
	/// of the input order.
	/// </summary>
	[Fact]
	public void GetModels_AfterInitialize_ReturnsNameSortedCatalog()
	{
		// Arrange: deliberately unsorted input.
		ModelRouter sut = new();
		sut.Initialize([Model("gamma"), Model("alpha"), Model("beta")]);

		// Act
		string[] names = sut.GetModels().Select(model => model.Name).ToArray();

		// Assert
		Assert.Equal(["alpha", "beta", "gamma"], names);
	}

	#endregion

	#region TryResolve()

	/// <summary>Cases pairing a stored name with a client-supplied lookup that must resolve to it.</summary>
	public static TheoryData<string, string, string> ResolveCases => new()
	{
		// Exact match.
		{ "exact", "llama3", "llama3" },

		// Case-insensitive match.
		{ "different case", "Llama3", "llama3" },

		// The conventional :latest suffix is stripped before lookup.
		{ "latest suffix", "llama3", "llama3:latest" },

		// Surrounding whitespace is trimmed.
		{ "surrounding whitespace", "llama3", "  llama3  " }
	};

	/// <summary>
	/// Verifies that <see cref="ModelRouter.TryResolve"/> resolves a stored model across casing,
	/// <c>:latest</c> suffix, and whitespace variations.
	/// </summary>
	/// <param name="scenario">A human-readable label for the case.</param>
	/// <param name="storedName">The name the catalog is seeded with.</param>
	/// <param name="lookup">The client-supplied name to resolve.</param>
	[Theory]
	[MemberData(nameof(ResolveCases))]
	public void TryResolve_WhenNameMatches_ReturnsTrueAndModel(string scenario, string storedName, string lookup)
	{
		_ = scenario;

		// Arrange
		ModelRouter sut = new();
		sut.Initialize([Model(storedName)]);

		// Act
		bool resolved = sut.TryResolve(lookup, out RegisteredModel? model);

		// Assert
		Assert.True(resolved);
		Assert.NotNull(model);
		Assert.Equal(storedName, model.Name);
	}

	/// <summary>
	/// Verifies that <see cref="ModelRouter.TryResolve"/> reports failure and yields <see langword="null"/>
	/// for a name absent from the catalog.
	/// </summary>
	[Fact]
	public void TryResolve_WhenNameUnknown_ReturnsFalseAndNull()
	{
		// Arrange
		ModelRouter sut = new();
		sut.Initialize([Model("llama3")]);

		// Act
		bool resolved = sut.TryResolve("missing", out RegisteredModel? model);

		// Assert
		Assert.False(resolved);
		Assert.Null(model);
	}

	/// <summary>
	/// Verifies that <see cref="ModelRouter.TryResolve"/> rejects a <see langword="null"/> name.
	/// </summary>
	[Fact]
	public void TryResolve_WhenNameIsNull_ThrowsArgumentNullException()
	{
		// Arrange
		ModelRouter sut = new();
		sut.Initialize([Model("llama3")]);

		// Act + Assert
		var exception =
			Assert.Throws<ArgumentNullException>(() => sut.TryResolve(null!, out RegisteredModel? _));
		Assert.Equal("modelName", exception.ParamName);
	}

	/// <summary>
	/// Verifies that <see cref="ModelRouter.TryResolve"/> rejects an empty or whitespace name.
	/// </summary>
	/// <param name="modelName">The invalid name to resolve.</param>
	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void TryResolve_WhenNameIsEmptyOrWhiteSpace_ThrowsArgumentException(string modelName)
	{
		// Arrange
		ModelRouter sut = new();
		sut.Initialize([Model("llama3")]);

		// Act + Assert
		var exception =
			Assert.Throws<ArgumentException>(() => sut.TryResolve(modelName, out RegisteredModel? _));
		Assert.Equal("modelName", exception.ParamName);
	}

	#endregion
}
