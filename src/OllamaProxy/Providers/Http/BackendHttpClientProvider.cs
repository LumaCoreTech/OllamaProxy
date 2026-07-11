// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Microsoft.Extensions.Options;

using OllamaProxy.Configuration;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Providers.Http;

/// <summary>
/// Default <see cref="IBackendHttpClientProvider"/> that resolves backend clients from the underlying
/// <see cref="IHttpClientFactory"/> using the shared <see cref="BackendHttpClientNames"/> convention.
/// The wrapped factory is thread-safe, so a single registered instance serves all callers.
/// </summary>
sealed class BackendHttpClientProvider : IBackendHttpClientProvider
{
	private readonly IHttpClientFactory       mHttpClientFactory;
	private readonly BackendConnectionOptions mConnectionOptions;

	/// <summary>
	/// Initializes a new instance of the <see cref="BackendHttpClientProvider"/> class.
	/// </summary>
	/// <param name="httpClientFactory">The factory used to materialize named backend clients.</param>
	/// <param name="proxyOptions">
	/// The proxy options supplying the connection tuning used when building ad-hoc draft clients.
	/// </param>
	/// <exception cref="ArgumentNullException">
	/// <paramref name="httpClientFactory"/> or <paramref name="proxyOptions"/> is <see langword="null"/>.
	/// </exception>
	public BackendHttpClientProvider(
		IHttpClientFactory     httpClientFactory,
		IOptions<ProxyOptions> proxyOptions)
	{
		ArgumentNullException.ThrowIfNull(httpClientFactory);
		ArgumentNullException.ThrowIfNull(proxyOptions);
		mHttpClientFactory = httpClientFactory;
		mConnectionOptions = proxyOptions.Value.Connection;
	}

	/// <inheritdoc/>
	public HttpClient CreateClient(string backendName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(backendName);
		return mHttpClientFactory.CreateClient(BackendHttpClientNames.ForBackend(backendName));
	}

	/// <inheritdoc/>
	public HttpClient CreateClient(BackendContext backend)
	{
		ArgumentNullException.ThrowIfNull(backend);

		// A committed backend has a named, resilience-wrapped client registered at startup; reuse it.
		// A draft backend has none (it is not yet in configuration) so build a one-shot ad-hoc client
		// from its inline options instead. Both paths share the same wire configuration via
		// BackendHttpClientConfiguration so the draft preview behaves identically to a committed call.
		return backend.Draft is { } draft
			       ? BackendHttpClientConfiguration.CreateAdHocClient(draft, mConnectionOptions)
			       : CreateClient(backend.Name);
	}
}
