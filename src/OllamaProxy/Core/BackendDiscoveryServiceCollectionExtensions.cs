// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Microsoft.Extensions.DependencyInjection.Extensions;

using OllamaProxy.Diagnostics;
using OllamaProxy.Providers;
using OllamaProxy.Providers.Http;

namespace OllamaProxy.Core;

/// <summary>
/// Registers the host-agnostic backend-discovery stack: the provider adapters and their capability prober, the
/// backend HTTP client provider, the backend resolver, and the shared <see cref="IBackendModelDiscovery"/>
/// orchestration. This is the single block both hosts of the cascade compose on, so the runtime catalog (inner
/// proxy host) and the admin fetch surface (outer chassis) discover models through the exact same path and
/// cannot drift in how they name, size, and probe them.
/// </summary>
/// <remarks>
///     <para>
///     The block deliberately does <b>not</b> bind <see cref="Configuration.ProxyOptions"/>, which is host-specific:
///     the inner proxy host binds it with fail-fast validation
///     (<see cref="Configuration.ConfigurationServiceCollectionExtensions.AddProxyOptions"/>), while the chassis
///     binds it tolerantly against its own proxy-configuration snapshot so a broken proxy config still lets the
///     admin surface load. Call this block <b>after</b> registering the options graph
///     (<c>AddOptions&lt;ProxyOptions&gt;().BindConfiguration(...)</c>): the provider adapters require
///     <c>IOptions&lt;ProxyOptions&gt;</c> and the prober requires <c>IOptionsMonitor&lt;ProxyOptions&gt;</c>, both
///     of which that registration supplies.
///     </para>
///     <para>
///     Every registration is idempotent against a host that already provides the same service (the inner host
///     registers its resilient named clients and request tracing separately), so composing the block on top of the
///     inner host's existing wiring is safe. It must, however, be invoked at most <b>once per container</b>:
///     <see cref="ProviderServiceCollectionExtensions.AddProviders"/> registers one
///     <see cref="Providers.Abstractions.IProviderAdapter"/> per provider type, and a duplicate registration would
///     make adapter selection ambiguous.
///     </para>
/// </remarks>
static class BackendDiscoveryServiceCollectionExtensions
{
	/// <summary>
	/// Adds the shared backend-discovery services to the container.
	/// </summary>
	/// <param name="services">The service collection to register the discovery stack with.</param>
	/// <returns>The same <paramref name="services"/> instance, to support call chaining.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
	public static IServiceCollection AddBackendDiscovery(this IServiceCollection services)
	{
		ArgumentNullException.ThrowIfNull(services);

		// The clock injected wherever durations and timestamps are produced (providers, prober). TryAdd so a
		// host (or test) that already registered a deterministic substitute keeps it.
		services.TryAddSingleton(TimeProvider.System);

		// The provider adapters record provenance through the ambient request-trace accessor. On a host with no
		// tracing middleware (the chassis), its Current scope is the null object, so the adapters construct and
		// run safely without a request pipeline. TryAdd so the inner host's request-tracing registration, which
		// needs the same AsyncLocal-backed singleton for its middleware, shares this one instance.
		services.TryAddSingleton<IRequestTraceAccessor, RequestTraceAccessor>();

		// The HttpClient factory infrastructure. The committed-backend path reuses the resilient named clients
		// registered separately (AddBackendHttpClients); the draft/admin path builds a one-shot ad-hoc client
		// from inline options and never touches the factory. BackendHttpClientProvider still takes an
		// IHttpClientFactory in its constructor, so the infrastructure must be present either way.
		services.AddHttpClient();

		// Resolves a BackendContext to its HttpClient: the pre-configured named client for a committed backend,
		// or an ad-hoc client built from inline options for a draft one.
		services.TryAddSingleton<IBackendHttpClientProvider, BackendHttpClientProvider>();

		// The provider adapters plus the capability prober they fall back to during discovery. This registers
		// one IProviderAdapter per provider type, so the whole block must be composed at most once per container.
		services.AddProviders();

		// Pairs a backend with the adapter that services it: by committed name, or by a draft's provider type.
		services.TryAddSingleton<IProviderResolver, ProviderResolver>();

		// The discover-then-resolve orchestration. BackendModelDiscovery is a stateless, pure pipeline, so its
		// two consumers run identical logic without sharing an instance: the catalog builder owns a private one
		// by design (see ModelCatalogBuilder), while the admin fetch resolves this registration from the
		// container so the chassis reuses the exact same pipeline.
		services.TryAddSingleton<IBackendModelDiscovery, BackendModelDiscovery>();

		return services;
	}
}
