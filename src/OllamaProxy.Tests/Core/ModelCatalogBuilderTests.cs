// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OllamaProxy.Configuration;
using OllamaProxy.Contracts.Ollama;
using OllamaProxy.Core;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Tests.Core;

/// <summary>
/// Tests for <see cref="ModelCatalogBuilder"/>'s catalog assembly: name-collision handling during
/// auto-exposure discovery, registry-entry materialization, and the client-facing prefixing that names
/// discovered and registry models identically. The story covers the two collision shapes the builder
/// distinguishes:
/// <list type="number">
///     <item>
///         <description>
///         A discovered model whose name matches an explicit registry entry is skipped quietly
///         (expected — the registry wins by design) and logged at information level.
///         </description>
///     </item>
///     <item>
///         <description>
///         A discovered model whose name was already auto-exposed by another backend is skipped and
///         logged as a warning, because it becomes unreachable under that name.
///         </description>
///     </item>
/// </list>
/// </summary>
[Trait("Category", "Unit")]
public sealed class ModelCatalogBuilderTests
{
	/// <summary>
	/// Verifies that when two auto-exposed backends report the same model name, the builder exposes
	/// exactly one copy and warns that the other is shadowed and unreachable.
	/// </summary>
	[Fact]
	public async Task BuildAsync_WhenTwoBackendsShareModelName_ExposesOneAndWarnsAboutCollision()
	{
		// Arrange: PlugAndPlay auto-exposes both backends; each reports a "shared" model with a context.
		StubAdapter adapter = new(
			models: [Discovered("shared", contextLength: 4096)],
			providerType: "openai");

		IOptions<ProxyOptions> options = OptionsFor(
			OperatingMode.PlugAndPlay,
			("vllm", "openai"),
			("venice", "openai"));

		var log = new CapturingLogger();
		var sut = new ModelCatalogBuilder(options, new StubResolver(adapter), new StubCatalog(), log);

		// Act
		IReadOnlyList<RegisteredModel> catalog = await sut.BuildAsync(CancellationToken.None);

		// Assert: only one "shared" survives, and a collision warning names the model.
		Assert.Single(catalog, model => model.Name == "shared");
		Assert.Contains(
			log.Entries,
			entry =>
				entry.Level == LogLevel.Warning &&
				entry.Message.Contains("collision", StringComparison.OrdinalIgnoreCase) &&
				entry.Message.Contains("shared", StringComparison.Ordinal));
	}

