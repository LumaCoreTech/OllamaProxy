// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Configuration;
using OllamaProxy.Contracts.Ollama;
using OllamaProxy.Core;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Tests.Core;

// Backend model discovery: provider snapshots become proxy-ready candidates.
//
// These tests cover the orchestration extracted from ModelCatalogBuilder: raw provider models are transformed into
// resolved candidates, capability probes are gated by policy and concurrency, and invalid inputs fail before any
// provider call is attempted.
//
//   1. Candidate resolution: discovered order is preserved, backend prefixes are applied, the backend's raw
//      reported context window is carried through unchanged (the configured default never narrows it), and
//      resolved capability provenance is preserved.
//
//   2. Probe policy: a model with no resolvable effective window is skipped for the startup catalog policy, while a
//      context-less model rescued by the backend default is still probed; the admin fetch policy probes every
//      model so the UI can display every recoverable fact.
//
//   3. Concurrency: MaxConcurrentProbes limits in-flight capability resolutions within one backend.
//
//   4. Invalid arguments: null resolved backends and backend options are rejected.
[Trait("Category", "Unit")]
public sealed class BackendModelDiscoveryTests
{
	// --- 1. Candidate resolution ---

	/// <summary>
	/// Verifies that <see cref="BackendModelDiscovery.DiscoverAsync"/> preserves the backend's reported model order
	/// after resolving each model into a <see cref="DiscoveryCandidate"/>.
	/// </summary>
	[Fact]
	public async Task DiscoverAsync_WhenAdapterReturnsModels_PreservesReportedOrder()
	{
		// Arrange
		var sut = new BackendModelDiscovery();
		ResolvedBackend resolved = Resolved(
			new StubAdapter(
			[
				Discovered("alpha", contextLength: 4096),
				Discovered("beta", contextLength: 8192),
				Discovered("gamma", contextLength: 16384)
			]));

		// Act
		IReadOnlyList<DiscoveryCandidate> candidates = await sut.DiscoverAsync(
			                                               resolved,
			                                               Backend(),
			                                               DiscoveryProbePolicy.SkipContextless,
			                                               CancellationToken.None);

		// Assert
		Assert.Equal(3, candidates.Count);
		AssertCandidate(candidates[0], "alpha", "alpha", 4096, ModelCapabilities.CompletionOnly);
		AssertCandidate(candidates[1], "beta", "beta", 8192, ModelCapabilities.CompletionOnly);
		AssertCandidate(candidates[2], "gamma", "gamma", 16384, ModelCapabilities.CompletionOnly);
	}

	/// <summary>
	/// Verifies that <see cref="BackendModelDiscovery.DiscoverAsync"/> applies the backend's client-facing model
	/// prefix while preserving the bare upstream identifier used for provider requests.
	/// </summary>
	[Fact]
	public async Task DiscoverAsync_WhenBackendHasPrefix_AppliesClientFacingPrefix()
	{
		// Arrange
		var sut = new BackendModelDiscovery();
		ResolvedBackend resolved = Resolved(new StubAdapter([Discovered("gemma2", contextLength: 8192)]));

		// Act
		IReadOnlyList<DiscoveryCandidate> candidates = await sut.DiscoverAsync(
			                                               resolved,
			                                               Backend(prefix: "vllm"),
			                                               DiscoveryProbePolicy.SkipContextless,
			                                               CancellationToken.None);

		// Assert
		DiscoveryCandidate candidate = Assert.Single(candidates);
		AssertCandidate(candidate, "vllm/gemma2", "gemma2", 8192, ModelCapabilities.CompletionOnly);
	}

	/// <summary>
	/// Verifies that <see cref="BackendModelDiscovery.DiscoverAsync"/> carries the backend's raw reported context
	/// window through unchanged even when the configured default is narrower, so the candidate never understates
	/// what the backend advertises — the configured default is a fallback for the consumer, not a narrowing clamp.
	/// </summary>
	[Fact]
	public async Task DiscoverAsync_WhenBackendDefaultNarrowerThanReported_CarriesRawReportedWindow()
	{
		// Arrange: the provider reports an 8192 window while the backend configures a narrower 2048 default.
		var sut = new BackendModelDiscovery();
		ResolvedBackend resolved = Resolved(new StubAdapter([Discovered("model", contextLength: 8192)]));

		// Act
		IReadOnlyList<DiscoveryCandidate> candidates = await sut.DiscoverAsync(
			                                               resolved,
			                                               Backend(contextLength: 2048),
			                                               DiscoveryProbePolicy.SkipContextless,
			                                               CancellationToken.None);

		// Assert: the candidate carries the raw reported 8192, not the narrower configured default.
		DiscoveryCandidate candidate = Assert.Single(candidates);
		AssertCandidate(candidate, "model", "model", 8192, ModelCapabilities.CompletionOnly);
	}

