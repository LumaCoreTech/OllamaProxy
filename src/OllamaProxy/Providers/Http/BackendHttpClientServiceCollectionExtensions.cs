// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Microsoft.Extensions.Http.Resilience;

using OllamaProxy.Configuration;

namespace OllamaProxy.Providers.Http;

/// <summary>
/// Registers one resilient, pre-authenticated <see cref="HttpClient"/> per configured backend, plus
/// the <see cref="IBackendHttpClientProvider"/> that resolves them. Each client is configured with
/// the backend's base address, a bearer <c>Authorization</c> header, an infinite client-level timeout
/// (long streaming responses are bounded by the resilience attempt timeout and the request's
/// cancellation token, not by an abrupt client cutoff), and the standard resilience pipeline.
/// </summary>
static class BackendHttpClientServiceCollectionExtensions
{
	/// <summary>
	/// The per-attempt timeout. Because streaming reads use response-headers completion, this bounds
	/// only the time to first response headers, never the duration of an in-flight token stream.
	/// </summary>
	private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(100);

	/// <summary>
	/// The overall timeout across retries; must be at least <see cref="AttemptTimeout"/>.
	/// </summary>
	private static readonly TimeSpan TotalRequestTimeout = TimeSpan.FromSeconds(300);

	/// <summary>
	/// The circuit-breaker sampling window; the resilience handler requires it to be at least twice
	/// <see cref="AttemptTimeout"/>.
	/// </summary>
	private static readonly TimeSpan CircuitBreakerSamplingDuration = TimeSpan.FromSeconds(300);

	/// <summary>
	/// Adds a configured typed client for every backend declared under
	/// <see cref="ProxyOptions.SectionName"/> and registers the backend client provider.
	/// </summary>
	/// <param name="services">The service collection to register the clients with.</param>
	/// <param name="configuration">The application configuration carrying the backend definitions.</param>
	/// <returns>The same <paramref name="services"/> instance, to support call chaining.</returns>
	public static IServiceCollection AddBackendHttpClients(
		this IServiceCollection services,
		IConfiguration          configuration)
	{
		ArgumentNullException.ThrowIfNull(services);
		ArgumentNullException.ThrowIfNull(configuration);

		services.AddSingleton<IBackendHttpClientProvider, BackendHttpClientProvider>();

		// Bind just enough of the options graph to enumerate backends; full validation is performed
		// separately by the options pipeline at startup.
		ProxyOptions options = new();
		configuration.GetSection(ProxyOptions.SectionName).Bind(options);

		foreach ((string backendName, BackendOptions backend) in options.Backends)
		{
			services
				.AddHttpClient(
					BackendHttpClientNames.ForBackend(backendName),
					client => BackendHttpClientConfiguration.Configure(client, backend))
				.ConfigurePrimaryHttpMessageHandler(() => BackendHttpHandlerFactory.Create(options.Connection))
				.AddStandardResilienceHandler(ConfigureResilience);
		}

		return services;
	}

	/// <summary>
	/// Tunes the standard resilience pipeline so its timeouts accommodate slow first-token latency on
	/// large models while keeping the inter-dependent timeout/circuit-breaker constraints valid.
	/// </summary>
	/// <param name="options">The resilience options to adjust in place.</param>
	private static void ConfigureResilience(HttpStandardResilienceOptions options)
	{
		options.AttemptTimeout.Timeout = AttemptTimeout;
		options.TotalRequestTimeout.Timeout = TotalRequestTimeout;
		options.CircuitBreaker.SamplingDuration = CircuitBreakerSamplingDuration;
	}
}
