// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Net.Http.Headers;

using OllamaProxy.Configuration;

namespace OllamaProxy.Providers.Http;

/// <summary>
/// The single source of truth for turning a <see cref="BackendOptions"/> into a configured
/// <see cref="HttpClient"/>: base address, bearer authentication, the accept headers gateways
/// content-negotiate on, and the infinite client-level timeout. It is shared by two callers so the wire
/// configuration never drifts between them:
/// <list type="number">
///     <item>
///         <description>
///         The startup registration (<see cref="BackendHttpClientServiceCollectionExtensions"/>), which
///         configures one named, resilience-wrapped client per committed backend.
///         </description>
///     </item>
///     <item>
///         <description>
///         The draft path (<see cref="IBackendHttpClientProvider.CreateClient(Abstractions.BackendContext)"/>),
///         which builds a one-shot ad-hoc client for a not-yet-committed backend during preview discovery.
///         </description>
///     </item>
/// </list>
/// </summary>
static class BackendHttpClientConfiguration
{
	/// <summary>
	/// Applies the backend's base address, bearer authentication, content-negotiation accept headers, and
	/// infinite client-level timeout to an existing client. The infinite timeout is intentional: long
	/// streaming responses are bounded by the resilience attempt timeout (committed clients) or the
	/// caller's cancellation token (draft clients and probes), never by an abrupt client cutoff.
	/// </summary>
	/// <param name="client">The client instance to configure.</param>
	/// <param name="backend">The backend options supplying the address and credentials.</param>
	/// <exception cref="ArgumentNullException">
	/// <paramref name="client"/> or <paramref name="backend"/> is <see langword="null"/>.
	/// </exception>
	public static void Configure(HttpClient client, BackendOptions backend)
	{
		ArgumentNullException.ThrowIfNull(client);
		ArgumentNullException.ThrowIfNull(backend);

		// HttpClient.BaseAddress requires a trailing slash so that relative paths are resolved against
		// the full base path rather than its parent. For example, "http://host/v1" would silently drop
		// "/v1" when combined with a relative path, while "http://host/v1/" preserves it correctly.
		string rawUrl = backend.BaseUrl.TrimEnd('/') + '/';
		if (Uri.TryCreate(rawUrl, UriKind.Absolute, out Uri? baseUri)) client.BaseAddress = baseUri;

		if (!string.IsNullOrEmpty(backend.ApiKey))
		{
			client.DefaultRequestHeaders.Authorization =
				new AuthenticationHeaderValue("Bearer", backend.ApiKey);
		}

		// Accept newline-delimited and SSE payloads explicitly; some gateways content-negotiate.
		client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

		// Streaming responses outlive any fixed client timeout; cancellation governs their lifetime.
		client.Timeout = Timeout.InfiniteTimeSpan;
	}

	/// <summary>
	/// Builds a one-shot <see cref="HttpClient"/> for a draft (not-yet-committed) backend, configured
	/// from the supplied options. Unlike the committed clients registered at startup, this ad-hoc client
	/// carries <em>no</em> resilience pipeline: a draft fetch is a rare, interactive operator action, and
	/// discovery and probing impose their own per-attempt timeouts and cancellation, so the extra retry
	/// machinery would add cost without value. Its transport handler is built from
	/// <paramref name="connection"/> so a draft preview establishes connections with the same DNS-refresh
	/// and connect-timeout behavior as a committed call. The caller owns the returned client and must
	/// dispose it.
	/// </summary>
	/// <param name="backend">The draft backend options supplying the address and credentials.</param>
	/// <param name="connection">The connection tuning supplying the transport handler behavior.</param>
	/// <returns>A configured, caller-owned client targeting the draft backend.</returns>
	/// <exception cref="ArgumentNullException">
	/// <paramref name="backend"/> or <paramref name="connection"/> is <see langword="null"/>.
	/// </exception>
	public static HttpClient CreateAdHocClient(BackendOptions backend, BackendConnectionOptions connection)
	{
		ArgumentNullException.ThrowIfNull(backend);
		ArgumentNullException.ThrowIfNull(connection);

		// Default disposeHandler:true so disposing the client tears down its handler and socket pool,
		// correct for a one-shot client that is not pooled across draft fetches.
		HttpClient client = new(BackendHttpHandlerFactory.Create(connection));
		Configure(client, backend);
		return client;
	}
}