	/// <summary>
	/// Verifies that <see cref="BackendModelDiscovery.DiscoverAsync"/> preserves the capability result returned by the
	/// provider adapter, including its provenance.
	/// </summary>
	[Fact]
	public async Task DiscoverAsync_WhenAdapterResolvesCapabilities_PreservesProvenance()
	{
		// Arrange
		ModelCapabilities listed = new(
			SupportsCompletion: true,
			SupportsTools: true,
			SupportsVision: true,
			SupportsEmbeddings: false,
			CapabilitySource.ProviderMetadata);
		var sut = new BackendModelDiscovery();
		ResolvedBackend resolved = Resolved(new StubAdapter([Discovered("rich", contextLength: 32768)], _ => listed));

		// Act
		IReadOnlyList<DiscoveryCandidate> candidates = await sut.DiscoverAsync(
			                                               resolved,
			                                               Backend(),
			                                               DiscoveryProbePolicy.SkipContextless,
			                                               CancellationToken.None);

		// Assert
		DiscoveryCandidate candidate = Assert.Single(candidates);
		AssertCandidate(candidate, "rich", "rich", 32768, listed);
	}

	// --- 2. Probe policy ---

	/// <summary>
	/// Verifies that <see cref="BackendModelDiscovery.DiscoverAsync"/> leaves a context-less model unprobed under the
	/// startup catalog policy, because that consumer drops the model during the merge anyway.
	/// </summary>
	[Fact]
	public async Task DiscoverAsync_WhenProbePolicySkipsContextlessModel_DoesNotProbeAndLeavesCapabilitiesNull()
	{
		// Arrange
		int probeCount = 0;
		var sut = new BackendModelDiscovery();
		ResolvedBackend resolved = Resolved(
			new StubAdapter(
				[Discovered("mystery", contextLength: null)],
				_ =>
				{
					probeCount++;
					return ModelCapabilities.CompletionOnly;
				}));

		// Act
		IReadOnlyList<DiscoveryCandidate> candidates = await sut.DiscoverAsync(
			                                               resolved,
			                                               Backend(),
			                                               DiscoveryProbePolicy.SkipContextless,
			                                               CancellationToken.None);

		// Assert
		DiscoveryCandidate candidate = Assert.Single(candidates);
		AssertCandidate(candidate, "mystery", "mystery", reportedContextLength: null, capabilities: null);
		Assert.Equal(0, probeCount);
	}

	/// <summary>
	/// Verifies that under the startup catalog policy a context-less model whose effective window is rescued by the
	/// backend default is still probed — the probe gate keys on the effective window, not the raw one — while the
	/// candidate carries a <see langword="null"/> reported window, leaving the fallback to the consumer.
	/// </summary>
	[Fact]
	public async Task DiscoverAsync_WhenBackendDefaultRescuesContextlessModel_ProbesButReportsNullWindow()
	{
		// Arrange: the provider reports no window, but the backend default makes the effective window resolvable, so
		// SkipContextless must not drop the probe (a model exposable via the default would otherwise lack
		// capabilities). The candidate still carries the raw reported null — applying the default is the consumer's job.
		int probeCount = 0;
		var sut = new BackendModelDiscovery();
		ResolvedBackend resolved = Resolved(
			new StubAdapter(
				[Discovered("rescued", contextLength: null)],
				_ =>
				{
					probeCount++;
					return ModelCapabilities.CompletionOnly;
				}));

		// Act
		IReadOnlyList<DiscoveryCandidate> candidates = await sut.DiscoverAsync(
			                                               resolved,
			                                               Backend(contextLength: 4096),
			                                               DiscoveryProbePolicy.SkipContextless,
			                                               CancellationToken.None);

		// Assert: probed exactly once, capabilities resolved, but the reported window stays null.
		DiscoveryCandidate candidate = Assert.Single(candidates);
		AssertCandidate(candidate, "rescued", "rescued", reportedContextLength: null, ModelCapabilities.CompletionOnly);
		Assert.Equal(1, probeCount);
	}

