// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using OllamaProxy.Admin;
using OllamaProxy.Admin.Catalog;
using OllamaProxy.Admin.Config;
using OllamaProxy.Admin.Fetch;
using OllamaProxy.Configuration;
using OllamaProxy.Core;
using OllamaProxy.Diagnostics;
using OllamaProxy.Hosting;
using OllamaProxy.Hosting.Cascade;
using OllamaProxy.Providers;
using OllamaProxy.Providers.Abstractions;
using OllamaProxy.Providers.Http;
using OllamaProxy.Providers.OpenAiProtocol;

namespace OllamaProxy.Tests.Composition;

/// <summary>
/// Smoke tests for the service-registration extension methods that compose the proxy's runtime and chassis service
/// graph. These tests build a validating provider so missing constructor dependencies fail at registration-test time.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ServiceCollectionExtensionsTests
{
	private const string ProxyListenUrl         = "http://localhost:11434";
	private const string ProxySectionKey        = ProxyOptions.SectionName + ":";
	private const string ListenUrlKey           = ProxySectionKey + nameof(ProxyOptions.ListenUrl);
	private const string OpenAiProviderType     = "openai";
	private const string OpenRouterProviderType = "openrouter";
	private const string VeniceProviderType     = "venice";
	private const string VllmProviderType       = "vllm";
	private const string TestDataDirectory      = "D:\\OllamaProxyTestData";
	private const string TestConfigPath         = "D:\\OllamaProxyTestData\\appsettings.json";

	private static readonly string[] ExpectedProviderTypes =
	[
		OpenAiProviderType,
		OpenRouterProviderType,
		VeniceProviderType,
		VllmProviderType
	];

	#region AddProxyOptions()

	/// <summary>
	/// Verifies that <see cref="ConfigurationServiceCollectionExtensions.AddProxyOptions"/> binds the proxy options
	/// section from the registered configuration source.
	/// </summary>
	[Fact]
	public void AddProxyOptions_WhenOptionsResolved_BindsProxyOptionsSection()
	{
		// Arrange
		ServiceCollection services = [];
		services.AddSingleton<IConfiguration>(CreateConfiguration());

		// Act
		IServiceCollection returned = services.AddProxyOptions();
		using ServiceProvider provider = BuildValidatedProvider(services);
		ProxyOptions options = provider.GetRequiredService<IOptions<ProxyOptions>>().Value;

		// Assert
		Assert.Same(services, returned);
		Assert.Equal(ProxyListenUrl, options.ListenUrl);
	}

	#endregion

	#region AddProviders()

	/// <summary>
	/// Verifies that <see cref="ProviderServiceCollectionExtensions.AddProviders"/> registers all provider adapters,
	/// descriptors, the capability prober, the reasoning cache, and the aggregating provider catalog.
	/// </summary>
	[Fact]
	public void AddProviders_WhenProviderServicesResolved_ComposesProviderCatalogAndAdapters()
	{
		// Arrange
		ServiceCollection services = CreateCommonServices();
		AddProviderRuntimePrerequisites(services);

		// Act
		IServiceCollection returned = services.AddProviders();
		using ServiceProvider provider = BuildValidatedProvider(services);
		string[] adapterProviderTypes = provider.GetServices<IProviderAdapter>()
			.Select(adapter => adapter.ProviderType)
			.ToArray();
		string[] descriptorProviderTypes = provider.GetServices<ProviderDescriptor>()
			.Select(descriptor => descriptor.ProviderType)
			.ToArray();
		var catalog = provider.GetRequiredService<IProviderCatalog>();

		// Assert
		Assert.Same(services, returned);
		Assert.Equal(ExpectedProviderTypes, adapterProviderTypes);
		Assert.Equal(ExpectedProviderTypes, descriptorProviderTypes);
		Assert.Equal(ExpectedProviderTypes, catalog.Providers.Select(descriptor => descriptor.ProviderType).ToArray());
		Assert.NotNull(provider.GetService<ICapabilityProber>());
		Assert.NotNull(provider.GetService<IReasoningDetailsCache>());
	}

	#endregion

	#region AddProviderTypeValidation()

	/// <summary>
	/// Verifies that <see cref="ProviderServiceCollectionExtensions.AddProviderTypeValidation"/> registers the provider
	/// type validator exactly once, even when called repeatedly.
	/// </summary>
	[Fact]
	public void AddProviderTypeValidation_WhenCalledTwice_RegistersSingleValidator()
	{
		// Arrange
		ServiceCollection services = CreateCommonServices();
		services.AddBackendDiscovery();

		// Act
		IServiceCollection returned = services
			.AddProviderTypeValidation()
			.AddProviderTypeValidation();
		using ServiceProvider provider = BuildValidatedProvider(services);
		IValidateOptions<ProxyOptions>[] validators = provider.GetServices<IValidateOptions<ProxyOptions>>()
			.Where(validator => validator is ProviderTypeValidateOptions)
			.ToArray();

		// Assert
		Assert.Same(services, returned);
		Assert.Single(validators);
	}

	#endregion

	#region AddBackendDiscovery()

	/// <summary>
	/// Verifies that <see cref="BackendDiscoveryServiceCollectionExtensions.AddBackendDiscovery"/> composes the shared
	/// provider discovery stack and preserves a pre-registered clock.
	/// </summary>
	[Fact]
	public void AddBackendDiscovery_WhenDiscoveryServicesResolved_ComposesSharedDiscoveryStack()
	{
		// Arrange
		ServiceCollection services = CreateCommonServices();
		TimeProvider timeProvider = TimeProvider.System;
		services.AddSingleton(timeProvider);

		// Act
		IServiceCollection returned = services.AddBackendDiscovery();
		using ServiceProvider provider = BuildValidatedProvider(services);

		// Assert
		Assert.Same(services, returned);
		Assert.Same(timeProvider, provider.GetRequiredService<TimeProvider>());
		Assert.NotNull(provider.GetService<IBackendHttpClientProvider>());
		Assert.NotNull(provider.GetService<IProviderResolver>());
		Assert.NotNull(provider.GetService<IBackendModelDiscovery>());
		Assert.NotNull(provider.GetService<IProviderCatalog>());
	}

	#endregion

	#region AddProxyCore()

	/// <summary>
	/// Verifies that <see cref="CoreServiceCollectionExtensions.AddProxyCore"/> registers one router instance behind
	/// both routing interfaces plus the hosted discovery service.
	/// </summary>
	[Fact]
	public void AddProxyCore_WhenCoreServicesResolved_ComposesRouterAndDiscoveryHost()
	{
		// Arrange
		ServiceCollection services = CreateCommonServices();
		services.AddBackendDiscovery();

		// Act
		IServiceCollection returned = services.AddProxyCore();
		using ServiceProvider provider = BuildValidatedProvider(services);
		var router = provider.GetRequiredService<IModelRouter>();
		var initializer = provider.GetRequiredService<IModelCatalogInitializer>();
		IHostedService hostedService = Assert.Single(
			provider.GetServices<IHostedService>(),
			service => service is ModelDiscoveryHostedService);

		// Assert
		Assert.Same(services, returned);
		Assert.Same(router, initializer);
		Assert.IsType<ModelDiscoveryHostedService>(hostedService);
		Assert.NotNull(provider.GetService<ModelCatalogBuilder>());
	}

	#endregion

	#region AddRequestTracing()

	/// <summary>
	/// Verifies that <see cref="DiagnosticsServiceCollectionExtensions.AddRequestTracing"/> composes the request-trace
	/// accessor, sink, and middleware while preserving a pre-registered clock.
	/// </summary>
	[Fact]
	public void AddRequestTracing_WhenTracingServicesResolved_ComposesTracingSubsystem()
	{
		// Arrange
		ServiceCollection services = CreateCommonServices();
		TimeProvider timeProvider = TimeProvider.System;
		services.AddSingleton(timeProvider);

		// Act
		IServiceCollection returned = services.AddRequestTracing();
		using ServiceProvider provider = BuildValidatedProvider(services);

		// Assert
		Assert.Same(services, returned);
		Assert.Same(timeProvider, provider.GetRequiredService<TimeProvider>());
		Assert.NotNull(provider.GetService<IRequestTraceAccessor>());
		Assert.NotNull(provider.GetService<IRequestTraceSink>());
		Assert.NotNull(provider.GetService<RequestTracingMiddleware>());
	}

	#endregion

	#region AddAdminModelServices()

	/// <summary>
	/// Verifies that <see cref="AdminServiceCollectionExtensions.AddAdminModelServices"/> composes the chassis-side
	/// admin services, fetcher, live catalog view, and configuration persistence path.
	/// </summary>
	[Fact]
	public void AddAdminModelServices_WhenAdminServicesResolved_ComposesAdminSurface()
	{
		// Arrange
		ServiceCollection services = CreateCommonServices();
		services.Configure<AdminOptions>(static _ => { });
		services.AddSingleton<IWritableProxyConfigFile>(new StubWritableProxyConfigFile());
		services.AddSingleton<IProxyHostSupervisor>(new StubProxyHostSupervisor());
		IConfiguration configuration = CreateConfiguration();

		// Act
		IServiceCollection returned = services.AddAdminModelServices(configuration);
		using ServiceProvider provider = BuildValidatedProvider(services);

		// Assert
		Assert.Same(services, returned);
		Assert.NotNull(provider.GetService<IBackendModelFetcher>());
		Assert.NotNull(provider.GetService<IAdminModelService>());
		Assert.NotNull(provider.GetService<IAdminCatalogService>());
		Assert.NotNull(provider.GetService<IProxyConfigWriter>());
		Assert.NotNull(provider.GetService<IProxyConfigApplier>());
	}

	#endregion

	/// <summary>
	/// Creates the common service registrations needed by the composition tests before the extension under test runs.
	/// </summary>
	/// <returns>
	/// A service collection seeded with options, configuration, logging, and data-directory services.
	/// </returns>
	private static ServiceCollection CreateCommonServices()
	{
		ServiceCollection services = [];
		services.AddLogging();
		services.AddSingleton<IConfiguration>(CreateConfiguration());
		services.AddOptions<ProxyOptions>().Configure(static options => options.ListenUrl = ProxyListenUrl);
		services.AddSingleton<IDataDirectory>(new DataDirectory(TestDataDirectory));
		return services;
	}

	/// <summary>
	/// Adds the host-level prerequisites that the lower-level provider registration block deliberately does not own.
	/// </summary>
	/// <param name="services">
	/// The service collection to seed before calling <see cref="ProviderServiceCollectionExtensions.AddProviders"/>.
	/// </param>
	private static void AddProviderRuntimePrerequisites(IServiceCollection services)
	{
		// AddProviders() is normally called by AddBackendDiscovery(), which supplies these cross-cutting services.
		// The direct AddProviders() smoke test seeds them explicitly so it verifies this layer without widening its
		// ownership to the full discovery stack.
		services.AddSingleton(TimeProvider.System);
		services.AddSingleton<IRequestTraceAccessor, RequestTraceAccessor>();
		services.AddSingleton<IBackendHttpClientProvider, StubBackendHttpClientProvider>();
	}

	/// <summary>
	/// Builds a configuration root containing the proxy section values used by registration smoke tests.
	/// </summary>
	/// <returns>
	/// A configuration root with the proxy listen URL configured.
	/// </returns>
	private static IConfiguration CreateConfiguration() => new ConfigurationBuilder()
		.AddInMemoryCollection(
			new Dictionary<string, string?>
			{
				[ListenUrlKey] = ProxyListenUrl
			})
		.Build();

	/// <summary>
	/// Builds a validating service provider so missing constructor dependencies surface during the smoke test.
	/// </summary>
	/// <param name="services">
	/// The service collection to build.
	/// </param>
	/// <returns>
	/// A service provider with build-time and scope validation enabled.
	/// </returns>
	private static ServiceProvider BuildValidatedProvider(IServiceCollection services) => services.BuildServiceProvider(
		new ServiceProviderOptions
		{
			ValidateOnBuild = true,
			ValidateScopes = true
		});

	/// <summary>
	/// Test double for the writable operator configuration file required by the admin registration graph.
	/// </summary>
	private sealed class StubWritableProxyConfigFile : IWritableProxyConfigFile
	{
		/// <inheritdoc/>
		public string Path => TestConfigPath;

		/// <inheritdoc/>
		public Task<string?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);

		/// <inheritdoc/>
		public Task WriteAsync(string content, CancellationToken cancellationToken)
		{
			ArgumentNullException.ThrowIfNull(content);
			return Task.CompletedTask;
		}

		/// <inheritdoc/>
		public Task DeleteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
	}

	/// <summary>
	/// Test double for the backend HTTP client provider required when the provider block is tested directly.
	/// </summary>
	private sealed class StubBackendHttpClientProvider : IBackendHttpClientProvider
	{
		/// <inheritdoc/>
		public HttpClient CreateClient(string backendName)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(backendName);
			return new HttpClient();
		}

		/// <inheritdoc/>
		public HttpClient CreateClient(BackendContext backend)
		{
			ArgumentNullException.ThrowIfNull(backend);
			return new HttpClient();
		}
	}

	/// <summary>
	/// Test double for the inner-host supervisor required by the admin registration graph.
	/// </summary>
	private sealed class StubProxyHostSupervisor : IProxyHostSupervisor
	{
		/// <inheritdoc/>
		public bool IsInnerHostActive => false;

		/// <inheritdoc/>
		public IReadOnlyList<RegisteredModel>? GetLiveModels() => null;

		/// <inheritdoc/>
		public Task<RecycleResult> RecycleAsync(CancellationToken cancellationToken) =>
			Task.FromResult(RecycleResult.Succeeded);

		/// <inheritdoc/>
		public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

		/// <inheritdoc/>
		public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
	}
}
