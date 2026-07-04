// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Core;

/// <summary>
/// Registers the routing core: the model router, the startup catalog builder, the effective-config
/// exporter, and the hosted service that runs discovery before the proxy serves traffic. The single
/// <see cref="ModelRouter"/> instance is exposed through both <see cref="IModelRouter"/> (read side,
/// consumed by endpoints) and <see cref="IModelCatalogInitializer"/> (write side, consumed once by
/// discovery), so reads and the one-time population share the same volatile snapshot rather than two
/// divergent objects.
/// </summary>
/// <remarks>
/// The backend-discovery stack (the clock, the <see cref="IProviderResolver"/>, the provider adapters,
/// and <see cref="IBackendModelDiscovery"/>) is <b>not</b> registered here. It is the host-agnostic block
/// both hosts of the cascade compose
/// (<see cref="BackendDiscoveryServiceCollectionExtensions.AddBackendDiscovery"/>), so it must be added to
/// the same container <b>before the host is built</b>. The catalog builder and the discovery hosted service
/// registered here resolve their resolver and clock only at run time, so registration order between the two
/// blocks does not matter; only their presence in the final container does.
/// </remarks>
static class CoreServiceCollectionExtensions
{
	/// <summary>
	/// Adds the routing core and the startup discovery pipeline to the container. Requires the shared
	/// backend-discovery block (<see cref="BackendDiscoveryServiceCollectionExtensions.AddBackendDiscovery"/>)
	/// to be composed on the same container so the catalog builder and discovery service can resolve their
	/// <see cref="IProviderResolver"/> and clock.
	/// </summary>
	/// <param name="services">The service collection to register the core services with.</param>
	/// <returns>The same <paramref name="services"/> instance, to support call chaining.</returns>
	/// <exception cref="ArgumentNullException">
	/// <paramref name="services"/> is <see langword="null"/>.
	/// </exception>
	public static IServiceCollection AddProxyCore(this IServiceCollection services)
	{
		ArgumentNullException.ThrowIfNull(services);

		// One router instance behind two intent-revealing interfaces: endpoints resolve through the
		// read side while discovery publishes through the write side.
		services.AddSingleton<ModelRouter>();
		services.AddSingleton<IModelRouter>(static sp => sp.GetRequiredService<ModelRouter>());
		services.AddSingleton<IModelCatalogInitializer>(static sp => sp.GetRequiredService<ModelRouter>());

		// The catalog builder produces the resolved catalog that the hosted service publishes via
		// IModelCatalogInitializer; it is used only once at startup, so it doesn't need to be
		// scoped or transient.
		services.AddSingleton<ModelCatalogBuilder>();

		// The hosted service that runs discovery before the proxy serves traffic.
		// It needs to be a hosted service to ensure it starts automatically when the application starts.
		services.AddHostedService<ModelDiscoveryHostedService>();

		return services;
	}
}