	/// <summary>
	/// Verifies that <see cref="BackendModelDiscovery.DiscoverAsync"/> still probes a context-less model under the
	/// admin fetch policy, so the UI can present its true capabilities even before the operator supplies a context
	/// length.
	/// </summary>
	[Fact]
	public async Task DiscoverAsync_WhenProbePolicyProbesAll_ProbesContextlessModel()
	{
		// Arrange
		int probeCount = 0;
		ModelCapabilities probed = new(
			SupportsCompletion: true,
			SupportsTools: false,
			SupportsVision: false,
			SupportsEmbeddings: true,
			CapabilitySource.Probed);
		var sut = new BackendModelDiscovery();
		ResolvedBackend resolved = Resolved(
			new StubAdapter(
				[Discovered("mystery", contextLength: null)],
				_ =>
				{
					probeCount++;
					return probed;
				}));

		// Act
		IReadOnlyList<DiscoveryCandidate> candidates = await sut.DiscoverAsync(
			                                               resolved,
			                                               Backend(),
			                                               DiscoveryProbePolicy.ProbeAll,
			                                               CancellationToken.None);

		// Assert
		DiscoveryCandidate candidate = Assert.Single(candidates);
		AssertCandidate(candidate, "mystery", "mystery", reportedContextLength: null, probed);
		Assert.Equal(1, probeCount);
	}

	// --- 3. Concurrency ---

	/// <summary>
	/// Verifies that <see cref="BackendModelDiscovery.DiscoverAsync"/> honors
	/// <see cref="CapabilityProbingOptions.MaxConcurrentProbes"/> within one backend.
	/// </summary>
	[Fact]
	public async Task DiscoverAsync_WhenMaxConcurrentProbesIsOne_LimitsProbeConcurrency()
	{
		// Arrange
		int currentProbeCount = 0;
		int maxProbeCount = 0;
		object sync = new();
		var sut = new BackendModelDiscovery();
		ResolvedBackend resolved = Resolved(
			new StubAdapter(
				[
					Discovered("alpha", contextLength: 4096),
					Discovered("beta", contextLength: 4096),
					Discovered("gamma", contextLength: 4096)
				],
				_ =>
				{
					int current = Interlocked.Increment(ref currentProbeCount);
					lock (sync)
					{
						maxProbeCount = Math.Max(maxProbeCount, current);
					}

					Thread.Sleep(20);
					Interlocked.Decrement(ref currentProbeCount);
					return ModelCapabilities.CompletionOnly;
				}));

		// Act
		IReadOnlyList<DiscoveryCandidate> candidates = await sut.DiscoverAsync(
			                                               resolved,
			                                               Backend(maxConcurrentProbes: 1),
			                                               DiscoveryProbePolicy.SkipContextless,
			                                               CancellationToken.None);

		// Assert
		Assert.Equal(3, candidates.Count);
		Assert.Equal(1, maxProbeCount);
	}

	// --- 4. Invalid arguments ---

