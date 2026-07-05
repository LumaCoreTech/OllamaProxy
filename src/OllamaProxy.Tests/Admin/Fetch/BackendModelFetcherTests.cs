// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Net;
using System.Runtime.CompilerServices;

using OllamaProxy.Admin.Fetch;
using OllamaProxy.Configuration;
using OllamaProxy.Contracts.Ollama;
using OllamaProxy.Core;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Tests.Admin.Fetch;

/// <summary>
/// Tests for <see cref="BackendModelFetcher"/>: one backend's models, or an honestly classified failure.
/// </summary>
/// <remarks>
/// <see cref="BackendModelFetcher"/> exposes two fetch shapes, and this file covers both (one #region each):
/// <list type="bullet">
///     <item>
///         <description>
///         <c>FetchAsync()</c> buffers the whole batch and returns the outcome as a BackendFetchResult. A failure
///         becomes a classified failure value, never an exception, apart from the two control-flow signals noted
///         below.
///         </description>
///     </item>
///     <item>
///         <description>
///         <c>FetchStreamingAsync()</c> yields each model as it resolves. Once it has begun yielding it cannot
///         fold a late failure back into a result, so a fault surfaces as a thrown BackendFetchException instead.
///         </description>
///     </item>
/// </list>
/// Both paths share one spine. They resolve through the draft path (the only path that works on the client-less
/// chassis), forward the caller's probe policy to discovery unchanged, and classify failures honestly: 401/403
/// is Authentication, any other upstream status is Upstream, and anything unattributable is Unknown. Both also
/// let the same two signals escape rather than swallow them: a cancellation through the caller's token, and an
/// argument-guard violation. For the scenario-by-scenario story of each method, see its #region below.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class BackendModelFetcherTests
{
	#region FetchAsync()

	// FetchAsync(): from the happy path through every way the buffered fetch can fail.
	//
	// These tests follow one buffered fetch from a successful discovery through every failure mode. They verify
	// that the fetcher resolves through the draft path, probes everything, and never lets a backend fault escape
	// as an exception. The only two conditions that do propagate are the control-flow signals in sections 4 and 5:
	//
	//   1. Happy path: a successful discovery becomes a Success result carrying the resolved models in order
	//      (WhenDiscoverySucceeds), always resolved through ResolveDraft (UsesDraftResolution) and forwarding the
	//      caller's probe policy to discovery unchanged (WhenPolicyIsNeverProbe, WhenPolicyIsProbeAll).
	//
	//   2. Attributable failures: an upstream error answer becomes a classified failure. 401/403 is
	//      Authentication (WhenProviderRejectsWithAuthStatus), every other status is Upstream
	//      (WhenProviderRejectsWithOtherStatus).
	//
	//   3. Unattributable failures: a transport fault or an unknown provider type cannot be pinned on the
	//      upstream server, so it is reported honestly as Unknown rather than mislabeled (WhenTransportFaults,
	//      WhenResolutionFails).
	//
	//   4. Control-flow signals that must NOT be swallowed: a cancellation through the caller's token propagates
	//      instead of becoming a failure row (WhenCancelledThroughToken).
	//
	//   5. Invalid arguments: a blank name or null options is a programming error and throws before any work
	//      (WhenBackendNameBlank, WhenBackendNull).

	// --- 1. Happy path ---

	/// <summary>
	/// Verifies that <see cref="BackendModelFetcher.FetchAsync"/> turns a successful discovery into a success
	/// result carrying the resolved models in their discovered order.
	/// </summary>
	[Fact]
	public async Task FetchAsync_WhenDiscoverySucceeds_ReturnsSuccessWithModels()
	{
		// Arrange
		DiscoveryCandidate alpha = Candidate("alpha");
		DiscoveryCandidate beta = Candidate("beta");
		var discovery = new FakeDiscovery([alpha, beta]);
		var sut = new BackendModelFetcher(new FakeResolver(), discovery);

		// Act
		BackendFetchResult result =
			await sut.FetchAsync("cloud", Backend(), DiscoveryProbePolicy.ProbeAll, CancellationToken.None);

		// Assert
		Assert.True(result.Succeeded);
		Assert.Equal("cloud", result.BackendName);
		Assert.Null(result.ErrorKind);
		Assert.Null(result.ErrorMessage);
		Assert.NotNull(result.Models);
		Assert.Equal(2, result.Models.Count);
		Assert.Same(alpha, result.Models[0]);
		Assert.Same(beta, result.Models[1]);
	}

	/// <summary>
	/// Verifies that <see cref="BackendModelFetcher.FetchAsync"/> resolves the supplied backend through the draft
	/// path — the only path that works on the chassis, where no named backend clients are registered.
	/// </summary>
	[Fact]
	public async Task FetchAsync_WhenFetching_UsesDraftResolution()
	{
		// Arrange
		var resolver = new FakeResolver();
		BackendOptions backend = Backend();
		var sut = new BackendModelFetcher(resolver, new FakeDiscovery([]));

		// Act
		await sut.FetchAsync("cloud", backend, DiscoveryProbePolicy.ProbeAll, CancellationToken.None);

		// Assert: the committed Resolve path was never taken; the draft path saw the exact options passed in.
		Assert.Equal(0, resolver.ResolveCallCount);
		Assert.Same(backend, resolver.LastDraft);
	}

	/// <summary>
	/// Verifies that <see cref="BackendModelFetcher.FetchAsync"/> forwards the
	/// <see cref="DiscoveryProbePolicy.NeverProbe"/> policy to discovery unchanged, so the admin surface can load
	/// fast without blocking on any upstream probe.
	/// </summary>
	[Fact]
	public async Task FetchAsync_WhenPolicyIsNeverProbe_ForwardsNeverProbe()
	{
		// Arrange
		var discovery = new FakeDiscovery([]);
		var sut = new BackendModelFetcher(new FakeResolver(), discovery);

		// Act
		await sut.FetchAsync("cloud", Backend(), DiscoveryProbePolicy.NeverProbe, CancellationToken.None);

		// Assert
		Assert.Equal(DiscoveryProbePolicy.NeverProbe, discovery.LastProbePolicy);
	}

	/// <summary>
	/// Verifies that <see cref="BackendModelFetcher.FetchAsync"/> forwards the
	/// <see cref="DiscoveryProbePolicy.ProbeAll"/> policy to discovery unchanged, so an explicit operator-triggered
	/// enrichment probes every model regardless of whether its context window could be determined.
	/// </summary>
	[Fact]
	public async Task FetchAsync_WhenPolicyIsProbeAll_ForwardsProbeAll()
	{
		// Arrange
		var discovery = new FakeDiscovery([]);
		var sut = new BackendModelFetcher(new FakeResolver(), discovery);

		// Act
		await sut.FetchAsync("cloud", Backend(), DiscoveryProbePolicy.ProbeAll, CancellationToken.None);

		// Assert
		Assert.Equal(DiscoveryProbePolicy.ProbeAll, discovery.LastProbePolicy);
	}

	// --- 2. Attributable failures: upstream answered with an error status ---

	/// <summary>
	/// Verifies that <see cref="BackendModelFetcher.FetchAsync"/> classifies a <see cref="ProviderException"/>
	/// carrying an authentication status (<c>401 Unauthorized</c> or <c>403 Forbidden</c>) as
	/// <see cref="BackendFetchErrorKind.Authentication"/>, propagating the exception message verbatim.
	/// </summary>
	/// <param name="statusCode">The upstream authentication status the provider failed with.</param>
	[Theory]
	[InlineData(HttpStatusCode.Unauthorized)] // 401 -> credentials
	[InlineData(HttpStatusCode.Forbidden)]    // 403 -> credentials
	public async Task FetchAsync_WhenProviderRejectsWithAuthStatus_ClassifiesAsAuthentication(HttpStatusCode statusCode)
	{
		// Arrange
		const string message = "upstream said no";
		var discovery = new FakeDiscovery(new ProviderException(statusCode, message));
		var sut = new BackendModelFetcher(new FakeResolver(), discovery);

		// Act
		BackendFetchResult result =
			await sut.FetchAsync("cloud", Backend(), DiscoveryProbePolicy.ProbeAll, CancellationToken.None);

		// Assert
		Assert.False(result.Succeeded);
		Assert.Equal("cloud", result.BackendName);
		Assert.Equal(BackendFetchErrorKind.Authentication, result.ErrorKind);
		Assert.Equal(message, result.ErrorMessage);
		Assert.Null(result.Models);
	}

	/// <summary>
	/// Verifies that <see cref="BackendModelFetcher.FetchAsync"/> classifies a <see cref="ProviderException"/>
	/// carrying any non-authentication status (the backend was reached but answered with an error) as
	/// <see cref="BackendFetchErrorKind.Upstream"/>, propagating the exception message verbatim.
	/// </summary>
	/// <param name="statusCode">The upstream error status the provider failed with.</param>
	[Theory]
	[InlineData(HttpStatusCode.NotFound)]        // 404 -> upstream/route
	[InlineData(HttpStatusCode.TooManyRequests)] // 429 -> upstream throttle
	[InlineData(HttpStatusCode.BadGateway)]      // 502 -> upstream outage
	public async Task FetchAsync_WhenProviderRejectsWithOtherStatus_ClassifiesAsUpstream(HttpStatusCode statusCode)
	{
		// Arrange
		const string message = "upstream said no";
		var discovery = new FakeDiscovery(new ProviderException(statusCode, message));
		var sut = new BackendModelFetcher(new FakeResolver(), discovery);

		// Act
		BackendFetchResult result =
			await sut.FetchAsync("cloud", Backend(), DiscoveryProbePolicy.ProbeAll, CancellationToken.None);

		// Assert
		Assert.False(result.Succeeded);
		Assert.Equal("cloud", result.BackendName);
		Assert.Equal(BackendFetchErrorKind.Upstream, result.ErrorKind);
		Assert.Equal(message, result.ErrorMessage);
		Assert.Null(result.Models);
	}

	// --- 3. Unattributable failures: honest Unknown rather than a guessed cause ---

	/// <summary>
	/// Verifies that <see cref="BackendModelFetcher.FetchAsync"/> reports a transport-level fault (one not carried
	/// as a <see cref="ProviderException"/>) as <see cref="BackendFetchErrorKind.Unknown"/>, because the failure
	/// cannot be pinned on the upstream server with confidence.
	/// </summary>
	[Fact]
	public async Task FetchAsync_WhenTransportFaults_ClassifiesAsUnknown()
	{
		// Arrange: a bare HttpRequestException stands in for DNS/connection/TLS faults the provider never reached.
		const string message = "connection refused";
		var discovery = new FakeDiscovery(new HttpRequestException(message));
		var sut = new BackendModelFetcher(new FakeResolver(), discovery);

		// Act
		BackendFetchResult result =
			await sut.FetchAsync("cloud", Backend(), DiscoveryProbePolicy.ProbeAll, CancellationToken.None);

		// Assert
		Assert.False(result.Succeeded);
		Assert.Equal(BackendFetchErrorKind.Unknown, result.ErrorKind);
		Assert.Equal(message, result.ErrorMessage);
		Assert.Null(result.Models);
	}

	/// <summary>
	/// Verifies that <see cref="BackendModelFetcher.FetchAsync"/> captures a resolution failure (for example an
	/// unknown provider type) as an <see cref="BackendFetchErrorKind.Unknown"/> failure row rather than letting it
	/// throw, so one misconfigured backend does not blank the fetch of every other backend.
	/// </summary>
	[Fact]
	public async Task FetchAsync_WhenResolutionFails_ClassifiesAsUnknown()
	{
		// Arrange: the resolver throws as ProviderResolver.ResolveDraft does for an unhandled provider type.
		const string message = "No provider adapter is registered for provider type 'mystery'.";
		var resolver = new FakeResolver(new InvalidOperationException(message));
		var sut = new BackendModelFetcher(resolver, new FakeDiscovery([]));

		// Act
		BackendFetchResult result =
			await sut.FetchAsync("cloud", Backend(), DiscoveryProbePolicy.ProbeAll, CancellationToken.None);

		// Assert
		Assert.False(result.Succeeded);
		Assert.Equal(BackendFetchErrorKind.Unknown, result.ErrorKind);
		Assert.Equal(message, result.ErrorMessage);
		Assert.Null(result.Models);
	}

	// --- 4. Control-flow signals that must NOT be swallowed ---

	/// <summary>
	/// Verifies that <see cref="BackendModelFetcher.FetchAsync"/> lets a cancellation through the caller's token
	/// propagate as an <see cref="OperationCanceledException"/> rather than swallowing it into a failure result,
	/// so the orchestrating admin service can abort the whole batch.
	/// </summary>
	[Fact]
	public async Task FetchAsync_WhenCancelledThroughToken_PropagatesCancellation()
	{
		// Arrange: the discovery observes the token and throws the matching OperationCanceledException, exactly as
		// a real cancelled await would.
		using var cts = new CancellationTokenSource();
		await cts.CancelAsync();
		var discovery = new FakeDiscovery(cancelOnToken: true);
		var sut = new BackendModelFetcher(new FakeResolver(), discovery);

		// Act + Assert
		await Assert.ThrowsAsync<OperationCanceledException>(() =>
			sut.FetchAsync("cloud", Backend(), DiscoveryProbePolicy.ProbeAll, cts.Token));
	}

	// --- 5. Invalid arguments ---

	/// <summary>
	/// Verifies that <see cref="BackendModelFetcher.FetchAsync"/> rejects a blank backend name before doing any
	/// work, because a blank name is a programming error in the caller, not a backend failure.
	/// </summary>
	[Fact]
	public async Task FetchAsync_WhenBackendNameBlank_ThrowsArgumentException()
	{
		// Arrange
		var sut = new BackendModelFetcher(new FakeResolver(), new FakeDiscovery([]));

		// Act + Assert
		var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
			                sut.FetchAsync("   ", Backend(), DiscoveryProbePolicy.ProbeAll, CancellationToken.None));
		Assert.Equal("backendName", exception.ParamName);
	}

	/// <summary>
	/// Verifies that <see cref="BackendModelFetcher.FetchAsync"/> rejects a <see langword="null"/> backend options
	/// argument before doing any work.
	/// </summary>
	[Fact]
	public async Task FetchAsync_WhenBackendNull_ThrowsArgumentNullException()
	{
		// Arrange
		var sut = new BackendModelFetcher(new FakeResolver(), new FakeDiscovery([]));

		// Act + Assert
		var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
			                sut.FetchAsync("cloud", null!, DiscoveryProbePolicy.ProbeAll, CancellationToken.None));
		Assert.Equal("backend", exception.ParamName);
	}

	#endregion

	#region FetchStreamingAsync()

	// FetchStreamingAsync(): the same spine as FetchAsync, but failures throw instead of being captured.
	//
	// The streaming path yields each model as it resolves, so it cannot fold a late failure back into a result.
	// Two things follow, and these tests pin both down:
	//
	//   - A fault surfaces as a thrown BackendFetchException carrying the same classified ErrorKind the buffered
	//     path would have recorded (auth, upstream, unknown). A caller-requested cancellation is the exception:
	//     it stays an OperationCanceledException so the caller can tell its own abort apart from a backend fault.
	//
	//   - The method is an async iterator, so its body (argument guards included) does not run until the stream
	//     is enumerated. Every test therefore drains the sequence through DrainAsync to trigger the deferred work,
	//     even the argument-guard cases that FetchAsync rejects eagerly.
	//
	// The scenarios mirror the buffered region one-for-one:
	//
	//   1. Happy path: yields the resolved models in order (WhenDiscoverySucceeds), through ResolveDraft
	//      (UsesDraftResolution), forwarding the caller's probe policy unchanged (WhenPolicyIsNeverProbe,
	//      WhenPolicyIsProbeAll).
	//
	//   2. Attributable failures: 401/403 throws classified as Authentication (WhenProviderRejectsWithAuthStatus),
	//      every other status as Upstream (WhenProviderRejectsWithOtherStatus).
	//
	//   3. Unattributable failures: a transport fault (WhenTransportFaults) or a deferred resolution fault
	//      (WhenResolutionFails) throws classified as Unknown.
	//
	//   4. Control-flow signals that must NOT be wrapped: a cancellation through the caller's token propagates as
	//      an OperationCanceledException, not a BackendFetchException (WhenCancelledThroughToken).
	//
	//   5. Invalid arguments: a blank name or null options throws on enumeration (WhenBackendNameBlank,
	//      WhenBackendNull).

	// --- 1. Happy path ---

	/// <summary>
	/// Verifies that <see cref="BackendModelFetcher.FetchStreamingAsync"/> yields each successfully discovered
	/// model, preserving the discovered order.
	/// </summary>
	[Fact]
	public async Task FetchStreamingAsync_WhenDiscoverySucceeds_YieldsModelsInOrder()
	{
		// Arrange
		DiscoveryCandidate alpha = Candidate("alpha");
		DiscoveryCandidate beta = Candidate("beta");
		var discovery = new FakeDiscovery([alpha, beta]);
		var sut = new BackendModelFetcher(new FakeResolver(), discovery);

		// Act
		List<DiscoveryCandidate> models = await DrainAsync(
			                                  sut.FetchStreamingAsync(
				                                  "cloud",
				                                  Backend(),
				                                  DiscoveryProbePolicy.ProbeAll,
				                                  CancellationToken.None));

		// Assert
		Assert.Equal(2, models.Count);
		Assert.Same(alpha, models[0]);
		Assert.Same(beta, models[1]);
	}

	/// <summary>
	/// Verifies that <see cref="BackendModelFetcher.FetchStreamingAsync"/> resolves the supplied backend through
	/// the draft path, the only path that works on the chassis, where no named backend clients are registered.
	/// </summary>
	[Fact]
	public async Task FetchStreamingAsync_WhenFetching_UsesDraftResolution()
	{
		// Arrange
		var resolver = new FakeResolver();
		BackendOptions backend = Backend();
		var sut = new BackendModelFetcher(resolver, new FakeDiscovery([]));

		// Act
		await DrainAsync(
			sut.FetchStreamingAsync("cloud", backend, DiscoveryProbePolicy.ProbeAll, CancellationToken.None));

		// Assert: the committed Resolve path was never taken; the draft path saw the exact options passed in.
		Assert.Equal(0, resolver.ResolveCallCount);
		Assert.Same(backend, resolver.LastDraft);
	}

	/// <summary>
	/// Verifies that <see cref="BackendModelFetcher.FetchStreamingAsync"/> forwards the
	/// <see cref="DiscoveryProbePolicy.NeverProbe"/> policy to discovery unchanged.
	/// </summary>
	[Fact]
	public async Task FetchStreamingAsync_WhenPolicyIsNeverProbe_ForwardsNeverProbe()
	{
		// Arrange
		var discovery = new FakeDiscovery([]);
		var sut = new BackendModelFetcher(new FakeResolver(), discovery);

		// Act
		await DrainAsync(
			sut.FetchStreamingAsync("cloud", Backend(), DiscoveryProbePolicy.NeverProbe, CancellationToken.None));

		// Assert
		Assert.Equal(DiscoveryProbePolicy.NeverProbe, discovery.LastProbePolicy);
	}

	/// <summary>
	/// Verifies that <see cref="BackendModelFetcher.FetchStreamingAsync"/> forwards the
	/// <see cref="DiscoveryProbePolicy.ProbeAll"/> policy to discovery unchanged, the policy the streaming path
	/// exists to serve.
	/// </summary>
	[Fact]
	public async Task FetchStreamingAsync_WhenPolicyIsProbeAll_ForwardsProbeAll()
	{
		// Arrange
		var discovery = new FakeDiscovery([]);
		var sut = new BackendModelFetcher(new FakeResolver(), discovery);

		// Act
		await DrainAsync(
			sut.FetchStreamingAsync("cloud", Backend(), DiscoveryProbePolicy.ProbeAll, CancellationToken.None));

		// Assert
		Assert.Equal(DiscoveryProbePolicy.ProbeAll, discovery.LastProbePolicy);
	}

	// --- 2. Attributable failures: upstream answered with an error status ---

	/// <summary>
	/// Verifies that <see cref="BackendModelFetcher.FetchStreamingAsync"/> throws a
	/// <see cref="BackendFetchException"/> classified as <see cref="BackendFetchErrorKind.Authentication"/> when
	/// discovery fails with an authentication status (<c>401 Unauthorized</c> or <c>403 Forbidden</c>),
	/// propagating the message verbatim.
	/// </summary>
	/// <param name="statusCode">The upstream authentication status the provider failed with.</param>
	[Theory]
	[InlineData(HttpStatusCode.Unauthorized)] // 401 -> credentials
	[InlineData(HttpStatusCode.Forbidden)]    // 403 -> credentials
	public async Task FetchStreamingAsync_WhenProviderRejectsWithAuthStatus_ThrowsClassifiedAsAuthentication(
		HttpStatusCode statusCode)
	{
		// Arrange
		const string message = "upstream said no";
		var discovery = new FakeDiscovery(new ProviderException(statusCode, message));
		var sut = new BackendModelFetcher(new FakeResolver(), discovery);

		// Act + Assert
		var exception = await Assert.ThrowsAsync<BackendFetchException>(() => DrainAsync(
			                sut.FetchStreamingAsync(
				                "cloud",
				                Backend(),
				                DiscoveryProbePolicy.ProbeAll,
				                CancellationToken.None)));
		Assert.Equal(BackendFetchErrorKind.Authentication, exception.ErrorKind);
		Assert.Equal(message, exception.Message);
	}

	/// <summary>
	/// Verifies that <see cref="BackendModelFetcher.FetchStreamingAsync"/> throws a
	/// <see cref="BackendFetchException"/> classified as <see cref="BackendFetchErrorKind.Upstream"/> when
	/// discovery fails with any non-authentication status, propagating the message verbatim.
	/// </summary>
	/// <param name="statusCode">The upstream error status the provider failed with.</param>
	[Theory]
	[InlineData(HttpStatusCode.NotFound)]        // 404 -> upstream/route
	[InlineData(HttpStatusCode.TooManyRequests)] // 429 -> upstream throttle
	[InlineData(HttpStatusCode.BadGateway)]      // 502 -> upstream outage
	public async Task FetchStreamingAsync_WhenProviderRejectsWithOtherStatus_ThrowsClassifiedAsUpstream(
		HttpStatusCode statusCode)
	{
		// Arrange
		const string message = "upstream said no";
		var discovery = new FakeDiscovery(new ProviderException(statusCode, message));
		var sut = new BackendModelFetcher(new FakeResolver(), discovery);

		// Act + Assert
		var exception = await Assert.ThrowsAsync<BackendFetchException>(() => DrainAsync(
			                sut.FetchStreamingAsync(
				                "cloud",
				                Backend(),
				                DiscoveryProbePolicy.ProbeAll,
				                CancellationToken.None)));
		Assert.Equal(BackendFetchErrorKind.Upstream, exception.ErrorKind);
		Assert.Equal(message, exception.Message);
	}

	// --- 3. Unattributable failures: honest Unknown rather than a guessed cause ---

	/// <summary>
	/// Verifies that <see cref="BackendModelFetcher.FetchStreamingAsync"/> throws a
	/// <see cref="BackendFetchException"/> classified as <see cref="BackendFetchErrorKind.Unknown"/> for a
	/// transport-level fault (one not carried as a <see cref="ProviderException"/>), because the failure cannot
	/// be pinned on the upstream server with confidence.
	/// </summary>
	[Fact]
	public async Task FetchStreamingAsync_WhenTransportFaults_ThrowsClassifiedAsUnknown()
	{
		// Arrange: a bare HttpRequestException stands in for DNS/connection/TLS faults the provider never reached.
		const string message = "connection refused";
		var discovery = new FakeDiscovery(new HttpRequestException(message));
		var sut = new BackendModelFetcher(new FakeResolver(), discovery);

		// Act + Assert
		var exception = await Assert.ThrowsAsync<BackendFetchException>(() => DrainAsync(
			                sut.FetchStreamingAsync(
				                "cloud",
				                Backend(),
				                DiscoveryProbePolicy.ProbeAll,
				                CancellationToken.None)));
		Assert.Equal(BackendFetchErrorKind.Unknown, exception.ErrorKind);
		Assert.Equal(message, exception.Message);
	}

	/// <summary>
	/// Verifies that <see cref="BackendModelFetcher.FetchStreamingAsync"/> throws a
	/// <see cref="BackendFetchException"/> classified as <see cref="BackendFetchErrorKind.Unknown"/> when draft
	/// resolution fails. The fault is deferred until the first enumeration, so draining the stream surfaces it.
	/// </summary>
	[Fact]
	public async Task FetchStreamingAsync_WhenResolutionFails_ThrowsClassifiedAsUnknown()
	{
		// Arrange: the resolver throws as ProviderResolver.ResolveDraft does for an unhandled provider type.
		const string message = "No provider adapter is registered for provider type 'mystery'.";
		var resolver = new FakeResolver(new InvalidOperationException(message));
		var sut = new BackendModelFetcher(resolver, new FakeDiscovery([]));

		// Act + Assert
		var exception = await Assert.ThrowsAsync<BackendFetchException>(() => DrainAsync(
			                sut.FetchStreamingAsync(
				                "cloud",
				                Backend(),
				                DiscoveryProbePolicy.ProbeAll,
				                CancellationToken.None)));
		Assert.Equal(BackendFetchErrorKind.Unknown, exception.ErrorKind);
		Assert.Equal(message, exception.Message);
	}

	// --- 4. Control-flow signals that must NOT be wrapped ---

	/// <summary>
	/// Verifies that <see cref="BackendModelFetcher.FetchStreamingAsync"/> lets a cancellation through the
	/// caller's token propagate as an <see cref="OperationCanceledException"/> rather than wrapping it in a
	/// <see cref="BackendFetchException"/>, so the admin surface can tell its own abort apart from a backend fault.
	/// </summary>
	[Fact]
	public async Task FetchStreamingAsync_WhenCancelledThroughToken_PropagatesCancellation()
	{
		// Arrange: the discovery observes the token and throws the matching OperationCanceledException, exactly as
		// a real cancelled await would.
		using var cts = new CancellationTokenSource();
		await cts.CancelAsync();
		var discovery = new FakeDiscovery(cancelOnToken: true);
		var sut = new BackendModelFetcher(new FakeResolver(), discovery);

		// Act + Assert: the cancellation stays an OperationCanceledException; it is never wrapped as a failure.
		await Assert.ThrowsAsync<OperationCanceledException>(() => DrainAsync(
			sut.FetchStreamingAsync("cloud", Backend(), DiscoveryProbePolicy.ProbeAll, cts.Token)));
	}

	// --- 5. Invalid arguments ---

	/// <summary>
	/// Verifies that <see cref="BackendModelFetcher.FetchStreamingAsync"/> rejects a blank backend name. The guard
	/// is deferred until enumeration because the method is an async iterator, so draining the stream triggers it.
	/// </summary>
	[Fact]
	public async Task FetchStreamingAsync_WhenBackendNameBlank_ThrowsArgumentException()
	{
		// Arrange
		var sut = new BackendModelFetcher(new FakeResolver(), new FakeDiscovery([]));

		// Act + Assert
		var exception = await Assert.ThrowsAsync<ArgumentException>(() => DrainAsync(
			                sut.FetchStreamingAsync(
				                "   ",
				                Backend(),
				                DiscoveryProbePolicy.ProbeAll,
				                CancellationToken.None)));
		Assert.Equal("backendName", exception.ParamName);
	}

	/// <summary>
	/// Verifies that <see cref="BackendModelFetcher.FetchStreamingAsync"/> rejects a <see langword="null"/> backend
	/// options argument. The guard is deferred until enumeration, so draining the stream triggers it.
	/// </summary>
	[Fact]
	public async Task FetchStreamingAsync_WhenBackendNull_ThrowsArgumentNullException()
	{
		// Arrange
		var sut = new BackendModelFetcher(new FakeResolver(), new FakeDiscovery([]));

		// Act + Assert
		var exception = await Assert.ThrowsAsync<ArgumentNullException>(() => DrainAsync(
			                sut.FetchStreamingAsync(
				                "cloud",
				                null!,
				                DiscoveryProbePolicy.ProbeAll,
				                CancellationToken.None)));
		Assert.Equal("backend", exception.ParamName);
	}

	#endregion

	#region Test infrastructure

	/// <summary>
	/// Builds backend options for the fetch; URL and key are placeholders because the fetcher's collaborators are
	/// faked and never open a connection.
	/// </summary>
	/// <returns>The configured backend options.</returns>
	private static BackendOptions Backend() => new()
		{ BaseUrl = "https://x/v1", ProviderType = "openai", ApiKey = "placeholder-key" };

	/// <summary>
	/// Builds a resolved discovery candidate with the given client name; the other fields are irrelevant to the
	/// fetcher, which passes the candidate list through unchanged.
	/// </summary>
	/// <param name="clientName">The client-facing model name.</param>
	/// <returns>The discovery candidate.</returns>
	private static DiscoveryCandidate Candidate(string clientName) => new(
		clientName,
		clientName,
		ReportedContextLength: 4096,
		ModelCapabilities.CompletionOnly);

	/// <summary>
	/// Enumerates a streaming fetch to completion and collects its candidates. The fetcher's guards and fault
	/// wrapping are deferred until the sequence is enumerated, so a test must drain the stream to trigger them.
	/// This helper is that drain.
	/// </summary>
	/// <param name="source">The streaming fetch to enumerate.</param>
	/// <returns>The candidates yielded, in stream order.</returns>
	private static async Task<List<DiscoveryCandidate>> DrainAsync(IAsyncEnumerable<DiscoveryCandidate> source)
	{
		List<DiscoveryCandidate> drained = [];
		await foreach (DiscoveryCandidate candidate in source)
		{
			drained.Add(candidate);
		}

		return drained;
	}

	/// <summary>
	/// A resolver test double that records how it was called and optionally throws from the draft path. It returns
	/// a resolved backend around a throwing adapter, because the fetcher never invokes the adapter directly — it
	/// delegates discovery to the (faked) <see cref="IBackendModelDiscovery"/>.
	/// </summary>
	/// <param name="draftFault">The exception the draft path throws, or <see langword="null"/> to resolve normally.</param>
	private sealed class FakeResolver(Exception? draftFault = null) : IProviderResolver
	{
		/// <summary>Gets the number of times the committed <see cref="Resolve"/> path was taken (expected: zero).</summary>
		public int ResolveCallCount { get; private set; }

		/// <summary>Gets the draft options the last <see cref="ResolveDraft"/> call received.</summary>
		public BackendOptions? LastDraft { get; private set; }

		/// <inheritdoc/>
		public ResolvedBackend Resolve(string backendName)
		{
			ResolveCallCount++;
			return new ResolvedBackend(new ThrowingAdapter(), new BackendContext(backendName));
		}

		/// <inheritdoc/>
		public ResolvedBackend ResolveDraft(BackendOptions draft)
		{
			LastDraft = draft;
			if (draftFault is { } fault) throw fault;
			return new ResolvedBackend(new ThrowingAdapter(), new BackendContext("(draft)", draft));
		}
	}

	/// <summary>
	/// A discovery test double that returns a fixed candidate list, throws a fixed fault, or observes the
	/// cancellation token — whichever the constructed scenario selected — and records the probe policy it saw.
	/// </summary>
	private sealed class FakeDiscovery : IBackendModelDiscovery
	{
		private readonly IReadOnlyList<DiscoveryCandidate>? mCandidates;
		private readonly Exception?                         mFault;
		private readonly bool                               mCancelOnToken;

		/// <summary>
		/// Initializes a discovery double that returns the given candidates.
		/// </summary>
		/// <param name="candidates">The candidates to return from discovery.</param>
		public FakeDiscovery(IReadOnlyList<DiscoveryCandidate> candidates)
		{
			mCandidates = candidates;
		}

		/// <summary>
		/// Initializes a discovery double that throws the given fault.
		/// </summary>
		/// <param name="fault">The exception discovery throws.</param>
		public FakeDiscovery(Exception fault)
		{
			mFault = fault;
		}

		/// <summary>
		/// Initializes a discovery double that throws <see cref="OperationCanceledException"/> when the token is
		/// cancelled, modelling a real cancelled await.
		/// </summary>
		/// <param name="cancelOnToken">Whether to observe the token and throw on cancellation.</param>
		public FakeDiscovery(bool cancelOnToken)
		{
			mCancelOnToken = cancelOnToken;
		}

		/// <summary>Gets the probe policy the last <see cref="DiscoverAsync"/> call received.</summary>
		public DiscoveryProbePolicy? LastProbePolicy { get; private set; }

		/// <inheritdoc/>
		public Task<IReadOnlyList<DiscoveryCandidate>> DiscoverAsync(
			ResolvedBackend      resolved,
			BackendOptions       backend,
			DiscoveryProbePolicy probePolicy,
			CancellationToken    cancellationToken)
		{
			LastProbePolicy = probePolicy;

			if (mCancelOnToken)
			{
				cancellationToken.ThrowIfCancellationRequested();
			}

			if (mFault is { } fault)
			{
				return Task.FromException<IReadOnlyList<DiscoveryCandidate>>(fault);
			}

			return Task.FromResult(mCandidates!);
		}

		/// <inheritdoc/>
		public async IAsyncEnumerable<DiscoveryCandidate> DiscoverStreamingAsync(
			ResolvedBackend                            resolved,
			BackendOptions                             backend,
			DiscoveryProbePolicy                       probePolicy,
			[EnumeratorCancellation] CancellationToken cancellationToken)
		{
			LastProbePolicy = probePolicy;

			if (mCancelOnToken)
			{
				cancellationToken.ThrowIfCancellationRequested();
			}

			if (mFault is { } fault)
			{
				throw fault;
			}

			await Task.CompletedTask.ConfigureAwait(false);

			foreach (DiscoveryCandidate candidate in mCandidates!)
			{
				cancellationToken.ThrowIfCancellationRequested();
				yield return candidate;
			}
		}
	}

	/// <summary>
	/// A provider adapter that throws from every member. The fetcher never calls it directly — discovery is faked —
	/// so it exists only to satisfy the <see cref="ResolvedBackend"/> shape the resolver returns.
	/// </summary>
	private sealed class ThrowingAdapter : IProviderAdapter
	{
		/// <inheritdoc/>
		public string ProviderType => "openai";

		/// <inheritdoc/>
		public Task<IReadOnlyList<DiscoveredModel>> DiscoverModelsAsync(
			BackendContext    backend,
			CancellationToken cancellationToken) => throw new NotSupportedException();

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
