// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using OllamaProxy.Hosting;
using OllamaProxy.Hosting.Cascade;
using OllamaProxy.Providers.Http;

namespace OllamaProxy.Tests.Hosting.Cascade;

/// <summary>
/// Integration tests for <see cref="ProxyHostFactory"/>, the production inner-host assembler. They build a real
/// host through the full composition pipeline — options, backend clients, discovery, routing core, tracing, and
/// the endpoint surface — but always with the dry-run <see cref="NoopServer"/> so no socket is opened and the
/// live proxy port is never contended. The foreground <see cref="FakeServiceEnvironment"/> keeps the content
/// root at the test host's directory rather than pinning it to <see cref="AppContext.BaseDirectory"/>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProxyHostFactoryTests
{
	/// <summary>
	/// Verifies that a dry-run build composes a startable host and swaps Kestrel for the non-binding
	/// <see cref="NoopServer"/>, the property the supervisor relies on to validate a candidate without binding
	/// the proxy port.
	/// </summary>
	[Fact]
	public void CreateProxyHost_WhenDryRun_RegistersNoopServer()
	{
		// Arrange
		ProxyHostFactory factory = new(FakeServiceEnvironment.Foreground);

		// Act
		using IHost host = factory.CreateProxyHost(useDryRunServer: true);

		// Assert
		var server = host.Services.GetRequiredService<IServer>();
		Assert.IsType<NoopServer>(server);
	}

	/// <summary>
	/// Verifies that the composed host wires the full proxy graph: the backend client provider is resolvable,
	/// proving options binding and the backend HTTP client registration ran during composition.
	/// </summary>
	[Fact]
	public void CreateProxyHost_WhenDryRun_ComposesBackendClientProvider()
	{
		// Arrange
		ProxyHostFactory factory = new(FakeServiceEnvironment.Foreground);

		// Act
		using IHost host = factory.CreateProxyHost(useDryRunServer: true);

		// Assert
		var provider = host.Services.GetRequiredService<IBackendHttpClientProvider>();
		Assert.NotNull(provider);
	}

	/// <summary>
	/// Verifies that a dry-run host can fully start and stop without error: startup exercises dependency
	/// injection, options validation, and startup discovery, confirming the validated candidate path the
	/// supervisor depends on.
	/// </summary>
	[Fact]
	public async Task CreateProxyHost_WhenDryRun_StartsAndStopsWithoutError()
	{
		// Arrange
		ProxyHostFactory factory = new(FakeServiceEnvironment.Foreground);
		using IHost host = factory.CreateProxyHost(useDryRunServer: true);

		// Act + Assert: neither call throws, confirming the validated candidate path the supervisor depends on.
		await host.StartAsync();
		await host.StopAsync();
	}

	/// <summary>
	/// Verifies that under the Service Control Manager the factory composes the inner host for the service
	/// hosting model: the content root is pinned to the executable's directory (<see cref="AppContext.BaseDirectory"/>,
	/// because a service starts in System32) and the writable data directory routes to the
	/// <c>%ProgramData%\OllamaProxy\data</c> subtree. Exercised with the dry-run <see cref="NoopServer"/> so the
	/// service-mode composition is validated without binding the proxy port. This is the branch the foreground
	/// tests never reached.
	/// </summary>
	[Fact]
	public void CreateProxyHost_WhenWindowsService_PinsContentRootAndRoutesDataToProgramData()
	{
		// Arrange
		ProxyHostFactory factory = new(FakeServiceEnvironment.Service);
		string expectedDataDirectory = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
			"OllamaProxy",
			"data");

		// Act
		using IHost host = factory.CreateProxyHost(useDryRunServer: true);

		// Assert: the service branch composed the SCM content-root pin and the ProgramData data subtree.
		var environment = host.Services.GetRequiredService<IHostEnvironment>();
		Assert.Equal(AppContext.BaseDirectory, environment.ContentRootPath);

		var dataDirectory = host.Services.GetRequiredService<IDataDirectory>();
		Assert.Equal(expectedDataDirectory, dataDirectory.BasePath);
	}

	/// <summary>
	/// Verifies that a service-mode dry-run host still starts and stops cleanly, so the SCM composition path the
	/// supervisor would run under a real service is validated end to end without a socket bind.
	/// </summary>
	[Fact]
	public async Task CreateProxyHost_WhenWindowsServiceDryRun_StartsAndStopsWithoutError()
	{
		// Arrange
		ProxyHostFactory factory = new(FakeServiceEnvironment.Service);
		using IHost host = factory.CreateProxyHost(useDryRunServer: true);

		// Act + Assert: the service-mode composition starts and stops without error.
		await host.StartAsync();
		await host.StopAsync();
	}
}