	/// <summary>
	/// Verifies that <see cref="BackendModelDiscovery.DiscoverAsync"/> rejects a <see langword="null"/> resolved
	/// backend before contacting any provider adapter.
	/// </summary>
	[Fact]
	public async Task DiscoverAsync_WhenResolvedIsNull_ThrowsArgumentNullException()
	{
		// Arrange
		var sut = new BackendModelDiscovery();

		// Act + Assert
		var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
			                sut.DiscoverAsync(
				                null!,
				                Backend(),
				                DiscoveryProbePolicy.SkipContextless,
				                CancellationToken.None));
		Assert.Equal("resolved", exception.ParamName);
	}

	/// <summary>
	/// Verifies that <see cref="BackendModelDiscovery.DiscoverAsync"/> rejects a <see langword="null"/> backend
	/// options argument before contacting any provider adapter.
	/// </summary>
	[Fact]
	public async Task DiscoverAsync_WhenBackendIsNull_ThrowsArgumentNullException()
	{
		// Arrange
		var sut = new BackendModelDiscovery();
		ResolvedBackend resolved = Resolved(new StubAdapter([]));

		// Act + Assert
		var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
			                sut.DiscoverAsync(
				                resolved,
				                null!,
				                DiscoveryProbePolicy.SkipContextless,
				                CancellationToken.None));
		Assert.Equal("backend", exception.ParamName);
	}

	// --- 5. Streaming discovery ---

	/// <summary>
	/// Verifies that <see cref="BackendModelDiscovery.DiscoverStreamingAsync"/> yields a resolved candidate for
	/// every discovered model, carrying the same resolution (prefix, reported window, probed capabilities) the
	/// buffered path produces.
	/// </summary>
	[Fact]
	public async Task DiscoverStreamingAsync_WhenAdapterReturnsModels_YieldsAllResolvedCandidates()
	{
		// Arrange
		var sut = new BackendModelDiscovery();
		ResolvedBackend resolved = Resolved(
			new StubAdapter(
			[
				Discovered("alpha", contextLength: 4096),
				Discovered("beta", contextLength: 8192),
				Discovered("gamma", contextLength: 16384)
			]));

		// Act
		List<DiscoveryCandidate> candidates = [];
		await foreach (DiscoveryCandidate candidate in sut.DiscoverStreamingAsync(
			               resolved,
			               Backend(),
			               DiscoveryProbePolicy.ProbeAll,
			               CancellationToken.None))
		{
			candidates.Add(candidate);
		}

		// Assert: every model is present, resolved, and yielded in client-name order.
		Assert.Equal(3, candidates.Count);
		AssertCandidate(candidates[0], "alpha", "alpha", 4096, ModelCapabilities.CompletionOnly);
		AssertCandidate(candidates[1], "beta", "beta", 8192, ModelCapabilities.CompletionOnly);
		AssertCandidate(candidates[2], "gamma", "gamma", 16384, ModelCapabilities.CompletionOnly);
	}

	/// <summary>
	/// Verifies that <see cref="BackendModelDiscovery.DiscoverStreamingAsync"/> emits candidates in client-facing
	/// name order — the same order the admin table sorts on — regardless of the order the backend listed them, so
	/// the UI fills top-to-bottom rather than in listing or completion order.
	/// </summary>
	[Fact]
	public async Task DiscoverStreamingAsync_WhenModelsListedOutOfOrder_EmitsInClientNameOrder()
	{
		// Arrange: the backend lists the models deliberately unsorted. The stream must still emit them sorted by
		// client-facing name so the rows fill in a stable top-to-bottom order.
		var sut = new BackendModelDiscovery();
		ResolvedBackend resolved = Resolved(
			new StubAdapter(
			[
				Discovered("gamma", contextLength: 4096),
				Discovered("alpha", contextLength: 4096),
				Discovered("beta", contextLength: 4096)
			]));

		// Act
		List<string> emissionOrder = [];
		await foreach (DiscoveryCandidate candidate in sut.DiscoverStreamingAsync(
			               resolved,
			               Backend(),
			               DiscoveryProbePolicy.ProbeAll,
			               CancellationToken.None))
		{
			emissionOrder.Add(candidate.UpstreamModel);
		}

		// Assert: alphabetical by client name, independent of the backend's listing order.
		Assert.Equal(["alpha", "beta", "gamma"], emissionOrder);
	}

	/// <summary>
	/// Verifies that <see cref="BackendModelDiscovery.DiscoverStreamingAsync"/> starts every model's probe eagerly
	/// and only <em>awaits</em> them in order: a slow leading model (first alphabetically) does not serialize the
	/// ones beneath it — their probes run concurrently while it is still resolving — yet the emission order stays
	/// strictly client-name order. If probing were lazy/sequential, the leading model would deadlock waiting for
	/// later probes that never started.
	/// </summary>
	[Fact]
	public async Task DiscoverStreamingAsync_WhenLeadingModelIsSlow_ProbesLaterModelsConcurrentlyButEmitsInOrder()
	{
		// Arrange: "alpha" sorts first but its probe blocks until both later models have entered their own probe.
		// This only completes if the later probes run concurrently with alpha's, proving eager start; the emission
		// must still be client-name order, proving ordered await.
		using var laterProbesStarted = new CountdownEvent(2);
		var sut = new BackendModelDiscovery();
		ResolvedBackend resolved = Resolved(
			new StubAdapter(
				[
					Discovered("alpha", contextLength: 4096),
					Discovered("beta", contextLength: 4096),
					Discovered("gamma", contextLength: 4096)
				],
				model =>
				{
					if (model.Id == "alpha")
					{
						// Resolve only once the later probes are both in flight; a deadlock here would mean they
						// never started, i.e. resolution was sequential rather than eager.
						// ReSharper disable once AccessToDisposedClosure
						Assert.True(laterProbesStarted.Wait(TimeSpan.FromSeconds(5)));
						return ModelCapabilities.CompletionOnly;
					}

					// ReSharper disable once AccessToDisposedClosure
					laterProbesStarted.Signal();
					return ModelCapabilities.CompletionOnly;
				}));

		// Act: three permits so all three probes can be in flight at once.
		List<string> emissionOrder = [];
		await foreach (DiscoveryCandidate candidate in sut.DiscoverStreamingAsync(
			               resolved,
			               Backend(maxConcurrentProbes: 3),
			               DiscoveryProbePolicy.ProbeAll,
			               CancellationToken.None))
		{
			emissionOrder.Add(candidate.UpstreamModel);
		}

		// Assert: strict client-name order even though alpha resolved only after the later probes had started.
		Assert.Equal(["alpha", "beta", "gamma"], emissionOrder);
	}

	/// <summary>
	/// Verifies that <see cref="BackendModelDiscovery.DiscoverStreamingAsync"/> honors
	/// <see cref="CapabilityProbingOptions.MaxConcurrentProbes"/> within one backend, exactly as the buffered path
	/// does.
	/// </summary>
	[Fact]
	public async Task DiscoverStreamingAsync_WhenMaxConcurrentProbesIsOne_LimitsProbeConcurrency()
	{
		// Arrange
		int currentProbeCount = 0;
		int maxProbeCount = 0;
		object sync = new();
		var sut = new BackendModelDiscovery();
		ResolvedBackend resolved = Resolved(
			new StubAdapter(
				[
					Discovered("alpha", contextLength: 4096),
					Discovered("beta", contextLength: 4096),
					Discovered("gamma", contextLength: 4096)
				],
				_ =>
				{
					int current = Interlocked.Increment(ref currentProbeCount);
					lock (sync)
					{
						maxProbeCount = Math.Max(maxProbeCount, current);
					}

					Thread.Sleep(20);
					Interlocked.Decrement(ref currentProbeCount);
					return ModelCapabilities.CompletionOnly;
				}));

		// Act
		List<DiscoveryCandidate> candidates = [];
		await foreach (DiscoveryCandidate candidate in sut.DiscoverStreamingAsync(
			               resolved,
			               Backend(maxConcurrentProbes: 1),
			               DiscoveryProbePolicy.ProbeAll,
			               CancellationToken.None))
		{
			candidates.Add(candidate);
		}

		// Assert
		Assert.Equal(3, candidates.Count);
		Assert.Equal(1, maxProbeCount);
	}

	/// <summary>
	/// Verifies that <see cref="BackendModelDiscovery.DiscoverStreamingAsync"/> does not absorb a listing failure:
	/// when the provider's model listing throws, the enumeration surfaces the fault rather than ending silently.
	/// </summary>
	[Fact]
	public async Task DiscoverStreamingAsync_WhenListingFails_PropagatesFaultToEnumerator()
	{
		// Arrange
		var sut = new BackendModelDiscovery();
		ResolvedBackend resolved =
			Resolved(new ThrowingListingAdapter(new InvalidOperationException("listing failed")));

		// Act + Assert: the fault flows out of the await foreach, not swallowed into an empty stream.
		async Task Enumerate()
		{
			await foreach (DiscoveryCandidate _ in sut.DiscoverStreamingAsync(
				               resolved,
				               Backend(),
				               DiscoveryProbePolicy.ProbeAll,
				               CancellationToken.None)) { }
		}

		var exception = await Assert.ThrowsAsync<InvalidOperationException>(Enumerate);
		Assert.Equal("listing failed", exception.Message);
	}

	/// <summary>
	/// Verifies that <see cref="BackendModelDiscovery.DiscoverStreamingAsync"/> does not absorb a per-model probe
	/// failure: when a model's capability probe throws, the enumeration surfaces the fault.
	/// </summary>
	[Fact]
	public async Task DiscoverStreamingAsync_WhenProbeFails_PropagatesFaultToEnumerator()
	{
		// Arrange: the single model's probe throws.
		var sut = new BackendModelDiscovery();
		ResolvedBackend resolved = Resolved(
			new StubAdapter(
				[Discovered("boom", contextLength: 4096)],
				_ => throw new InvalidOperationException("probe failed")));

		// Act + Assert
		async Task Enumerate()
		{
			await foreach (DiscoveryCandidate _ in sut.DiscoverStreamingAsync(
				               resolved,
				               Backend(),
				               DiscoveryProbePolicy.ProbeAll,
				               CancellationToken.None)) { }
		}

		var exception = await Assert.ThrowsAsync<InvalidOperationException>(Enumerate);
		Assert.Equal("probe failed", exception.Message);
	}

	/// <summary>
	/// Verifies that <see cref="BackendModelDiscovery.DiscoverStreamingAsync"/> rejects a <see langword="null"/>
	/// resolved backend before contacting any provider adapter.
	/// </summary>
	[Fact]
	public async Task DiscoverStreamingAsync_WhenResolvedIsNull_ThrowsArgumentNullException()
	{
		// Arrange
		var sut = new BackendModelDiscovery();

		// Act + Assert: the guard runs before the first MoveNextAsync because the iterator validates eagerly.
		async Task Enumerate()
		{
			await foreach (DiscoveryCandidate _ in sut.DiscoverStreamingAsync(
				               null!,
				               Backend(),
				               DiscoveryProbePolicy.ProbeAll,
				               CancellationToken.None)) { }
		}

		var exception = await Assert.ThrowsAsync<ArgumentNullException>(Enumerate);
		Assert.Equal("resolved", exception.ParamName);
	}

	/// <summary>
	/// Verifies that <see cref="BackendModelDiscovery.DiscoverStreamingAsync"/> rejects a <see langword="null"/>
	/// backend options argument before contacting any provider adapter.
	/// </summary>
	[Fact]
	public async Task DiscoverStreamingAsync_WhenBackendIsNull_ThrowsArgumentNullException()
	{
		// Arrange
		var sut = new BackendModelDiscovery();
		ResolvedBackend resolved = Resolved(new StubAdapter([]));

		// Act + Assert
		async Task Enumerate()
		{
			await foreach (DiscoveryCandidate _ in sut.DiscoverStreamingAsync(
				               resolved,
				               null!,
				               DiscoveryProbePolicy.ProbeAll,
				               CancellationToken.None)) { }
		}

		var exception = await Assert.ThrowsAsync<ArgumentNullException>(Enumerate);
		Assert.Equal("backend", exception.ParamName);
	}

	#region Test infrastructure

	/// <summary>
	/// Builds backend options with optional discovery-related settings; URL and key are placeholders because the
	/// discovery unit tests use an in-memory adapter.
	/// </summary>
	/// <param name="prefix">The optional client-facing model prefix.</param>
	/// <param name="contextLength">The optional backend context-length default.</param>
	/// <param name="maxConcurrentProbes">The optional maximum number of concurrent model probes.</param>
	/// <returns>The configured backend options.</returns>
	private static BackendOptions Backend(
		string? prefix              = null,
		long?   contextLength       = null,
		int?    maxConcurrentProbes = null) => new()
	{
		BaseUrl = "https://x/v1",
		ProviderType = "openai",
		ApiKey = "placeholder-key",
		ModelPrefix = prefix,
		ContextLength = contextLength is { } value ? (int)value : null,
		Probing = new CapabilityProbingOptions { MaxConcurrentProbes = maxConcurrentProbes ?? 4 }
	};

	/// <summary>
	/// Builds a resolved backend around a test adapter and a stable backend context.
	/// </summary>
	/// <param name="adapter">The provider adapter to expose through the resolved backend.</param>
	/// <returns>The resolved backend passed to the discovery service.</returns>
	private static ResolvedBackend Resolved(IProviderAdapter adapter) => new(adapter, new BackendContext("cloud"));

	/// <summary>
	/// Builds a raw discovered model with an optional context length.
	/// </summary>
	/// <param name="id">The upstream model identifier.</param>
	/// <param name="contextLength">The optional context length reported by the backend.</param>
	/// <returns>The raw provider-discovered model.</returns>
	private static DiscoveredModel Discovered(string id, long? contextLength) => new(id, ContextLength: contextLength);

	/// <summary>
	/// Asserts the complete scalar state of a resolved discovery candidate.
	/// </summary>
	/// <param name="actual">The candidate to verify.</param>
	/// <param name="clientName">The expected client-facing model name.</param>
	/// <param name="upstreamModel">The expected upstream model identifier.</param>
	/// <param name="reportedContextLength">The expected raw reported context window.</param>
	/// <param name="capabilities">The expected resolved capabilities.</param>
	private static void AssertCandidate(
		DiscoveryCandidate actual,
		string             clientName,
		string             upstreamModel,
		long?              reportedContextLength,
		ModelCapabilities? capabilities)
	{
		Assert.Equal(clientName, actual.ClientName);
		Assert.Equal(upstreamModel, actual.UpstreamModel);
		Assert.Equal(reportedContextLength, actual.ReportedContextLength);
		Assert.Equal(capabilities, actual.Capabilities);
	}

	/// <summary>
	/// A provider adapter that reports a fixed model list and resolves capabilities through a supplied delegate.
	/// Only discovery and capability determination are exercised by these tests.
	/// </summary>
	/// <param name="models">The raw models returned from provider discovery.</param>
	/// <param name="capabilities">The optional capability resolver; defaults to completion-only.</param>
	private sealed class StubAdapter(
		IReadOnlyList<DiscoveredModel>            models,
		Func<DiscoveredModel, ModelCapabilities>? capabilities = null) : IProviderAdapter
	{
		/// <inheritdoc/>
		public string ProviderType => "openai";

		/// <inheritdoc/>
		public Task<IReadOnlyList<DiscoveredModel>> DiscoverModelsAsync(
			BackendContext    backend,
			CancellationToken cancellationToken) => Task.FromResult(models);

		/// <inheritdoc/>
		public Task<ModelCapabilities> DetermineCapabilitiesAsync(
			BackendContext    backend,
			DiscoveredModel   model,
			CancellationToken cancellationToken) => Task.Run(
			() => (capabilities ?? (_ => ModelCapabilities.CompletionOnly))(model),
			cancellationToken);

		/// <inheritdoc/>
		public IAsyncEnumerable<OllamaChatResponse> StreamChatAsync(
			BackendContext    backend,
			string            upstreamModel,
			OllamaChatRequest request,
			ReasoningEffort?  pinnedEffort,
			CancellationToken cancellationToken) => throw new NotSupportedException();

		/// <inheritdoc/>
		public Task<OllamaChatResponse> CompleteChatAsync(
			BackendContext    backend,
			string            upstreamModel,
			OllamaChatRequest request,
			ReasoningEffort?  pinnedEffort,
			CancellationToken cancellationToken) => throw new NotSupportedException();

		/// <inheritdoc/>
		public Task<OllamaEmbedResponse> CreateEmbeddingsAsync(
			BackendContext     backend,
			string             upstreamModel,
			OllamaEmbedRequest request,
			CancellationToken  cancellationToken) => throw new NotSupportedException();
	}

	/// <summary>
	/// A provider adapter whose model listing always faults, used to prove the streaming discovery does not absorb
	/// a listing failure. No other member is exercised.
	/// </summary>
	/// <param name="fault">The exception thrown from <see cref="DiscoverModelsAsync"/>.</param>
	private sealed class ThrowingListingAdapter(Exception fault) : IProviderAdapter
	{
		/// <inheritdoc/>
		public string ProviderType => "openai";

		/// <inheritdoc/>
		public Task<IReadOnlyList<DiscoveredModel>> DiscoverModelsAsync(
			BackendContext    backend,
			CancellationToken cancellationToken) => Task.FromException<IReadOnlyList<DiscoveredModel>>(fault);

		/// <inheritdoc/>
		public Task<ModelCapabilities> DetermineCapabilitiesAsync(
			BackendContext    backend,
			DiscoveredModel   model,
			CancellationToken cancellationToken) => throw new NotSupportedException();

		/// <inheritdoc/>
		public IAsyncEnumerable<OllamaChatResponse> StreamChatAsync(
			BackendContext    backend,
			string            upstreamModel,
			OllamaChatRequest request,
			ReasoningEffort?  pinnedEffort,
			CancellationToken cancellationToken) => throw new NotSupportedException();

		/// <inheritdoc/>
		public Task<OllamaChatResponse> CompleteChatAsync(
			BackendContext    backend,
			string            upstreamModel,
			OllamaChatRequest request,
			ReasoningEffort?  pinnedEffort,
			CancellationToken cancellationToken) => throw new NotSupportedException();

		/// <inheritdoc/>
		public Task<OllamaEmbedResponse> CreateEmbeddingsAsync(
			BackendContext     backend,
			string             upstreamModel,
			OllamaEmbedRequest request,
			CancellationToken  cancellationToken) => throw new NotSupportedException();
	}

	#endregion
}
