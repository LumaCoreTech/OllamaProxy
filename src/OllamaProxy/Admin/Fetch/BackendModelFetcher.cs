// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Net;
using System.Runtime.CompilerServices;

using OllamaProxy.Configuration;
using OllamaProxy.Core;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Admin.Fetch;

/// <summary>
/// The default <see cref="IBackendModelFetcher"/>. It resolves the supplied backend options as a <b>draft</b>
/// (<see cref="IProviderResolver.ResolveDraft"/>) and discovers against it under the
/// <see cref="DiscoveryProbePolicy"/> the caller selects. So the fetch reads the backend's base address,
/// credentials, and probing settings straight from the options it is handed, never from a name-keyed
/// registration that was frozen at startup. That is what lets the admin surface observe the current on-disk
/// configuration (and unsaved previews) without waiting for the proxy to recycle. It can do that because it
/// lives on the non-recycling chassis.
/// </summary>
/// <remarks>
/// Going through the draft path is not merely convenient on the chassis; it is required. The chassis never
/// registers the per-backend named HTTP clients (<c>AddBackendHttpClients</c> runs only on the inner proxy
/// host), so a committed-name resolution would have no client to use. The draft path builds a one-shot ad-hoc
/// client from the inline options instead. That both sidesteps the missing client and guarantees freshness. The
/// type holds no state beyond its injected collaborators and is safe to share as a singleton.
/// </remarks>
sealed class BackendModelFetcher : IBackendModelFetcher
{
	private readonly IProviderResolver      mResolver;
	private readonly IBackendModelDiscovery mDiscovery;

	/// <summary>
	/// Initializes a new instance of the <see cref="BackendModelFetcher"/> class.
	/// </summary>
	/// <param name="resolver">Resolves the supplied backend options to an adapter via the draft path.</param>
	/// <param name="discovery">Runs the shared discover-then-resolve orchestration against the resolved backend.</param>
	/// <exception cref="ArgumentNullException">
	/// <paramref name="resolver"/> or <paramref name="discovery"/> is <see langword="null"/>.
	/// </exception>
	public BackendModelFetcher(IProviderResolver resolver, IBackendModelDiscovery discovery)
	{
		ArgumentNullException.ThrowIfNull(resolver);
		ArgumentNullException.ThrowIfNull(discovery);

		mResolver = resolver;
		mDiscovery = discovery;
	}

	/// <inheritdoc/>
	public async Task<BackendFetchResult> FetchAsync(
		string               backendName,
		BackendOptions       backend,
		DiscoveryProbePolicy probePolicy,
		CancellationToken    cancellationToken)
	{
		// Guards run before the try: a null backend or blank name is a programming error in the calling admin
		// service, not a backend failure, so it must surface as an exception rather than be captured as an
		// "Unknown" fetch result that hides the bug behind a UI error row.
		ArgumentException.ThrowIfNullOrWhiteSpace(backendName);
		ArgumentNullException.ThrowIfNull(backend);

		try
		{
			// ResolveDraft selects the adapter by the backend's provider type and pairs it with a draft context,
			// so discovery builds an ad-hoc client from these exact options. Inside the try because an unknown
			// provider type throws here, and the admin surface wants that surfaced as this backend's failure row
			// rather than as an exception that blanks the fetch of every other backend.
			ResolvedBackend resolved = mResolver.ResolveDraft(backend);

			// The probe policy is the caller's choice: NeverProbe for a fast, non-blocking page load that shows
			// only the capabilities a provider already lists, or ProbeAll for an explicit, operator-triggered
			// enrichment that probes every model regardless of whether its context window could be determined.
			IReadOnlyList<DiscoveryCandidate> models = await mDiscovery
				                                           .DiscoverAsync(
					                                           resolved,
					                                           backend,
					                                           probePolicy,
					                                           cancellationToken)
				                                           .ConfigureAwait(false);

			return BackendFetchResult.Success(backendName, models);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// A cancellation the caller asked for is a control-flow signal, not a backend fault: propagate it so
			// the admin service can abort the whole batch instead of recording a misleading failure row. A
			// cancellation NOT tied to our token (for example an internal timeout) does not match this filter and
			// falls through to the generic handler below, where it is honestly classified as Unknown.
			throw;
		}
		catch (ProviderException ex)
		{
			// The backend was reached and answered with an error status, so the fault is attributable to it.
			return BackendFetchResult.Failure(backendName, ClassifyProviderException(ex), ex.Message);
		}
		catch (Exception ex)
		{
			// A transport fault (DNS, connection refused, TLS), a malformed response, or an unknown provider type:
			// the failure cannot be pinned on the upstream server with confidence, so it is reported honestly as
			// Unknown rather than mislabeled as an Upstream error the proxy cannot prove.
			return BackendFetchResult.Failure(backendName, BackendFetchErrorKind.Unknown, ex.Message);
		}
	}