	/// <summary>
	/// Verifies that when a discovered model collides with an explicit registry entry, the registry
	/// entry is kept, the discovered copy is skipped, and the skip is logged at information level
	/// rather than warned, since a registry win is the documented, expected behavior.
	/// </summary>
	[Fact]
	public async Task BuildAsync_WhenDiscoveredModelMatchesRegistryEntry_KeepsRegistryAndLogsInformation()
	{
		// Arrange: a registry-only backend "cloud" pins "shared", and a PlugAndPlay backend "local" also reports
		// "shared" through discovery. The registry entry carries no capability override, so its capability source
		// is Default — the collision logic must not rely on that to recognize a registry entry.
		StubAdapter adapter = new(
			models: [Discovered("shared", contextLength: 4096)],
			providerType: "openai");

		IOptions<ProxyOptions> options = OptionsFor(
			OperatingMode.PlugAndPlay,
			("cloud", "openai"),
			("local", "openai"));
		options.Value.Backends["cloud"].Mode = OperatingMode.Explicit;
		options.Value.Backends["cloud"]
			.Models.Add(
				new ModelRegistrationOptions
				{
					Name = "shared",
					ContextLength = 2048
				});

		var log = new CapturingLogger();
		var sut = new ModelCatalogBuilder(options, new StubResolver(adapter), new StubCatalog(), log);

		// Act
		IReadOnlyList<RegisteredModel> catalog = await sut.BuildAsync(CancellationToken.None);

		// Assert: the surviving "shared" is the registry entry on "cloud", and the skip was info, not warning.
		RegisteredModel shared = Assert.Single(catalog, model => model.Name == "shared");
		Assert.Equal("cloud", shared.BackendName);
		Assert.Contains(
			log.Entries,
			entry =>
				entry.Level == LogLevel.Information &&
				entry.Message.Contains("registry entry already pins this name", StringComparison.Ordinal));
		Assert.DoesNotContain(
			log.Entries,
			entry =>
				entry.Level == LogLevel.Warning &&
				entry.Message.Contains("collision", StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// Verifies that <see cref="OperatingMode.PlugAndPlay"/> ignores the model registry entirely: a pinned
	/// entry the backend does not discover is not exposed, only the discovered models are, and a warning is
	/// logged so the ignored configuration is never silent. This is the counterpart to the Hybrid
	/// registry-win test above — the same kind of pin that wins in Hybrid is dropped in PlugAndPlay, which
	/// is what keeps the two modes distinct.
	/// </summary>
	[Fact]
	public async Task BuildAsync_WhenPlugAndPlayHasRegistryEntries_IgnoresRegistryAndWarns()
	{
		// Arrange: PlugAndPlay with a backend that discovers "discovered-model", plus a registry entry
		// "registry-only" the backend never reports. Were the registry honored (as in Hybrid), the pin
		// would surface; in PlugAndPlay it must not.
		StubAdapter adapter = new(
			models: [Discovered("discovered-model", contextLength: 4096)],
			providerType: "openai");

		IOptions<ProxyOptions> options = OptionsFor(OperatingMode.PlugAndPlay, ("local", "openai"));
		options.Value.Backends["local"]
			.Models.Add(new ModelRegistrationOptions { Name = "registry-only", ContextLength = 2048 });

		var log = new CapturingLogger();
		var sut = new ModelCatalogBuilder(options, new StubResolver(adapter), new StubCatalog(), log);

		// Act
		IReadOnlyList<RegisteredModel> catalog = await sut.BuildAsync(CancellationToken.None);

		// Assert: only the discovered model is exposed, proving the pinned entry was ignored, not merged.
		RegisteredModel model = Assert.Single(catalog);
		Assert.Equal("discovered-model", model.Name);

		// Assert: the ignored registry is surfaced as a single warning naming the count and the Hybrid path.
		(LogLevel Level, string Message) warning = Assert.Single(
			log.Entries,
			entry => entry.Level == LogLevel.Warning);
		Assert.Equal(
			"Backend local is in PlugAndPlay mode and does not honor its model registry; 1 configured model(s) " +
			"will be ignored. This mode exposes every discovered model. Switch the backend to Hybrid mode to pin " +
			"or override models while still auto-exposing it.",
			warning.Message);
	}

	/// <summary>
	/// Verifies that when both backends carry a distinct <see cref="BackendOptions.ModelPrefix"/>, the
	/// same upstream model is exposed under two prefixed, collision-free names, each still requesting
	/// the bare upstream identifier.
	/// </summary>
	[Fact]
	public async Task BuildAsync_WhenBackendsPrefixModels_ExposesBothUnderPrefixedNames()
	{
		// Arrange: both backends report "gemma2-27b"; distinct prefixes keep them apart.
		StubAdapter adapter = new(
			models: [Discovered("gemma2-27b", contextLength: 8192)],
			providerType: "openai");

		IOptions<ProxyOptions> options = OptionsFor(
			OperatingMode.PlugAndPlay,
			("vllm", "openai"),
			("venice", "openai"));
		options.Value.Backends["vllm"].ModelPrefix = "vllm";
		options.Value.Backends["venice"].ModelPrefix = "venice";

		var log = new CapturingLogger();
		var sut = new ModelCatalogBuilder(options, new StubResolver(adapter), new StubCatalog(), log);

		// Act
		IReadOnlyList<RegisteredModel> catalog = await sut.BuildAsync(CancellationToken.None);

		// Assert: two distinct client names, no collision warning, upstream identifier unchanged.
		RegisteredModel vllm = Assert.Single(catalog, model => model.Name == "vllm/gemma2-27b");
		RegisteredModel venice = Assert.Single(catalog, model => model.Name == "venice/gemma2-27b");
		Assert.Equal("gemma2-27b", vllm.UpstreamModel);
		Assert.Equal("gemma2-27b", venice.UpstreamModel);
		Assert.DoesNotContain(log.Entries, entry => entry.Level == LogLevel.Warning);
	}

	/// <summary>
	/// Verifies that a backend without a prefix exposes its discovered models under their bare upstream
	/// identifier, so single-backend deployments keep the shorter name.
	/// </summary>
	[Fact]
	public async Task BuildAsync_WhenBackendHasNoPrefix_ExposesBareModelName()
	{
		// Arrange
		StubAdapter adapter = new(
			models: [Discovered("gpt-4o", contextLength: 128000)],
			providerType: "openai");

		IOptions<ProxyOptions> options = OptionsFor(OperatingMode.PlugAndPlay, ("cloud", "openai"));

		var sut = new ModelCatalogBuilder(options, new StubResolver(adapter), new StubCatalog(), new CapturingLogger());

		// Act
		IReadOnlyList<RegisteredModel> catalog = await sut.BuildAsync(CancellationToken.None);

		// Assert
		RegisteredModel model = Assert.Single(catalog);
		Assert.Equal("gpt-4o", model.Name);
		Assert.Equal("gpt-4o", model.UpstreamModel);
	}

	/// <summary>
	/// Verifies that a discovered model whose context length is neither reported by the backend nor
	/// configured is skipped with a warning rather than crashing discovery, so one silent backend
	/// (e.g. one that omits a context window) never blocks startup for every other model.
	/// </summary>
	[Fact]
	public async Task BuildAsync_WhenDiscoveredModelHasNoContextLength_SkipsModelAndWarns()
	{
		// Arrange: the backend reports a model but advertises no context length, and none is configured.
		StubAdapter adapter = new(
			models: [DiscoveredWithoutContext("mystery-model")],
			providerType: "openai");

		IOptions<ProxyOptions> options = OptionsFor(OperatingMode.PlugAndPlay, ("venice", "openai"));

		var log = new CapturingLogger();
		var sut = new ModelCatalogBuilder(options, new StubResolver(adapter), new StubCatalog(), log);

		// Act
		IReadOnlyList<RegisteredModel> catalog = await sut.BuildAsync(CancellationToken.None);

		// Assert: the model is not exposed, and a warning explains why and how to recover.
		Assert.Empty(catalog);
		Assert.Contains(
			log.Entries,
			entry =>
				entry.Level == LogLevel.Warning &&
				entry.Message.Contains("mystery-model", StringComparison.Ordinal) &&
				entry.Message.Contains("did not report a context length", StringComparison.Ordinal));
	}

	/// <summary>
	/// Verifies that a backend reporting a context length is still exposed even when another model on
	/// the same backend is skipped for lacking one, proving the skip is per-model and not fatal.
	/// </summary>
	[Fact]
	public async Task BuildAsync_WhenSomeDiscoveredModelsLackContextLength_ExposesOnlyThoseWithOne()
	{
		// Arrange: one model carries a context length, the other does not.
		StubAdapter adapter = new(
			models: [Discovered("good-model", contextLength: 8192), DiscoveredWithoutContext("bad-model")],
			providerType: "openai");

		IOptions<ProxyOptions> options = OptionsFor(OperatingMode.PlugAndPlay, ("venice", "openai"));

		var sut = new ModelCatalogBuilder(options, new StubResolver(adapter), new StubCatalog(), new CapturingLogger());

		// Act
		IReadOnlyList<RegisteredModel> catalog = await sut.BuildAsync(CancellationToken.None);

		// Assert: only the model with a known window survives.
		RegisteredModel model = Assert.Single(catalog);
		Assert.Equal("good-model", model.Name);
		Assert.Equal(8192, model.ContextLength);
	}

	/// <summary>
	/// Verifies that an auto-exposed model whose backend reports no context window is still exposed when the
	/// backend configures a default, the default serving as the effective window's fallback. This complements
	/// the per-model skip above: the same context-less model is dropped without a default but rescued with one.
	/// </summary>
	[Fact]
	public async Task BuildAsync_WhenDiscoveredModelLacksContextButBackendHasDefault_ExposesWithDefault()
	{
		// Arrange: a context-less discovered model on a backend whose default supplies the fallback window.
		StubAdapter adapter = new(
			models: [DiscoveredWithoutContext("mystery-model")],
			providerType: "openai");

		IOptions<ProxyOptions> options = OptionsFor(OperatingMode.PlugAndPlay, ("venice", "openai"));
		options.Value.Backends["venice"].ContextLength = 16384;

		var sut = new ModelCatalogBuilder(options, new StubResolver(adapter), new StubCatalog(), new CapturingLogger());

		// Act
		IReadOnlyList<RegisteredModel> catalog = await sut.BuildAsync(CancellationToken.None);

		// Assert: the model is exposed with the backend default as its effective window.
		RegisteredModel model = Assert.Single(catalog);
		Assert.Equal("mystery-model", model.Name);
		Assert.Equal(16384, model.ContextLength);
	}

	/// <summary>
	/// Verifies that an auto-exposed model whose reported context window exceeds the backend default keeps its
	/// larger reported window — the default is a fallback, never a narrowing clamp. This is the regression that
	/// previously capped every Venice model at the backend default instead of honoring the provider's metadata.
	/// </summary>
	[Fact]
	public async Task BuildAsync_WhenReportedContextExceedsBackendDefault_ExposesReportedWindow()
	{
		// Arrange: the provider reports a 128k window while the backend configures a narrower 32k default.
		StubAdapter adapter = new(
			models: [Discovered("venice-uncensored", contextLength: 131072)],
			providerType: "openai");

		IOptions<ProxyOptions> options = OptionsFor(OperatingMode.PlugAndPlay, ("venice", "openai"));
		options.Value.Backends["venice"].ContextLength = 32768;

		var sut = new ModelCatalogBuilder(options, new StubResolver(adapter), new StubCatalog(), new CapturingLogger());

		// Act
		IReadOnlyList<RegisteredModel> catalog = await sut.BuildAsync(CancellationToken.None);

		// Assert: the reported 128k wins over the narrower 32k default — no clamping.
		RegisteredModel model = Assert.Single(catalog);
		Assert.Equal("venice-uncensored", model.Name);
		Assert.Equal(131072, model.ContextLength);
	}

	/// <summary>
	/// Verifies that a missing context length on an explicit registry entry is fatal — unlike the
	/// discovery path, an operator who pins a model without a resolvable window has made a
	/// configuration error the proxy must surface loudly at startup.
	/// </summary>
	[Fact]
	public async Task BuildAsync_WhenRegistryEntryHasNoContextLength_ThrowsInvalidOperationException()
	{
		// Arrange: Explicit mode with a pinned model but no context length anywhere (entry or backend default).
		StubAdapter adapter = new(models: [], providerType: "openai");

		IOptions<ProxyOptions> options = OptionsFor(OperatingMode.Explicit, ("cloud", "openai"));
		options.Value.Backends["cloud"].Models.Add(new ModelRegistrationOptions { Name = "pinned" });

		var sut = new ModelCatalogBuilder(options, new StubResolver(adapter), new StubCatalog(), new CapturingLogger());

		// Act + Assert
		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.BuildAsync(CancellationToken.None));
		Assert.Equal(
			"Model 'pinned' on backend 'cloud' has no context length: the backend reported none, the backend " +
			"default is unset, and the registry entry does not specify one. Set 'ContextLength' on the " +
			"'OllamaProxy:Backends:cloud:Models' entry, or 'OllamaProxy:Backends:cloud:ContextLength' as a " +
			"backend default, so the proxy can advertise and enforce the correct context window.",
			ex.Message);
	}

	/// <summary>
	/// Verifies that a registry entry with no capability flags set resolves to completion-only with a
	/// <see cref="CapabilitySource.Default"/> source, proving the proxy's baseline modality is assumed
	/// for a minimally pinned model and that the existing zero-override behavior is preserved.
	/// </summary>
	[Fact]
	public async Task BuildAsync_WhenRegistryEntryHasNoCapabilityFlags_ResolvesCompletionOnlyAsDefault()
	{
		// Arrange: Explicit mode with a model pinned by name and context only — no capability flags.
		StubAdapter adapter = new(models: [], providerType: "openai");

		IOptions<ProxyOptions> options = OptionsFor(OperatingMode.Explicit, ("cloud", "openai"));
		options.Value.Backends["cloud"]
			.Models.Add(new ModelRegistrationOptions { Name = "pinned", ContextLength = 4096 });

		var sut = new ModelCatalogBuilder(options, new StubResolver(adapter), new StubCatalog(), new CapturingLogger());

		// Act
		IReadOnlyList<RegisteredModel> catalog = await sut.BuildAsync(CancellationToken.None);

		// Assert: completion is assumed, every additive flag is off, and no override marks it Configured.
		RegisteredModel model = Assert.Single(catalog);
		Assert.True(model.Capabilities.SupportsCompletion);
		Assert.False(model.Capabilities.SupportsTools);
		Assert.False(model.Capabilities.SupportsVision);
		Assert.False(model.Capabilities.SupportsEmbeddings);
		Assert.Equal(CapabilitySource.Default, model.Capabilities.Source);
	}

	/// <summary>
	/// Verifies that a registry entry pinning an embedding-only model (completion explicitly disabled,
	/// embeddings enabled) is exposed with exactly those capabilities and a
	/// <see cref="CapabilitySource.Configured"/> source, closing the gap where the registry could not
	/// previously express a model that does not chat.
	/// </summary>
	[Fact]
	public async Task BuildAsync_WhenRegistryEntryIsEmbeddingOnly_ResolvesEmbeddingsWithoutCompletion()
	{
		// Arrange: Explicit mode with a model that opts out of completion and into embeddings.
		StubAdapter adapter = new(models: [], providerType: "openai");

		IOptions<ProxyOptions> options = OptionsFor(OperatingMode.Explicit, ("cloud", "openai"));
		options.Value.Backends["cloud"]
			.Models.Add(
				new ModelRegistrationOptions
				{
					Name = "nomic-embed-text",
					ContextLength = 8192,
					SupportsCompletion = false,
					SupportsEmbeddings = true
				});

		var sut = new ModelCatalogBuilder(options, new StubResolver(adapter), new StubCatalog(), new CapturingLogger());

		// Act
		IReadOnlyList<RegisteredModel> catalog = await sut.BuildAsync(CancellationToken.None);

		// Assert: the model is exposed as embedding-only, and any pinned flag marks the source Configured.
		RegisteredModel model = Assert.Single(catalog);
		Assert.False(model.Capabilities.SupportsCompletion);
		Assert.False(model.Capabilities.SupportsTools);
		Assert.False(model.Capabilities.SupportsVision);
		Assert.True(model.Capabilities.SupportsEmbeddings);
		Assert.Equal(CapabilitySource.Configured, model.Capabilities.Source);
	}

	/// <summary>
	/// Verifies that a registry entry pinning a model that supports both completion and embeddings
	/// resolves to exactly that combination, since some models (e.g. certain Qwen/GTE variants) serve
	/// both endpoints and the explicit flags must not be mutually exclusive.
	/// </summary>
	[Fact]
	public async Task BuildAsync_WhenRegistryEntrySupportsCompletionAndEmbeddings_ResolvesBoth()
	{
		// Arrange: Explicit mode with a model that explicitly pins both completion and embeddings on.
		StubAdapter adapter = new(models: [], providerType: "openai");

		IOptions<ProxyOptions> options = OptionsFor(OperatingMode.Explicit, ("cloud", "openai"));
		options.Value.Backends["cloud"]
			.Models.Add(
				new ModelRegistrationOptions
				{
					Name = "qwen3-embed-and-chat",
					ContextLength = 32768,
					SupportsCompletion = true,
					SupportsEmbeddings = true
				});

		var sut = new ModelCatalogBuilder(options, new StubResolver(adapter), new StubCatalog(), new CapturingLogger());

		// Act
		IReadOnlyList<RegisteredModel> catalog = await sut.BuildAsync(CancellationToken.None);

		// Assert: both endpoints are advertised, and the pinned flags mark the source Configured.
		RegisteredModel model = Assert.Single(catalog);
		Assert.True(model.Capabilities.SupportsCompletion);
		Assert.True(model.Capabilities.SupportsEmbeddings);
		Assert.False(model.Capabilities.SupportsTools);
		Assert.False(model.Capabilities.SupportsVision);
		Assert.Equal(CapabilitySource.Configured, model.Capabilities.Source);
	}

	/// <summary>
	/// Verifies that an explicit registry entry on a backend with a <see cref="BackendOptions.ModelPrefix"/> is
	/// exposed under its prefixed client-facing name while the upstream identifier the proxy requests stays
	/// bare, so a pinned model is named exactly as the same model would be when auto-exposed.
	/// </summary>
	[Fact]
	public async Task BuildAsync_WhenRegistryEntryHasPrefix_ExposesPrefixedNameWithBareUpstream()
	{
		// Arrange: an Explicit backend pins the bare name "gemma2-27b" and carries a "vllm" prefix.
		StubAdapter adapter = new(models: [], providerType: "openai");

		IOptions<ProxyOptions> options = OptionsFor(OperatingMode.Explicit, ("cloud", "openai"));
		options.Value.Backends["cloud"].ModelPrefix = "vllm";
		options.Value.Backends["cloud"]
			.Models.Add(new ModelRegistrationOptions { Name = "gemma2-27b", ContextLength = 8192 });

		var sut = new ModelCatalogBuilder(options, new StubResolver(adapter), new StubCatalog(), new CapturingLogger());

		// Act
		IReadOnlyList<RegisteredModel> catalog = await sut.BuildAsync(CancellationToken.None);

		// Assert: the client-facing name is prefixed; the upstream id stays bare.
		RegisteredModel model = Assert.Single(catalog);
		Assert.Equal("vllm/gemma2-27b", model.Name);
		Assert.Equal("gemma2-27b", model.UpstreamModel);
	}

	/// <summary>
	/// Verifies that in <see cref="OperatingMode.Hybrid"/> a registry entry pinning the bare upstream id and a
	/// discovered model reporting that same id resolve to the <em>same</em> prefixed client-facing name —
	/// exposed exactly once, from the registry. This proves the registry entry is prefixed a single time at
	/// exposure (never doubly, never left bare) and still wins the name collision against the discovered copy.
	/// </summary>
	[Fact]
	public async Task BuildAsync_WhenHybridRegistryEntryAndDiscoveryShareName_PrefixesOnceAndRegistryWins()
	{
		// Arrange: a Hybrid backend with a "vllm" prefix. Discovery reports "gemma2-27b" and the registry pins
		// the same bare id. Discovery alone would expose "vllm/gemma2-27b"; the registry entry, prefixed once at
		// exposure, claims that very name first, so the discovered copy is shadowed. The entry's distinct 4096
		// window (vs. the discovered 8192) makes the surviving row identifiable as the pinned one.
		StubAdapter adapter = new(
			models: [Discovered("gemma2-27b", contextLength: 8192)],
			providerType: "openai");

		IOptions<ProxyOptions> options = OptionsFor(OperatingMode.Hybrid, ("cloud", "openai"));
		options.Value.Backends["cloud"].ModelPrefix = "vllm";
		options.Value.Backends["cloud"]
			.Models.Add(new ModelRegistrationOptions { Name = "gemma2-27b", ContextLength = 4096 });

		var sut = new ModelCatalogBuilder(options, new StubResolver(adapter), new StubCatalog(), new CapturingLogger());

		// Act
		IReadOnlyList<RegisteredModel> catalog = await sut.BuildAsync(CancellationToken.None);

		// Assert: exactly one entry, named with a single prefix (not bare, not "vllm/vllm/..."); its pinned 4096
		// window confirms the survivor is the registry row, not the discovered copy.
		RegisteredModel model = Assert.Single(catalog);
		Assert.Equal("vllm/gemma2-27b", model.Name);
		Assert.Equal("gemma2-27b", model.UpstreamModel);
		Assert.Equal(4096, model.ContextLength);
	}

	/// <summary>
	/// Verifies that in <see cref="OperatingMode.Hybrid"/> a registry pin without an explicit
	/// <see cref="ModelRegistrationOptions.ContextLength"/> inherits the window the backend reports for its
	/// upstream id during discovery — even when no backend default is configured. This is the runtime
	/// counterpart to the admin reconciliation preview: both resolve a pin's window through the shared
	/// three-tier rule (explicit override, then reported, then backend default), so a Hybrid pin is sized
	/// identically whether previewed in the admin surface or exposed at startup. It is the regression the former
	/// pre-discovery materialization could not satisfy — a pin without an override would have thrown for want of
	/// a context length even though the backend reports one.
	/// </summary>
	[Fact]
	public async Task BuildAsync_WhenHybridPinHasNoExplicitContext_InheritsReportedWindow()
	{
		// Arrange: a Hybrid backend whose discovery reports "venice-uncensored" with a 128k window. The registry
		// pins that same upstream id but sets NO ContextLength, and the backend configures NO default — so the
		// only window available is the one discovery reports. Pre-reorder this threw (the pin was materialized
		// before discovery ran); now discovery runs first, so the pin inherits the reported window by upstream id.
		StubAdapter adapter = new(
			models: [Discovered("venice-uncensored", contextLength: 131072)],
			providerType: "openai");

		IOptions<ProxyOptions> options = OptionsFor(OperatingMode.Hybrid, ("venice", "openai"));
		options.Value.Backends["venice"].Models.Add(new ModelRegistrationOptions { Name = "venice-uncensored" });

		var sut = new ModelCatalogBuilder(options, new StubResolver(adapter), new StubCatalog(), new CapturingLogger());

		// Act
		IReadOnlyList<RegisteredModel> catalog = await sut.BuildAsync(CancellationToken.None);

		// Assert: the pin wins the name collision and carries the discovered 128k window it inherited — not a
		// throw, and not a narrower value. The single entry is the pinned row sized by the backend's report.
		RegisteredModel model = Assert.Single(catalog);
		Assert.Equal("venice-uncensored", model.Name);
		Assert.Equal("venice-uncensored", model.UpstreamModel);
		Assert.Equal(131072, model.ContextLength);
	}

	/// <summary>
	/// Verifies that in <see cref="OperatingMode.Hybrid"/> a registry pin without an explicit
	/// <see cref="ModelRegistrationOptions.ContextLength"/> falls back to the backend default when the backend
	/// reports no window for its upstream id — the last tier of the shared three-tier rule (explicit override,
	/// then reported, then backend default). This proves the default still fills the gap for a pinned model when
	/// discovery yields nothing, exactly as it does for an auto-exposed one.
	/// </summary>
	[Fact]
	public async Task BuildAsync_WhenHybridPinHasNoExplicitContextAndBackendReportsNone_FallsBackToBackendDefault()
	{
		// Arrange: a Hybrid backend whose discovery reports "venice-uncensored" but WITHOUT a window. The pin sets
		// no ContextLength, so neither an override nor a reported value exists; only the backend default (16k)
		// remains. The pin must resolve to that default rather than throwing.
		StubAdapter adapter = new(
			models: [DiscoveredWithoutContext("venice-uncensored")],
			providerType: "openai");

		IOptions<ProxyOptions> options = OptionsFor(OperatingMode.Hybrid, ("venice", "openai"));
		options.Value.Backends["venice"].ContextLength = 16384;
		options.Value.Backends["venice"].Models.Add(new ModelRegistrationOptions { Name = "venice-uncensored" });

		var sut = new ModelCatalogBuilder(options, new StubResolver(adapter), new StubCatalog(), new CapturingLogger());

		// Act
		IReadOnlyList<RegisteredModel> catalog = await sut.BuildAsync(CancellationToken.None);

		// Assert: with no override and no reported window, the pin resolves to the 16k backend default.
		RegisteredModel model = Assert.Single(catalog);
		Assert.Equal("venice-uncensored", model.Name);
		Assert.Equal("venice-uncensored", model.UpstreamModel);
		Assert.Equal(16384, model.ContextLength);
	}

	/// <summary>
	/// Verifies that a discovered model supporting neither completion nor embeddings (e.g. an
	/// image-generation model) is not exposed, since it has no usable Ollama endpoint, and that the skip
	/// is logged at information level as an expected outcome rather than a fault.
	/// </summary>
	[Fact]
	public async Task BuildAsync_WhenDiscoveredModelHasNoUsableCapability_SkipsModelAndLogsInformation()
	{
		// Arrange: the backend reports an image-generation model whose capabilities are neither completion
		// nor embeddings — the very shape that has no route on the Ollama-native surface.
		ModelCapabilities generationOnly = new(
			SupportsCompletion: false,
			SupportsTools: false,
			SupportsVision: false,
			SupportsEmbeddings: false,
			CapabilitySource.ProviderMetadata);
		StubAdapter adapter = new(
			models: [Discovered("nano-banana-2", contextLength: 4096)],
			providerType: "openai",
			capabilities: _ => generationOnly);

		IOptions<ProxyOptions> options = OptionsFor(OperatingMode.PlugAndPlay, ("openrouter", "openai"));

		var log = new CapturingLogger();
		var sut = new ModelCatalogBuilder(options, new StubResolver(adapter), new StubCatalog(), log);

		// Act
		IReadOnlyList<RegisteredModel> catalog = await sut.BuildAsync(CancellationToken.None);

		// Assert: the model is not exposed, and the skip is information (expected), not a warning.
		Assert.Empty(catalog);
		Assert.Contains(
			log.Entries,
			entry =>
				entry.Level == LogLevel.Information &&
				entry.Message.Contains("nano-banana-2", StringComparison.Ordinal) &&
				entry.Message.Contains("neither completion nor embeddings", StringComparison.Ordinal));
		Assert.DoesNotContain(
			log.Entries,
			entry =>
				entry.Level == LogLevel.Warning &&
				entry.Message.Contains("nano-banana-2", StringComparison.Ordinal));
	}

	/// <summary>
	/// Verifies that a hybrid model producing both image and text (so it supports completion) is exposed
	/// normally, proving the no-usable-capability filter targets only generation-only models and never
	/// hides a model that can chat.
	/// </summary>
	[Fact]
	public async Task BuildAsync_WhenDiscoveredModelSupportsCompletion_ExposesModel()
	{
		// Arrange: a hybrid model that supports completion (and vision) — it must survive the filter.
		ModelCapabilities hybrid = new(
			SupportsCompletion: true,
			SupportsTools: false,
			SupportsVision: true,
			SupportsEmbeddings: false,
			CapabilitySource.ProviderMetadata);
		StubAdapter adapter = new(
			models: [Discovered("gpt-4o", contextLength: 128000)],
			providerType: "openai",
			capabilities: _ => hybrid);

		IOptions<ProxyOptions> options = OptionsFor(OperatingMode.PlugAndPlay, ("openrouter", "openai"));

		var sut = new ModelCatalogBuilder(options, new StubResolver(adapter), new StubCatalog(), new CapturingLogger());

		// Act
		IReadOnlyList<RegisteredModel> catalog = await sut.BuildAsync(CancellationToken.None);

		// Assert: the completion-capable model is exposed with its capabilities intact.
		RegisteredModel model = Assert.Single(catalog);
		Assert.Equal("gpt-4o", model.Name);
		Assert.True(model.Capabilities.SupportsCompletion);
		Assert.True(model.Capabilities.SupportsVision);
	}

	/// <summary>
	/// Verifies that a backend's models are probed concurrently rather than one after another, so a
	/// backend reporting many models (e.g. a provider with 60+) overlaps their capability probes instead
	/// of summing the latencies.
	/// </summary>
	[Fact]
	public async Task BuildAsync_WhenBackendReportsManyModels_ProbesModelsConcurrently()
	{
		// Arrange: three models whose capability resolution blocks on a shared barrier. If the builder
		// probed serially, the barrier (count 3) could never trip — so completing at all proves the probes
		// overlapped. MaxConcurrentProbes defaults to 1 (a deliberately serial scan that is the safe choice
		// against rate-limited backends), so this test raises it to 3 to admit all three probes at once. The
		// barrier wait is bounded so a future regression fails fast with a clear message instead of hanging.
		using Barrier barrier = new(3);
		StubAdapter adapter = new(
			models:
			[
				Discovered("model-a", contextLength: 4096),
				Discovered("model-b", contextLength: 4096),
				Discovered("model-c", contextLength: 4096)
			],
			providerType: "openai",
			capabilities: _ =>
			{
				// In the concurrent case all three probes meet here and the barrier trips at once; if probing
				// ever regresses to serial, this bounded wait times out and the test fails fast rather than
				// deadlocking the run.
				Assert.True(
					// ReSharper disable once AccessToDisposedClosure
					barrier.SignalAndWait(TimeSpan.FromSeconds(30)),
					"Capability probes did not run concurrently: the barrier never tripped within the timeout.");
				return ModelCapabilities.CompletionOnly;
			});

		IOptions<ProxyOptions> options = OptionsFor(OperatingMode.PlugAndPlay, ("venice", "openai"));

		// Admit all three probes at once. The default is a fully serial scan (MaxConcurrentProbes = 1), which
		// would deadlock the Barrier(3) above; the barrier itself proves the concurrency actually happened.
		options.Value.Backends["venice"].Probing.MaxConcurrentProbes = 3;

		var sut = new ModelCatalogBuilder(options, new StubResolver(adapter), new StubCatalog(), new CapturingLogger());

		// Act
		IReadOnlyList<RegisteredModel> catalog = await sut.BuildAsync(CancellationToken.None);

		// Assert: every model survived, proving the concurrent probes all completed and merged.
		Assert.Equal(3, catalog.Count);
		Assert.Contains(catalog, model => model.Name == "model-a");
		Assert.Contains(catalog, model => model.Name == "model-b");
		Assert.Contains(catalog, model => model.Name == "model-c");
	}

	/// <summary>
	/// Verifies that a per-backend mode lets a deployment mix discovery and registry-only backends: a
	/// <see cref="OperatingMode.PlugAndPlay"/> backend contributes its discovered models, while an
	/// <see cref="OperatingMode.Explicit"/> backend contributes only its pinned registry entries and runs no
	/// discovery, so the same model list reported by the stub adapter never leaks from the Explicit backend.
	/// </summary>
	[Fact]
	public async Task BuildAsync_WhenBackendIsExplicit_ReachableOnlyThroughRegistry()
	{
		// Arrange: two backends. "local" is PlugAndPlay and auto-exposes its one discovered model; "cloud" is
		// Explicit, so it runs no discovery and is reachable only through the "cloud-only" pin in its nested
		// registry. The shared stub adapter reports the same model list for every backend, so a stray
		// discovery from "cloud" would surface "shared" against it — its absence proves Explicit skipped discovery.
		StubAdapter adapter = new(
			models: [Discovered("shared", contextLength: 4096)],
			providerType: "openai");

		IOptions<ProxyOptions> options = OptionsFor(
			OperatingMode.PlugAndPlay,
			("local", "openai"),
			("cloud", "openai"));
		options.Value.Backends["cloud"].Mode = OperatingMode.Explicit;
		options.Value.Backends["cloud"]
			.Models.Add(
				new ModelRegistrationOptions
				{
					Name = "cloud-only",
					ContextLength = 2048
				});

		var log = new CapturingLogger();
		var sut = new ModelCatalogBuilder(options, new StubResolver(adapter), new StubCatalog(), log);

		// Act
		IReadOnlyList<RegisteredModel> catalog = await sut.BuildAsync(CancellationToken.None);

		// Assert: "cloud-only" survives (registry path), "shared" survives (discovered on "local"), and
		// "cloud" was never discovered — the absence of a "shared" entry pinned to "cloud" is the observable
		// signal that Explicit mode actually skipped discovery for that backend.
		Assert.Equal(2, catalog.Count);

		RegisteredModel cloudOnly = Assert.Single(catalog, model => model.Name == "cloud-only");
		Assert.Equal("cloud", cloudOnly.BackendName);

		RegisteredModel shared = Assert.Single(catalog, model => model.Name == "shared");
		Assert.Equal("local", shared.BackendName);
		Assert.DoesNotContain(catalog, model => model.BackendName == "cloud" && model.Name != "cloud-only");
		Assert.DoesNotContain(
			log.Entries,
			entry =>
				entry.Level == LogLevel.Warning &&
				entry.Message.Contains("cloud", StringComparison.Ordinal));
	}

	#region Constructor

	/// <summary>
	/// Verifies that the <see cref="ModelCatalogBuilder"/> constructor rejects a
	/// <see langword="null"/> options argument.
	/// </summary>
	[Fact]
	public void Constructor_WhenOptionsIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() =>
			new ModelCatalogBuilder(
				null!,
				new StubResolver(new StubAdapter([], "openai")),
				new StubCatalog(),
				new CapturingLogger()));
		Assert.Equal("options", exception.ParamName);
	}

