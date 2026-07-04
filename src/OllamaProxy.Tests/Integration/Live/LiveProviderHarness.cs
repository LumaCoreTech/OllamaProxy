// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Microsoft.Extensions.Logging.Abstractions;

using OllamaProxy.Diagnostics;
using OllamaProxy.Providers.OpenAiProtocol;

namespace OllamaProxy.Tests.Integration.Live;

/// <summary>
/// Bundles the collaborators one live test needs: the <see cref="RecordingHttpClientProvider"/> (so a test can
/// read the exact body the provider posted), the real <see cref="IReasoningDetailsCache"/> (shared with the
/// provider so a captured blob survives between the round-trip's two turns), the provider adapter under test,
/// and a per-call timeout token. A single instance is built per test through
/// <see cref="LiveProviderHarness.Create{TProvider}"/>, then disposed to tear down the recording transport.
/// <para>
/// The timeout is deliberately generous (default two minutes): a live reasoning model can take far longer than
/// a hermetic unit test, and the harness bounds each call through this token rather than the client's infinite
/// timeout — exercising the same cancellation path production relies on for long streaming responses.
/// </para>
/// </summary>
/// <typeparam name="TProvider">The concrete provider adapter type under test.</typeparam>
sealed class LiveProviderHarness<TProvider> : IDisposable
	where TProvider : OpenAiCompatibleProvider
{
	private readonly RecordingHttpClientProvider mRecorder;
	private readonly CancellationTokenSource     mTimeoutSource;

	internal LiveProviderHarness(
		RecordingHttpClientProvider recorder,
		IReasoningDetailsCache      cache,
		TProvider                   provider,
		LiveBackendConfig           config,
		TimeSpan                    timeout)
	{
		mRecorder = recorder;
		Cache = cache;
		Provider = provider;
		Config = config;
		mTimeoutSource = new CancellationTokenSource(timeout);
	}

	/// <summary>Gets the recording client provider that captured the most recent outgoing request.</summary>
	public RecordingHttpClientProvider Recorder => mRecorder;

	/// <summary>Gets the reasoning-details cache shared with the provider under test.</summary>
	public IReasoningDetailsCache Cache { get; }

	/// <summary>Gets the provider adapter under test.</summary>
	public TProvider Provider { get; }

	/// <summary>Gets the resolved live backend configuration.</summary>
	public LiveBackendConfig Config { get; }

	/// <summary>Gets the per-call timeout token bounding each live request.</summary>
	public CancellationToken Token => mTimeoutSource.Token;

	/// <inheritdoc/>
	public void Dispose()
	{
		mTimeoutSource.Dispose();
		mRecorder.Dispose();
	}
}

/// <summary>
/// Factory for <see cref="LiveProviderHarness{TProvider}"/>. Constructs the recorder, a real reasoning-details
/// cache, and the provider adapter from a single <see cref="LiveBackendConfig"/>, wiring them so the provider
/// posts through the recorder and shares the cache — the arrangement the conformance helpers expect.
/// </summary>
static class LiveProviderHarness
{
	/// <summary>The default per-call timeout, generous enough for slow live reasoning models.</summary>
	public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

	/// <summary>
	/// Builds a harness for the provider produced by <paramref name="providerFactory"/>, supplying it the
	/// recorder, a fresh real cache, the system clock, the config's options, a no-op trace accessor, the
	/// shared no-op capability prober, and a null logger.
	/// </summary>
	/// <typeparam name="TProvider">The concrete provider adapter type under test.</typeparam>
	/// <param name="config">The resolved live backend configuration.</param>
	/// <param name="providerFactory">
	/// Builds the concrete provider from its collaborators. Each backend test supplies this so the harness
	/// stays provider-agnostic while the concrete logger type is satisfied at the call site.
	/// </param>
	/// <returns>A ready-to-use harness; the caller owns its lifetime and must dispose it.</returns>
	/// <exception cref="ArgumentNullException">
	/// <paramref name="config"/> or <paramref name="providerFactory"/> is <see langword="null"/>.
	/// </exception>
	public static LiveProviderHarness<TProvider> Create<TProvider>(
		LiveBackendConfig                         config,
		Func<LiveProviderDependencies, TProvider> providerFactory)
		where TProvider : OpenAiCompatibleProvider
	{
		ArgumentNullException.ThrowIfNull(config);
		ArgumentNullException.ThrowIfNull(providerFactory);

		RecordingHttpClientProvider recorder = new(config.Options);
		IReasoningDetailsCache cache = TestReasoningDetailsCache.CreateDefault();

		LiveProviderDependencies dependencies = new(
			recorder,
			new LiveStubCapabilityProber(),
			TimeProvider.System,
			config.ToProxyOptions(),
			new RequestTraceAccessor(),
			cache);

		TProvider provider = providerFactory(dependencies);

		return new LiveProviderHarness<TProvider>(recorder, cache, provider, config, DefaultTimeout);
	}
}

/// <summary>
/// The collaborators a concrete provider adapter is constructed from, bundled so a backend test's provider
/// factory can forward them positionally without re-listing each one. The only argument it must add itself is
/// the concrete <c>ILogger&lt;TProvider&gt;</c>, which a test supplies as <see cref="NullLogger{T}.Instance"/>.
/// </summary>
/// <param name="HttpClientProvider">The recording client provider the adapter posts through.</param>
/// <param name="CapabilityProber">The no-op capability prober.</param>
/// <param name="TimeProvider">The system clock.</param>
/// <param name="Options">The proxy options carrying the single live backend.</param>
/// <param name="TraceAccessor">The request-trace accessor.</param>
/// <param name="ReasoningDetailsCache">The cache shared with the harness for the round-trip.</param>
sealed record LiveProviderDependencies(
	RecordingHttpClientProvider                                                   HttpClientProvider,
	ICapabilityProber                                                             CapabilityProber,
	TimeProvider                                                                  TimeProvider,
	Microsoft.Extensions.Options.IOptions<OllamaProxy.Configuration.ProxyOptions> Options,
	IRequestTraceAccessor                                                         TraceAccessor,
	IReasoningDetailsCache                                                        ReasoningDetailsCache);
