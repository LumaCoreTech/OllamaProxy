// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Microsoft.Extensions.Options;

using OllamaProxy.Configuration;
using OllamaProxy.Contracts.Ollama;
using OllamaProxy.Core;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Tests.Core;

/// <summary>
/// Tests for <see cref="ProviderResolver"/>, which maps a backend name to the adapter matching that
/// backend's provider type. The story escalates from success to the three failure modes:
/// <list type="number">
///     <item>
///         <description>Resolve succeeds, selecting the adapter by provider type (WhenBackendConfigured).</description>
///     </item>
///     <item>
///         <description>
///         ResolveDraft selects the adapter by the draft's provider type and carries the draft inline
///         (WhenProviderTypeMatches), then rejects an unmatched type and a null draft.
///         </description>
///     </item>
///     <item>
///         <description>Construction rejects duplicate provider types (WhenDuplicateProviderType) and null args.</description>
///     </item>
///     <item>
///         <description>Resolve rejects unknown backends, unmatched provider types, and blank names.</description>
///     </item>
/// </list>
/// </summary>
[Trait("Category", "Unit")]
public sealed class ProviderResolverTests
{
	private static IOptions<ProxyOptions> OptionsWith(params (string Name, string ProviderType)[] backends)
	{
		ProxyOptions options = new();
		foreach ((string name, string providerType) in backends)
		{
			options.Backends[name] = new BackendOptions { BaseUrl = "https://x/v1", ProviderType = providerType };
		}

		return Options.Create(options);
	}

	/// <summary>
	/// Verifies that <see cref="ProviderResolver.Resolve"/> returns the adapter whose provider type
	/// matches the named backend, paired with a backend context carrying that name.
	/// </summary>
	[Fact]
	public void Resolve_WhenBackendConfigured_ReturnsMatchingAdapterAndContext()
	{
		// Arrange: two adapters; the backend selects the OpenAI one by provider type.
		StubAdapter openai = new("openai");
		StubAdapter other = new("anthropic");
		ProviderResolver sut = new(OptionsWith(("cloud", "openai")), [openai, other]);

		// Act
		ResolvedBackend resolved = sut.Resolve("cloud");

		// Assert
		Assert.Same(openai, resolved.Adapter);
		Assert.Equal("cloud", resolved.Context.Name);
	}

	/// <summary>
	/// Verifies that <see cref="ProviderResolver.ResolveDraft"/> selects the adapter whose provider type
	/// matches the draft's, and pairs it with a context that carries the draft options inline so the
	/// adapter builds an ad-hoc client rather than resolving a named one.
	/// </summary>
	[Fact]
	public void ResolveDraft_WhenProviderTypeMatches_ReturnsMatchingAdapterAndDraftContext()
	{
		// Arrange: two adapters; the draft selects the OpenAI one by its provider type, with no
		// committed backend of that name registered (the resolver has an empty backend set).
		StubAdapter openai = new("openai");
		StubAdapter other = new("anthropic");
		ProviderResolver sut = new(OptionsWith(), [openai, other]);
		BackendOptions draft = new() { BaseUrl = "https://draft/v1", ProviderType = "openai" };

		// Act
		ResolvedBackend resolved = sut.ResolveDraft(draft);

		// Assert: the adapter is selected by provider type and the context carries the draft inline.
		Assert.Same(openai, resolved.Adapter);
		Assert.Same(draft, resolved.Context.Draft);
		Assert.Equal("(draft)", resolved.Context.Name);
	}

	/// <summary>
	/// Verifies that <see cref="ProviderResolver.ResolveDraft"/> throws when no registered adapter
	/// matches the draft's provider type.
	/// </summary>
	[Fact]
	public void ResolveDraft_WhenNoAdapterForProviderType_ThrowsInvalidOperationException()
	{
		// Arrange: the draft wants "anthropic" but only the OpenAI adapter is registered.
		ProviderResolver sut = new(OptionsWith(), [new StubAdapter("openai")]);
		BackendOptions draft = new() { BaseUrl = "https://draft/v1", ProviderType = "anthropic" };

		// Act + Assert
		var exception = Assert.Throws<InvalidOperationException>(() => sut.ResolveDraft(draft));
		Assert.Equal(
			"No provider adapter is registered for provider type 'anthropic' required by the draft backend.",
			exception.Message);
	}

