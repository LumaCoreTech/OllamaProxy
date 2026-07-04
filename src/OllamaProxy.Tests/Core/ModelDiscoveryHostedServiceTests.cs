// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using OllamaProxy.Configuration;
using OllamaProxy.Contracts.Ollama;
using OllamaProxy.Core;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Tests.Core;

/// <summary>
/// Tests for <see cref="ModelDiscoveryHostedService"/>, the startup hosted service that builds the
/// catalog, publishes it to the router, and logs a backend-grouped overview. The file is organized by
/// member:
/// <list type="number">
///     <item>
///         <description>
///         <see cref="ModelDiscoveryHostedService.StartAsync"/> — publishes the catalog and logs the
///         summary on success, and warns when the catalog ends up empty.
///         </description>
///     </item>
///     <item>
///         <description>
///         <see cref="ModelDiscoveryHostedService.BuildCatalogSummary"/> — backend grouping, ordering,
///         and per-model formatting.
///         </description>
///     </item>
///     <item>
///         <description>
///         <see cref="ModelDiscoveryHostedService.DescribeCapabilities"/> — capability flag rendering
///         and the <c>none</c> fallback.
///         </description>
///     </item>
/// </list>
/// </summary>
[Trait("Category", "Unit")]
public sealed class ModelDiscoveryHostedServiceTests
{
	private static ModelCapabilities Caps(
		bool             completion = true,
		bool             tools      = false,
		bool             vision     = false,
		bool             embeddings = false,
		CapabilitySource source     = CapabilitySource.Default) => new(completion, tools, vision, embeddings, source);

	private static RegisteredModel Model(
		string             name,
		string             backend       = "backend",
		string?            upstream      = null,
		long               contextLength = 8192,
		ModelCapabilities? capabilities  = null) => new(
		name,
		backend,
		upstream ?? name,
		capabilities ?? Caps(),
		contextLength);

	#region StartAsync()

	/// <summary>
	/// Verifies that <see cref="ModelDiscoveryHostedService.StartAsync"/> builds the catalog from the
	/// explicit registry, publishes it to the router, and logs the backend-grouped summary at
	/// information level without warning.
	/// </summary>
	[Fact]
	public async Task StartAsync_WhenCatalogHasModels_PublishesAndLogsSummary()
	{
		// Arrange: an Explicit-mode registry exposes only its pins; metadata-only discovery lists no models,
		// so the published catalog is exactly the configured pin.
		IOptions<ProxyOptions> options = OptionsFor(
			OperatingMode.Explicit,
			("cloud", new BackendOptions { BaseUrl = "https://x/v1", ProviderType = "openai" }),
			models: [Registration("gpt-4o", contextLength: 128000)]);

		ModelRouter router = new();
		var log = new CapturingLogger();
		ModelDiscoveryHostedService sut = CreateService(options, router, log);

		// Act
		await sut.StartAsync(CancellationToken.None);

		// Assert: the catalog reached the router and the summary was logged, with no warning.
		RegisteredModel published = Assert.Single(router.GetModels());
		Assert.Equal("gpt-4o", published.Name);
		Assert.DoesNotContain(log.Entries, entry => entry.Level == LogLevel.Warning);
		Assert.Contains(
			log.Entries,
			entry =>
				entry.Level == LogLevel.Information && entry.Message.Contains(
					"Model catalog ready:",
					StringComparison.Ordinal));
	}

	/// <summary>
	/// Verifies that <see cref="ModelDiscoveryHostedService.StartAsync"/> warns and skips the summary
	/// when discovery yields an empty catalog, so the proxy still boots but the misconfiguration is loud.
	/// </summary>
	[Fact]
	public async Task StartAsync_WhenCatalogIsEmpty_WarnsAndSkipsSummary()
	{
		// Arrange: Explicit mode with no registry entries and a backend that lists no models produces an
		// empty catalog.
		IOptions<ProxyOptions> options = OptionsFor(
			OperatingMode.Explicit,
			("cloud", new BackendOptions { BaseUrl = "https://x/v1", ProviderType = "openai" }),
			models: []);

		ModelRouter router = new();
		var log = new CapturingLogger();
		ModelDiscoveryHostedService sut = CreateService(options, router, log);

		// Act
		await sut.StartAsync(CancellationToken.None);

		// Assert
		Assert.Empty(router.GetModels());
		Assert.Contains(
			log.Entries,
			entry =>
				entry.Level == LogLevel.Warning && entry.Message.Contains("empty catalog", StringComparison.Ordinal));
		Assert.DoesNotContain(
			log.Entries,
			entry =>
				entry.Message.Contains("Model catalog ready:", StringComparison.Ordinal));
	}