	/// <inheritdoc/>
	public async IAsyncEnumerable<DiscoveryCandidate> FetchStreamingAsync(
		string                                     backendName,
		BackendOptions                             backend,
		DiscoveryProbePolicy                       probePolicy,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		// Guards run before any work, matching FetchAsync: a null backend or blank name is a calling bug that
		// must surface as an exception rather than be smuggled into a fetch failure the operator would misread.
		ArgumentException.ThrowIfNullOrWhiteSpace(backendName);
		ArgumentNullException.ThrowIfNull(backend);

		// Resolving the draft can throw for an unknown provider type. An iterator cannot yield-and-throw in the
		// same place, and a try/catch may not wrap a yield. So resolution and the stream live in a local async
		// iterator, and the whole enumeration is wrapped on the way out. Every fault (resolution, listing, or a
		// per-model probe) is classified into a BackendFetchException, while a caller cancellation passes through.
		IAsyncEnumerator<DiscoveryCandidate> enumerator =
			StreamAsync(backend, probePolicy, cancellationToken).GetAsyncEnumerator(cancellationToken);

		try
		{
			while (true)
			{
				DiscoveryCandidate candidate;
				try
				{
					if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) yield break;
					candidate = enumerator.Current;
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					// A cancellation the caller asked for is control flow, not a backend fault: let it propagate
					// so the admin surface aborts the probe cleanly instead of rendering a misleading failure.
					throw;
				}
				catch (ProviderException ex)
				{
					// The backend answered with an error status, so the fault is attributable to it. Re-thrown as
					// the streaming failure form carrying the same classification a result fetch would record.
					throw new BackendFetchException(ClassifyProviderException(ex), ex.Message, ex);
				}
				catch (Exception ex)
				{
					// A transport fault, a malformed response, or an unknown provider type: not provably the
					// upstream's fault, so reported honestly as Unknown, the streaming twin of FetchAsync's catch.
					throw new BackendFetchException(BackendFetchErrorKind.Unknown, ex.Message, ex);
				}

				yield return candidate;
			}
		}
		finally
		{
			await enumerator.DisposeAsync().ConfigureAwait(false);
		}
	}

	/// <summary>
	/// The inner streaming pipeline: resolves the draft and yields the discovery's candidates verbatim. It is kept
	/// separate from <see cref="FetchStreamingAsync"/> so that method can wrap every fault in a single classified
	/// <see cref="BackendFetchException"/>. That includes the resolution fault this defers until first enumeration.
	/// </summary>
	/// <param name="backend">The backend options to discover against.</param>
	/// <param name="probePolicy">The capability-probing policy to apply.</param>
	/// <param name="cancellationToken">A token to cancel discovery and probing mid-stream.</param>
	/// <returns>The resolved candidates in client-name order, matching the admin table's sort.</returns>
	private async IAsyncEnumerable<DiscoveryCandidate> StreamAsync(
		BackendOptions                             backend,
		DiscoveryProbePolicy                       probePolicy,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		ResolvedBackend resolved = mResolver.ResolveDraft(backend);

		await foreach (DiscoveryCandidate candidate in mDiscovery
			               .DiscoverStreamingAsync(resolved, backend, probePolicy, cancellationToken)
			               .ConfigureAwait(false))
		{
			yield return candidate;
		}
	}

	/// <summary>
	/// Classifies a <see cref="ProviderException"/> by its upstream status: a <c>401</c> or <c>403</c> is a
	/// credential problem (<see cref="BackendFetchErrorKind.Authentication"/>); every other status the backend
	/// returned is an <see cref="BackendFetchErrorKind.Upstream"/> fault.
	/// </summary>
	/// <param name="exception">The provider exception carrying the upstream status code.</param>
	/// <returns>The error kind the status code maps to.</returns>
	private static BackendFetchErrorKind ClassifyProviderException(ProviderException exception) =>
		exception.StatusCode switch
		{
			HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => BackendFetchErrorKind.Authentication,
			var _                                                   => BackendFetchErrorKind.Upstream
		};
}