	/// <summary>
	/// Verifies that <see cref="ProviderResolver.ResolveDraft"/> rejects a <see langword="null"/> draft.
	/// </summary>
	[Fact]
	public void ResolveDraft_WhenDraftIsNull_ThrowsArgumentNullException()
	{
		// Arrange
		ProviderResolver sut = new(OptionsWith(("cloud", "openai")), [new StubAdapter("openai")]);

		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() => sut.ResolveDraft(null!));
		Assert.Equal("draft", exception.ParamName);
	}

	/// <summary>
	/// Verifies that the <see cref="ProviderResolver"/> constructor rejects two adapters that declare
	/// the same provider type, since selection would then be ambiguous.
	/// </summary>
	[Fact]
	public void Constructor_WhenDuplicateProviderType_ThrowsInvalidOperationException()
	{
		// Arrange
		StubAdapter first = new("openai");
		StubAdapter second = new("openai");

		// Act + Assert
		var exception = Assert.Throws<InvalidOperationException>(() =>
			new ProviderResolver(OptionsWith(("cloud", "openai")), [first, second]));
		Assert.Equal(
			"More than one provider adapter is registered for provider type 'openai'.",
			exception.Message);
	}

	/// <summary>
	/// Verifies that the <see cref="ProviderResolver"/> constructor rejects a <see langword="null"/>
	/// options argument.
	/// </summary>
	[Fact]
	public void Constructor_WhenOptionsIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception =
			Assert.Throws<ArgumentNullException>(() => new ProviderResolver(null!, [new StubAdapter("openai")]));
		Assert.Equal("options", exception.ParamName);
	}

	/// <summary>
	/// Verifies that the <see cref="ProviderResolver"/> constructor rejects a <see langword="null"/>
	/// adapter sequence.
	/// </summary>
	[Fact]
	public void Constructor_WhenAdaptersIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception =
			Assert.Throws<ArgumentNullException>(() => new ProviderResolver(OptionsWith(("cloud", "openai")), null!));
		Assert.Equal("adapters", exception.ParamName);
	}

	/// <summary>
	/// Verifies that <see cref="ProviderResolver.Resolve"/> throws for a backend name that is not
	/// configured.
	/// </summary>
	[Fact]
	public void Resolve_WhenBackendUnknown_ThrowsInvalidOperationException()
	{
		// Arrange
		ProviderResolver sut = new(OptionsWith(("cloud", "openai")), [new StubAdapter("openai")]);

		// Act + Assert
		var exception =
			Assert.Throws<InvalidOperationException>(() => sut.Resolve("missing"));
		Assert.Equal("Backend 'missing' is not configured.", exception.Message);
	}

	/// <summary>
	/// Verifies that <see cref="ProviderResolver.Resolve"/> throws when no registered adapter matches
	/// the backend's provider type.
	/// </summary>
	[Fact]
	public void Resolve_WhenNoAdapterForProviderType_ThrowsInvalidOperationException()
	{
		// Arrange: backend wants "anthropic" but only the OpenAI adapter is registered.
		ProviderResolver sut = new(OptionsWith(("cloud", "anthropic")), [new StubAdapter("openai")]);

		// Act + Assert
		var exception =
			Assert.Throws<InvalidOperationException>(() => sut.Resolve("cloud"));
		Assert.Equal(
			"No provider adapter is registered for provider type 'anthropic' required by backend 'cloud'.",
			exception.Message);
	}

	/// <summary>
	/// Verifies that <see cref="ProviderResolver.Resolve"/> rejects a <see langword="null"/> backend name.
	/// </summary>
	[Fact]
	public void Resolve_WhenBackendNameIsNull_ThrowsArgumentNullException()
	{
		// Arrange
		ProviderResolver sut = new(OptionsWith(("cloud", "openai")), [new StubAdapter("openai")]);

		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() => sut.Resolve(null!));
		Assert.Equal("backendName", exception.ParamName);
	}

	/// <summary>
	/// Verifies that <see cref="ProviderResolver.Resolve"/> rejects an empty or whitespace backend name.
	/// </summary>
	/// <param name="backendName">The invalid backend name to resolve.</param>
	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void Resolve_WhenBackendNameIsEmptyOrWhiteSpace_ThrowsArgumentException(string backendName)
	{
		// Arrange
		ProviderResolver sut = new(OptionsWith(("cloud", "openai")), [new StubAdapter("openai")]);

		// Act + Assert
		var exception = Assert.Throws<ArgumentException>(() => sut.Resolve(backendName));
		Assert.Equal("backendName", exception.ParamName);
	}

	/// <summary>A minimal <see cref="IProviderAdapter"/> stub that only reports a provider type.</summary>
	private sealed class StubAdapter(string providerType) : IProviderAdapter
	{
		public string ProviderType { get; } = providerType;

		public IAsyncEnumerable<OllamaChatResponse> StreamChatAsync(
			BackendContext    backend,
			string            upstreamModel,
			OllamaChatRequest request,
			ReasoningEffort?  pinnedEffort,
			CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task<OllamaChatResponse> CompleteChatAsync(
			BackendContext    backend,
			string            upstreamModel,
			OllamaChatRequest request,
			ReasoningEffort?  pinnedEffort,
			CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task<OllamaEmbedResponse> CreateEmbeddingsAsync(
			BackendContext     backend,
			string             upstreamModel,
			OllamaEmbedRequest request,
			CancellationToken  cancellationToken) => throw new NotSupportedException();

		public Task<IReadOnlyList<DiscoveredModel>> DiscoverModelsAsync(
			BackendContext    backend,
			CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task<ModelCapabilities> DetermineCapabilitiesAsync(
			BackendContext    backend,
			DiscoveredModel   model,
			CancellationToken cancellationToken) => throw new NotSupportedException();
	}
}
