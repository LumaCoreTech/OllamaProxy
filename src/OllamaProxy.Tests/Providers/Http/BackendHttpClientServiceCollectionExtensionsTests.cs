// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Net.Http.Headers;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using OllamaProxy.Providers.Http;

namespace OllamaProxy.Tests.Providers.Http;

/// <summary>
/// Tests for <see cref="BackendHttpClientServiceCollectionExtensions.AddBackendHttpClients"/>. They verify the
/// guard clauses and that one named, pre-authenticated client is registered per configured backend: the client
/// resolved through <see cref="IHttpClientFactory"/> under <see cref="BackendHttpClientNames.ForBackend"/>
/// carries the backend's base address and bearer token exactly as <see cref="BackendHttpClientConfiguration"/>
/// applies them, and the <see cref="IBackendHttpClientProvider"/> resolver is registered as a singleton.
/// </summary>
[Trait("Category", "Unit")]
public sealed class BackendHttpClientServiceCollectionExtensionsTests
{
	private const string BackendName = "primary";
	private const string BaseUrl     = "https://api.example.com/v1";
	private const string ApiKey      = "secret-key-value";

	/// <summary>
	/// Builds a configuration root exposing a single backend under the <c>OllamaProxy</c> section, matching the
	/// shape <see cref="BackendHttpClientServiceCollectionExtensions.AddBackendHttpClients"/> binds against.
	/// </summary>
	/// <returns>A configuration carrying one backend definition.</returns>
	private static IConfiguration BuildSingleBackendConfiguration() => new ConfigurationBuilder()
		.AddInMemoryCollection(
			new Dictionary<string, string?>
			{
				[$"OllamaProxy:Backends:{BackendName}:BaseUrl"] = BaseUrl,
				[$"OllamaProxy:Backends:{BackendName}:ApiKey"] = ApiKey
			})
		.Build();

	/// <summary>
	/// Verifies that <see cref="BackendHttpClientServiceCollectionExtensions.AddBackendHttpClients"/> registers the
	/// <see cref="IBackendHttpClientProvider"/> resolver as a singleton so provider adapters share one client source.
	/// </summary>
	[Fact]
	public void AddBackendHttpClients_RegistersBackendClientProviderAsSingleton()
	{
		// Arrange
		ServiceCollection services = [];

		// Act
		services.AddBackendHttpClients(BuildSingleBackendConfiguration());

		// Assert
		using ServiceProvider provider = services.BuildServiceProvider();
		var first = provider.GetRequiredService<IBackendHttpClientProvider>();
		var second = provider.GetRequiredService<IBackendHttpClientProvider>();
		Assert.Same(first, second);
	}

	/// <summary>
	/// Verifies that a client resolved under the backend's factory name carries the base address and bearer
	/// authentication configured for that backend, proving the per-backend registration wired the client through
	/// <see cref="BackendHttpClientConfiguration"/>.
	/// </summary>
	[Fact]
	public void AddBackendHttpClients_WhenBackendConfigured_RegistersConfiguredNamedClient()
	{
		// Arrange
		ServiceCollection services = [];
		services.AddBackendHttpClients(BuildSingleBackendConfiguration());

		// Act
		using ServiceProvider provider = services.BuildServiceProvider();
		var factory = provider.GetRequiredService<IHttpClientFactory>();
		using HttpClient client = factory.CreateClient(BackendHttpClientNames.ForBackend(BackendName));

		// Assert
		Assert.Equal(new Uri(BaseUrl + "/"), client.BaseAddress);
		AuthenticationHeaderValue? auth = client.DefaultRequestHeaders.Authorization;
		Assert.NotNull(auth);
		Assert.Equal("Bearer", auth.Scheme);
		Assert.Equal(ApiKey, auth.Parameter);
	}

	/// <summary>
	/// Verifies that an empty backend catalog is valid: the provider resolver is still registered so the proxy
	/// can start with no backends after a fresh install, but no per-backend named client is wired (no
	/// <see cref="IHttpClientFactory"/> registration is added because no backend was enumerated).
	/// </summary>
	[Fact]
	public void AddBackendHttpClients_WhenNoBackendsConfigured_RegistersProviderButNoNamedClients()
	{
		// Arrange
		ServiceCollection services = [];
		IConfiguration configuration = new ConfigurationBuilder().Build();

		// Act
		services.AddBackendHttpClients(configuration);

		// Assert: the provider resolver is registered, but nothing pulled in the HTTP client factory, proving no
		// per-backend AddHttpClient ran.
		Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IBackendHttpClientProvider));
		Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IHttpClientFactory));
	}

	/// <summary>
	/// Verifies that <see cref="BackendHttpClientServiceCollectionExtensions.AddBackendHttpClients"/> rejects a
	/// <see langword="null"/> service collection.
	/// </summary>
	[Fact]
	public void AddBackendHttpClients_WhenServicesIsNull_ThrowsArgumentNullException()
	{
		// Arrange
		IConfiguration configuration = new ConfigurationBuilder().Build();

		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() =>
			BackendHttpClientServiceCollectionExtensions.AddBackendHttpClients(null!, configuration));
		Assert.Equal("services", exception.ParamName);
	}

	/// <summary>
	/// Verifies that <see cref="BackendHttpClientServiceCollectionExtensions.AddBackendHttpClients"/> rejects a
	/// <see langword="null"/> configuration.
	/// </summary>
	[Fact]
	public void AddBackendHttpClients_WhenConfigurationIsNull_ThrowsArgumentNullException()
	{
		// Arrange
		ServiceCollection services = [];

		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() => services.AddBackendHttpClients(null!));
		Assert.Equal("configuration", exception.ParamName);
	}
}