	/// <summary>
	/// Verifies that <see cref="ModelDiscoveryHostedService.StopAsync"/> completes synchronously without
	/// side effects, as the service does no teardown work.
	/// </summary>
	[Fact]
	public Task StopAsync_WhenCalled_CompletesWithoutError()
	{
		// Arrange
		IOptions<ProxyOptions> options = OptionsFor(
			OperatingMode.Explicit,
			("cloud", new BackendOptions { BaseUrl = "https://x/v1", ProviderType = "openai" }),
			models: [Registration("gpt-4o", contextLength: 128000)]);
		ModelDiscoveryHostedService sut = CreateService(options, new ModelRouter(), new CapturingLogger());

		// Act + Assert: should not throw and should complete.
		return sut.StopAsync(CancellationToken.None);
	}

	#endregion

	#region BuildCatalogSummary()

	/// <summary>
	/// Verifies that <see cref="ModelDiscoveryHostedService.BuildCatalogSummary"/> renders a header with
	/// the model and backend counts, groups models under their backend, and emits one detail line per
	/// model carrying the client name, upstream model, context length, capabilities, and provenance.
	/// </summary>
	[Fact]
	public void BuildCatalogSummary_WhenModelsSpanBackends_GroupsAndFormatsEachModel()
	{
		// Arrange: two backends, with the cloud one carrying a tool-capable model from provider metadata.
		RegisteredModel[] models =
		[
			Model(
				"gpt-4o",
				"cloud",
				upstream: "gpt-4o-2024",
				contextLength: 128000,
				capabilities: Caps(tools: true, vision: true, source: CapabilitySource.ProviderMetadata)),
			Model(
				"qwen",
				"local",
				upstream: "qwen2.5-coder-7b",
				contextLength: 8192,
				capabilities: Caps(source: CapabilitySource.Default))
		];

		// Act
		string summary = ModelDiscoveryHostedService.BuildCatalogSummary(models);

		// Assert
		Assert.Contains("Model catalog ready: 2 model(s) across 2 backend(s).", summary, StringComparison.Ordinal);
		Assert.Contains("Backend 'cloud' (1 model(s)):", summary, StringComparison.Ordinal);
		Assert.Contains(
			"- gpt-4o -> upstream 'gpt-4o-2024' | context 128000 | caps: completion, tools, vision | source ProviderMetadata",
			summary,
			StringComparison.Ordinal);
		Assert.Contains("Backend 'local' (1 model(s)):", summary, StringComparison.Ordinal);
		Assert.Contains(
			"- qwen -> upstream 'qwen2.5-coder-7b' | context 8192 | caps: completion | source Default",
			summary,
			StringComparison.Ordinal);
	}

	/// <summary>
	/// Verifies that <see cref="ModelDiscoveryHostedService.BuildCatalogSummary"/> orders backends and
	/// the models within them case-insensitively, so the startup log is deterministic regardless of
	/// catalog iteration order.
	/// </summary>
	[Fact]
	public void BuildCatalogSummary_WhenOrderingVaries_SortsBackendsAndModels()
	{
		// Arrange: deliberately out-of-order backends and models.
		RegisteredModel[] models =
		[
			Model("Zeta", "zulu"),
			Model("alpha", "alpha"),
			Model("Beta", "alpha")
		];

		// Act
		string summary = ModelDiscoveryHostedService.BuildCatalogSummary(models);

		// Assert: 'alpha' backend precedes 'zulu', and within 'alpha', 'alpha' precedes 'Beta'.
		int alphaBackend = summary.IndexOf("Backend 'alpha'", StringComparison.Ordinal);
		int zuluBackend = summary.IndexOf("Backend 'zulu'", StringComparison.Ordinal);
		Assert.True(alphaBackend >= 0 && zuluBackend > alphaBackend);

		int alphaModel = summary.IndexOf("- alpha ->", StringComparison.Ordinal);
		int betaModel = summary.IndexOf("- Beta ->", StringComparison.Ordinal);
		Assert.True(alphaModel >= 0 && betaModel > alphaModel);
	}

	#endregion

	#region DescribeCapabilities()

	/// <summary>
	/// Verifies that <see cref="ModelDiscoveryHostedService.DescribeCapabilities"/> renders the enabled
	/// flags as a comma-separated list in a stable order.
	/// </summary>
	/// <param name="completion">Whether completion is enabled.</param>
	/// <param name="tools">Whether tools are enabled.</param>
	/// <param name="vision">Whether vision is enabled.</param>
	/// <param name="embeddings">Whether embeddings are enabled.</param>
	/// <param name="expected">The expected comma-separated rendering.</param>
	[Theory]
	[InlineData(true, false, false, false, "completion")]
	[InlineData(true, true, false, false, "completion, tools")]
	[InlineData(true, true, true, false, "completion, tools, vision")]
	[InlineData(true, true, true, true, "completion, tools, vision, embeddings")]
	[InlineData(false, false, false, true, "embeddings")]
	public void DescribeCapabilities_WhenFlagsEnabled_RendersOrderedList(
		bool   completion,
		bool   tools,
		bool   vision,
		bool   embeddings,
		string expected)
	{
		// Act
		string rendered = ModelDiscoveryHostedService.DescribeCapabilities(Caps(completion, tools, vision, embeddings));

		// Assert
		Assert.Equal(expected, rendered);
	}