	/// <summary>
	/// Verifies that the <see cref="ModelCatalogBuilder"/> constructor rejects a
	/// <see langword="null"/> provider resolver argument.
	/// </summary>
	[Fact]
	public void Constructor_WhenProviderResolverIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() =>
			new ModelCatalogBuilder(
				OptionsFor(OperatingMode.PlugAndPlay, ("cloud", "openai")),
				null!,
				new StubCatalog(),
				new CapturingLogger()));
		Assert.Equal("providerResolver", exception.ParamName);
	}

	/// <summary>
	/// Verifies that the <see cref="ModelCatalogBuilder"/> constructor rejects a
	/// <see langword="null"/> provider catalog argument.
	/// </summary>
	[Fact]
	public void Constructor_WhenProviderCatalogIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() =>
			new ModelCatalogBuilder(
				OptionsFor(OperatingMode.PlugAndPlay, ("cloud", "openai")),
				new StubResolver(new StubAdapter([], "openai")),
				null!,
				new CapturingLogger()));
		Assert.Equal("providerCatalog", exception.ParamName);
	}

	/// <summary>
	/// Verifies that the <see cref="ModelCatalogBuilder"/> constructor rejects a
	/// <see langword="null"/> logger argument.
	/// </summary>
	[Fact]
	public void Constructor_WhenLoggerIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() =>
			new ModelCatalogBuilder(
				OptionsFor(OperatingMode.PlugAndPlay, ("cloud", "openai")),
				new StubResolver(new StubAdapter([], "openai")),
				new StubCatalog(),
				null!));
		Assert.Equal("logger", exception.ParamName);
	}

	#endregion

	#region Explicit backend metadata enrichment

	/// <summary>
	/// Verifies that an Explicit backend enriches its pinned models with provider metadata
	/// (CreatedAtUtc and Metadata) from the backend's listing, even though unpinned models
	/// are not auto-exposed.
	/// </summary>
	[Fact]
	public async Task BuildAsync_WhenExplicitBackendPinsModel_EnrichesWithProviderMetadata()
	{
		// Arrange: Explicit backend pins "pinned-model" and the adapter reports it with metadata.
		var createdAt = new DateTimeOffset(2024, 3, 15, 10, 0, 0, TimeSpan.Zero);
		var metadata = new ProviderModelMetadata(
			DisplayName: "Pinned Model Display",
			Description: "A model with rich metadata");

		StubAdapter adapter = new(
			models:
			[
				new DiscoveredModel(
					"pinned-model",
					Created: createdAt,
					ContextLength: 8192,
					Metadata: metadata)
			],
			providerType: "venice");

		IOptions<ProxyOptions> options = OptionsFor(
			OperatingMode.Explicit,
			("venice", "venice"));
		options.Value.Backends["venice"]
			.Models.Add(
				new ModelRegistrationOptions
				{
					Name = "pinned-model",
					ContextLength = 8192
				});

		var log = new CapturingLogger();
		var sut = new ModelCatalogBuilder(options, new StubResolver(adapter), new StubCatalog(), log);

		// Act
		IReadOnlyList<RegisteredModel> catalog = await sut.BuildAsync(CancellationToken.None);

		// Assert: the pinned model is enriched with CreatedAtUtc and Metadata.
		RegisteredModel model = Assert.Single(catalog);
		Assert.Equal("pinned-model", model.Name);
		Assert.Equal(createdAt, model.CreatedAtUtc);
		Assert.NotNull(model.Metadata);
		Assert.Equal("Pinned Model Display", model.Metadata.DisplayName);
		Assert.Equal("A model with rich metadata", model.Metadata.Description);
	}

	/// <summary>
	/// Verifies that an Explicit backend's pinned model remains unchanged when the backend's
	/// listing does not include that model (no metadata enrichment occurs).
	/// </summary>
	[Fact]
	public async Task BuildAsync_WhenExplicitBackendPinsModelButListingLacksIt_PinRemainsUnchanged()
	{
		// Arrange: Explicit backend pins "pinned-model" but the adapter only reports "other-model".
		StubAdapter adapter = new(
			models: [new DiscoveredModel("other-model", ContextLength: 4096)],
			providerType: "openai");

		IOptions<ProxyOptions> options = OptionsFor(
			OperatingMode.Explicit,
			("cloud", "openai"));
		options.Value.Backends["cloud"]
			.Models.Add(
				new ModelRegistrationOptions
				{
					Name = "pinned-model",
					ContextLength = 2048
				});

		var log = new CapturingLogger();
		var sut = new ModelCatalogBuilder(options, new StubResolver(adapter), new StubCatalog(), log);

		// Act
		IReadOnlyList<RegisteredModel> catalog = await sut.BuildAsync(CancellationToken.None);

		// Assert: the pinned model exists but has no enrichment data.
		RegisteredModel model = Assert.Single(catalog);
		Assert.Equal("pinned-model", model.Name);
		Assert.Null(model.CreatedAtUtc);
		Assert.Null(model.Metadata);
	}

	/// <summary>
	/// Verifies that an Explicit backend does not auto-expose unpinned models, even when
	/// the backend's listing includes them (metadata-only discovery).
	/// </summary>
	[Fact]
	public async Task BuildAsync_WhenExplicitBackendReportsUnpinnedModels_DoesNotAutoExposeThem()
	{
		// Arrange: Explicit backend pins only "pinned-model" but the adapter reports both
		// "pinned-model" and "unpinned-model".
		StubAdapter adapter = new(
			models:
			[
				new DiscoveredModel("pinned-model", ContextLength: 8192),
				new DiscoveredModel("unpinned-model", ContextLength: 4096)
			],
			providerType: "venice");

		IOptions<ProxyOptions> options = OptionsFor(
			OperatingMode.Explicit,
			("venice", "venice"));
		options.Value.Backends["venice"]
			.Models.Add(
				new ModelRegistrationOptions
				{
					Name = "pinned-model",
					ContextLength = 8192
				});

		var log = new CapturingLogger();
		var sut = new ModelCatalogBuilder(options, new StubResolver(adapter), new StubCatalog(), log);

		// Act
		IReadOnlyList<RegisteredModel> catalog = await sut.BuildAsync(CancellationToken.None);

		// Assert: only the pinned model is exposed; the unpinned one is not auto-exposed.
		RegisteredModel model = Assert.Single(catalog);
		Assert.Equal("pinned-model", model.Name);
	}

	#endregion

	#region Test infrastructure

	private static IOptions<ProxyOptions> OptionsFor(
		OperatingMode                               mode,
		params (string Name, string ProviderType)[] backends)
	{
		// Each backend now carries its own mode. Tests share a single mode across the backends they configure
		// here and override an individual backend inline (Backends[name].Mode = ...) for the mixed-mode
		// scenarios that pair a discovery backend with a registry-only one.
		ProxyOptions options = new();
		foreach ((string name, string providerType) in backends)
		{
			options.Backends[name] = new BackendOptions
			{
				BaseUrl = "https://x/v1",
				ProviderType = providerType,
				ApiKey = "placeholder-key",
				Mode = mode
			};
		}

		return Options.Create(options);
	}

	private static DiscoveredModel Discovered(string id, long contextLength) => new(id, ContextLength: contextLength);

	private static DiscoveredModel DiscoveredWithoutContext(string id) => new(id, ContextLength: null);

	/// <summary>A resolver that returns the same stub adapter for every backend name.</summary>
	private sealed class StubResolver(IProviderAdapter adapter) : IProviderResolver
	{
		public ResolvedBackend Resolve(string backendName) => new(adapter, new BackendContext(backendName));

		public ResolvedBackend ResolveDraft(BackendOptions draft) => new(adapter, new BackendContext("(draft)", draft));
	}

	/// <summary>
	/// A provider catalog that resolves a backend's mode from its explicit <see cref="BackendOptions.Mode"/>,
	/// falling back to <see cref="OperatingMode.Explicit"/> when unset. Every test here sets the mode explicitly
	/// through <see cref="OptionsFor"/>, so this fixed mapping mirrors the real catalog's resolution without
	/// needing the provider descriptors; the other members are unused by the builder.
	/// </summary>
	private sealed class StubCatalog : IProviderCatalog
	{
		public IReadOnlyList<ProviderDescriptor> Providers => [];

		public bool IsSupported(string? providerType) => true;

		public OperatingMode DefaultModeFor(string? providerType) => OperatingMode.Explicit;

		public string DefaultBaseUrlFor(string? providerType) => string.Empty;

		public string DisplayNameFor(string? providerType) => providerType ?? string.Empty;

		public OperatingMode ResolveMode(BackendOptions backend)
		{
			ArgumentNullException.ThrowIfNull(backend);

			return backend.Mode ?? OperatingMode.Explicit;
		}
	}

	/// <summary>
	/// A provider adapter that reports a fixed set of discovered models and completion-only
	/// capabilities; only discovery and capability determination are exercised here.
	/// </summary>
	private sealed class StubAdapter(
		IReadOnlyList<DiscoveredModel>            models,
		string                                    providerType,
		Func<DiscoveredModel, ModelCapabilities>? capabilities = null) : IProviderAdapter
	{
		public string ProviderType { get; } = providerType;

		public Task<IReadOnlyList<DiscoveredModel>> DiscoverModelsAsync(
			BackendContext    backend,
			CancellationToken cancellationToken) => Task.FromResult(models);

		public Task<ModelCapabilities> DetermineCapabilitiesAsync(
			BackendContext    backend,
			DiscoveredModel   model,
			CancellationToken cancellationToken) =>
			// Yield first so concurrent callers (the builder probes models in parallel) actually overlap
			// on the thread pool instead of completing synchronously inline; real adapters are async too.
			Task.Run(() => (capabilities ?? (_ => ModelCapabilities.CompletionOnly))(model), cancellationToken);

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

	/// <summary>An <see cref="ILogger{T}"/> that records each entry's level and rendered message.</summary>
	private sealed class CapturingLogger : ILogger<ModelCatalogBuilder>
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
