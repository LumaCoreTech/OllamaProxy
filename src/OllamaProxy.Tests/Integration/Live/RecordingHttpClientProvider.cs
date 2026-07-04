// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Configuration;
using OllamaProxy.Providers.Http;

namespace OllamaProxy.Tests.Integration.Live;

/// <summary>
/// An <see cref="IBackendHttpClientProvider"/> that hands the provider under test a <em>real</em>
/// <see cref="HttpClient"/> pointed at a live backend, while recording the most recent outgoing request so a
/// test can inspect the exact body the provider posted. It is the only seam swapped between the deterministic
/// integration tests (which inject a canned <c>HttpMessageHandler</c>) and the live conformance suite: here
/// the inner handler is a real <see cref="SocketsHttpHandler"/>, so the provider's translation pipeline runs
/// end to end against the upstream.
/// <para>
/// Each client is configured through the production <see cref="BackendHttpClientConfiguration.Configure"/>, so
/// the base address, bearer authentication, accept headers, and infinite client-level timeout are byte-for-byte
/// what production applies — the test cannot drift from the real wire configuration. The infinite client timeout
/// is intentional and matches production: a live call's duration is bounded by the cancellation token the
/// harness passes, exercising the same cancellation path production relies on for long streaming responses.
/// </para>
/// <para>
/// The recording handler is shared across every client this provider hands out and is wrapped with
/// <c>disposeHandler: false</c>, so the provider's <c>using HttpClient</c> disposal tears down only the thin
/// client wrapper, never the handler or its captured state. That is what lets a two-turn round-trip read the
/// <em>second</em> turn's body after both calls have completed. This type owns the handler and disposes it.
/// </para>
/// </summary>
sealed class RecordingHttpClientProvider : IBackendHttpClientProvider, IDisposable
{
	private readonly BackendOptions          mBackendOptions;
	private readonly RecordingMessageHandler mRecordingHandler;

	/// <summary>
	/// Initializes a new instance of the <see cref="RecordingHttpClientProvider"/> class targeting the
	/// supplied backend, building a real <see cref="SocketsHttpHandler"/> as the transport.
	/// </summary>
	/// <param name="backendOptions">The backend options every issued client is configured from.</param>
	/// <exception cref="ArgumentNullException"><paramref name="backendOptions"/> is <see langword="null"/>.</exception>
	public RecordingHttpClientProvider(BackendOptions backendOptions)
	{
		ArgumentNullException.ThrowIfNull(backendOptions);

		mBackendOptions = backendOptions;

		// A real transport with a bounded connect timeout so an unreachable host fails promptly instead of
		// hanging; the response read itself stays governed by the caller's cancellation token, mirroring how
		// production bounds long streaming responses.
		mRecordingHandler = new RecordingMessageHandler(
			new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) });
	}

	/// <summary>
	/// Gets the body of the most recent request the provider posted, or <see langword="null"/> when no
	/// request carrying a body has been sent yet. After a two-turn round-trip this is the second turn's body.
	/// </summary>
	public string? LastRequestBody => mRecordingHandler.LastRequestBody;

	/// <summary>
	/// Gets the absolute URI of the most recent request the provider sent, or <see langword="null"/> when no
	/// request has been sent yet.
	/// </summary>
	public Uri? LastRequestUri => mRecordingHandler.LastRequestUri;

	/// <inheritdoc/>
	public HttpClient CreateClient(string backendName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(backendName);

		// disposeHandler:false — the provider disposes each returned client via `using`, but the shared
		// recording handler (and the body it captured) must outlive that so the test can read it afterwards.
		HttpClient client = new(mRecordingHandler, disposeHandler: false);
		BackendHttpClientConfiguration.Configure(client, mBackendOptions);
		return client;
	}

	/// <inheritdoc/>
	public void Dispose() => mRecordingHandler.Dispose();

	/// <summary>
	/// A pass-through <see cref="DelegatingHandler"/> that buffers and records each outgoing request's body
	/// and URI before forwarding it to the real transport. Buffering via
	/// <see cref="HttpContent.LoadIntoBufferAsync()"/> lets the body be read here and then re-read by the
	/// transport, so recording never consumes the content the backend must still receive.
	/// </summary>
	private sealed class RecordingMessageHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
	{
		public string? LastRequestBody { get; private set; }

		public Uri? LastRequestUri { get; private set; }

		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken  cancellationToken)
		{
			LastRequestUri = request.RequestUri;

			if (request.Content is not null)
			{
				// Buffer first so reading the body for the record does not consume it before the transport sends.
				await request.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
				LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			}

			return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
		}
	}
}
