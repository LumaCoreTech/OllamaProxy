// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using OllamaProxy.Configuration;
using OllamaProxy.Providers.Abstractions;
using OllamaProxy.Providers.OpenAi;
using OllamaProxy.Providers.OpenAiProtocol;
using OllamaProxy.Providers.OpenRouter;
using OllamaProxy.Providers.Venice;
using OllamaProxy.Providers.Vllm;

namespace OllamaProxy.Providers;

/// <summary>
/// Registers the provider adapters that translate the Ollama surface to a concrete upstream API. Each
/// adapter is registered against <see cref="IProviderAdapter"/> so the resolver can select one by its
/// <see cref="IProviderAdapter.ProviderType"/>; adapters are stateless and shared as singletons. The
/// generic <see cref="OpenAiProvider"/> serves the official OpenAI API and any plain OpenAI-compatible
/// backend, while the Venice, vLLM, and OpenRouter adapters specialize the shared base for their own
/// reasoning dialects. Additional providers register here without touching the resolver or endpoints.
/// <para>
/// Each adapter is added through <see cref="AddProvider{TProvider}"/>, which registers the adapter <em>and</em>
/// its <see cref="ProviderDescriptor"/> together, and the aggregating <see cref="IProviderCatalog"/> is
/// registered once so the descriptors drive configuration validation, the admin picker, and the mode/URL
/// defaults from a single data-driven source.
/// </para>
/// </summary>
static class ProviderServiceCollectionExtensions
{
	/// <summary>
	/// Adds the available provider adapters and the aggregating provider catalog to the container.
	/// </summary>
	/// <param name="services">The service collection to register the adapters with.</param>
	/// <returns>The same <paramref name="services"/> instance, to support call chaining.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
	public static IServiceCollection AddProviders(this IServiceCollection services)
	{
		ArgumentNullException.ThrowIfNull(services);

		// The capability prober is an implementation detail of the OpenAI-compatible providers: they use it
		// to fall back to runtime probing when a model's discovery metadata carries no capability signal. It
		// is stateless and safe to share, so it is registered as a singleton; it resolves its per-backend
		// probing settings and clock from the container.
		services.AddSingleton<ICapabilityProber, OpenAiCapabilityProber>();

		// The reasoning-details cache carries a backend's opaque reasoning_details blob across a multi-turn
		// tool-call conversation the Ollama wire format cannot itself convey. It holds shared mutable state
		// (the cache), so it is a singleton; the providers that round-trip the blob (Venice, OpenRouter)
		// consult it while the strict OpenAI dialect and vLLM leave it untouched via their provider gate.
		services.AddSingleton<IReasoningDetailsCache, ReasoningDetailsCache>();

		// Each provider contributes both its adapter (behavior) and its descriptor (cheap, options-free identity
		// and defaults). Adding a new provider is one more AddProvider<T> line here; nothing else in the proxy
		// needs to change for validation, the admin picker, or the defaults to pick it up.
		services
			.AddProvider<OpenAiProvider>()
			.AddProvider<OpenRouterProvider>()
			.AddProvider<VeniceProvider>()
			.AddProvider<VllmProvider>();

		// The aggregating catalog over the registered descriptors. It reads only the descriptors (never the
		// adapters), so it is safe to consult during options validation without re-entering the options graph the
		// adapters depend on.
		services.AddSingleton<IProviderCatalog, ProviderCatalog>();

		return services;
	}

	/// <summary>
	/// Registers a single provider: its adapter against <see cref="IProviderAdapter"/> (selected by the resolver
	/// at routing time) and its static <see cref="IProviderDescriptorSource.Descriptor"/> as a standalone
	/// <see cref="ProviderDescriptor"/> the catalog aggregates. Both are singletons: the adapter is stateless and
	/// the descriptor is immutable. The descriptor is read <em>statically</em> through the
	/// <see cref="IProviderDescriptorSource"/> constraint, so registering it never constructs the adapter and
	/// therefore never touches the options graph the adapter depends on.
	/// </summary>
	/// <typeparam name="TProvider">
	/// The provider adapter type to register; it both services requests and publishes its
	/// descriptor.
	/// </typeparam>
	/// <param name="services">The service collection to register the provider with.</param>
	/// <returns>The same <paramref name="services"/> instance, to support call chaining.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
	public static IServiceCollection AddProvider<TProvider>(this IServiceCollection services)
		where TProvider : class, IProviderAdapter, IProviderDescriptorSource
	{
		ArgumentNullException.ThrowIfNull(services);

		services.AddSingleton<IProviderAdapter, TProvider>();
		services.AddSingleton(TProvider.Descriptor);

		return services;
	}

	/// <summary>
	/// Adds the startup provider-type validation: every configured backend must name a registered provider type,
	/// and the neutral <see cref="BackendOptions.DefaultProviderType"/> must itself resolve to one. This is
	/// registered <em>only</em> on the inner proxy host, whose <see cref="ProxyOptions"/> binding is fail-fast
	/// (<c>ValidateOnStart</c>); the chassis binds the same options tolerantly so a broken proxy configuration
	/// still lets the admin surface load to fix it, and must not run this rule. It depends on
	/// <see cref="IProviderCatalog"/>, so call it after <see cref="AddProviders"/> (or the shared discovery block
	/// that composes it).
	/// </summary>
	/// <param name="services">The service collection to register the validator with.</param>
	/// <returns>The same <paramref name="services"/> instance, to support call chaining.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
	public static IServiceCollection AddProviderTypeValidation(this IServiceCollection services)
	{
		ArgumentNullException.ThrowIfNull(services);

		// TryAddEnumerable keyed by the implementation type so a repeated call does not register the validator
		// twice (which would surface every failure message in duplicate).
		services.TryAddEnumerable(
			ServiceDescriptor.Singleton<IValidateOptions<ProxyOptions>, ProviderTypeValidateOptions>());

		return services;
	}
}
