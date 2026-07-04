// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Providers.Http;

/// <summary>
/// Default <see cref="IBackendHttpClientProvider"/> that resolves backend clients from the underlying
/// <see cref="IHttpClientFactory"/> using the shared <see cref="BackendHttpClientNames"/> convention.
/// The wrapped factory is thread-safe, so a single registered instance serves all callers.
/// </summary>
sealed class BackendHttpClientProvider : IBackendHttpClientProvider
{
	private readonly IHttpClientFactory mHttpClientFactory;

	/// <summary>
	/// Initializes a new instance of the <see cref="BackendHttpClientProvider"/> class.
	/// </summary>
	/// <param name="httpClientFactory">The factory used to materialize named backend clients.</param>
	public BackendHttpClientProvider(IHttpClientFactory httpClientFactory)
	{
		ArgumentNullException.ThrowIfNull(httpClientFactory);
		mHttpClientFactory = httpClientFactory;
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
			       ? BackendHttpClientConfiguration.CreateAdHocClient(draft)
			       : CreateClient(backend.Name);
	}
}
