// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;

namespace OllamaProxy.Hosting.Cascade;

/// <summary>
/// A non-binding <see cref="IServer"/> used only for dry-run validation of a candidate inner proxy host. It
/// satisfies the dependency the hosting layer requires and lets a candidate host fully start (exercising the
/// dependency-injection container, the options validation, and the startup model discovery) without opening a
/// socket, so it never contends for the proxy port (<c>:11434</c>) the live host already holds. Once a dry-run
/// has confirmed the configuration is sound, the real host is built with Kestrel and bound for real.
/// </summary>
sealed class NoopServer : IServer
{
	/// <summary>
	/// Initializes a new instance of the <see cref="NoopServer"/> class, seeding an empty server-addresses
	/// feature so the hosting layer's post-start address read (used to log the listening endpoints) finds the
	/// feature it expects rather than dereferencing a missing one.
	/// </summary>
	public NoopServer()
	{
		Features.Set<IServerAddressesFeature>(new ServerAddressesFeature());
	}

	/// <inheritdoc/>
	public IFeatureCollection Features { get; } = new FeatureCollection();

	/// <inheritdoc/>
	public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
		where TContext : notnull =>
		// Intentionally a no-op: starting must succeed (so the dry-run can validate the rest of the host) while
		// opening no socket on the proxy port the live host already owns.
		Task.CompletedTask;

	/// <inheritdoc/>
	public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	/// <inheritdoc/>
	public void Dispose()
	{
		// Nothing to release: the server never opened a socket or allocated any unmanaged resource.
	}
}