	/// <summary>
	/// Verifies that <see cref="ModelDiscoveryHostedService.DescribeCapabilities"/> falls back to
	/// <c>none</c> when a model advertises no capabilities at all.
	/// </summary>
	[Fact]
	public void DescribeCapabilities_WhenNoFlagsEnabled_RendersNone()
	{
		// Act
		string rendered = ModelDiscoveryHostedService.DescribeCapabilities(
			Caps(completion: false, tools: false, vision: false, embeddings: false));

		// Assert
		Assert.Equal("none", rendered);
	}

	#endregion

	#region Test infrastructure

	private static IOptions<ProxyOptions> OptionsFor(
		OperatingMode                         mode,
		(string Name, BackendOptions Backend) backend,
		IList<ModelRegistrationOptions>       models)
	{
		// Each backend owns its mode and registry, so the mode and the pins are applied to the backend itself
		// rather than to a section-level mode or model list that no longer exists.
		backend.Backend.Mode = mode;
		foreach (ModelRegistrationOptions model in models) backend.Backend.Models.Add(model);

		ProxyOptions options = new();
		options.Backends[backend.Name] = backend.Backend;

		return Options.Create(options);
	}

	private static ModelRegistrationOptions Registration(string name, int contextLength) =>
		new() { Name = name, ContextLength = contextLength };

	private static ModelDiscoveryHostedService CreateService(
		IOptions<ProxyOptions> options,
		ModelRouter            router,
		CapturingLogger        log)
	{
		ModelCatalogBuilder builder = new(
			options,
			new StubResolver(new EmptyDiscoveryAdapter()),
			new ModeFromOptionsCatalog(),
			NullLogger<ModelCatalogBuilder>.Instance);

		return new ModelDiscoveryHostedService(builder, router, log);
	}

	/// <summary>A resolver that returns the same stub adapter for every backend name.</summary>
	private sealed class StubResolver(IProviderAdapter adapter) : IProviderResolver
	{
		public ResolvedBackend Resolve(string backendName) => new(adapter, new BackendContext(backendName));

		public ResolvedBackend ResolveDraft(BackendOptions draft) => new(adapter, new BackendContext("(draft)", draft));
	}

	/// <summary>
	/// A provider adapter that lists no models, so an Explicit backend's metadata-only discovery enriches
	/// nothing and the catalog is composed purely from the configured registry pins — exactly what these
	/// summary/empty-catalog tests exercise. The chat and embedding members are never reached.
	/// </summary>
	private sealed class EmptyDiscoveryAdapter : IProviderAdapter
	{
		public string ProviderType => "openai";

		public Task<IReadOnlyList<DiscoveredModel>> DiscoverModelsAsync(
			BackendContext    backend,
			CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DiscoveredModel>>([]);

		public Task<ModelCapabilities> DetermineCapabilitiesAsync(
			BackendContext    backend,
			DiscoveredModel   model,
			CancellationToken cancellationToken) => Task.FromResult(ModelCapabilities.CompletionOnly);

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
	}

	/// <summary>
	/// A provider catalog that resolves a backend's mode from its explicit <see cref="BackendOptions.Mode"/>,
	/// falling back to <see cref="OperatingMode.Explicit"/> when unset — enough for the catalog builder, which
	/// only consults <see cref="IProviderCatalog.ResolveMode"/>.
	/// </summary>
	private sealed class ModeFromOptionsCatalog : IProviderCatalog
	{
		public IReadOnlyList<ProviderDescriptor> Providers => [];

		public bool IsSupported(string? providerType) => true;

		public OperatingMode DefaultModeFor(string? providerType) => OperatingMode.Explicit;

		public string DefaultBaseUrlFor(string? providerType) => string.Empty;

		public string DisplayNameFor(string? providerType) => providerType ?? string.Empty;

		public OperatingMode ResolveMode(BackendOptions backend) => backend.Mode ?? OperatingMode.Explicit;
	}

	/// <summary>An <see cref="ILogger{T}"/> that records each entry's level and rendered message.</summary>
	private sealed class CapturingLogger : ILogger<ModelDiscoveryHostedService>
	{
		public List<(LogLevel Level, string Message)> Entries { get; } = [];

		public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(
			LogLevel                         logLevel,
			EventId                          eventId,
			TState                           state,
			Exception?                       exception,
			Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));

		private sealed class NullScope : IDisposable
		{
			public static readonly NullScope Instance = new();
			public                 void      Dispose() { }
		}
	}

	#endregion
}
