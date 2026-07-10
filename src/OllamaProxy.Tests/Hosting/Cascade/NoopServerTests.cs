// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;

using OllamaProxy.Hosting.Cascade;

namespace OllamaProxy.Tests.Hosting.Cascade;

/// <summary>
/// Tests for <see cref="NoopServer"/>, the non-binding <see cref="IServer"/> used only for dry-run validation
/// of a candidate inner proxy host. Its contract is deliberately minimal: it must expose a server-addresses
/// feature (so the hosting layer's post-start address read finds it), and its start/stop/dispose must all be
/// harmless no-ops so a candidate host can fully start without ever opening a socket on the live proxy port.
/// </summary>
[Trait("Category", "Unit")]
public sealed class NoopServerTests
{
	/// <summary>
	/// Verifies that the constructor seeds an <see cref="IServerAddressesFeature"/> so the hosting layer's
	/// post-start address read finds the feature it expects rather than dereferencing a missing one.
	/// </summary>
	[Fact]
	public void Constructor_SeedsServerAddressesFeature()
	{
		// Act
		using NoopServer sut = new();

		// Assert
		var feature = sut.Features.Get<IServerAddressesFeature>();
		Assert.NotNull(feature);
		Assert.Empty(feature.Addresses);
	}

	/// <summary>
	/// Verifies that <see cref="NoopServer.StartAsync{TContext}"/> completes synchronously without opening a
	/// socket, so a dry-run candidate can start and validate the rest of the host.
	/// </summary>
	/// <returns>A task that completes when the assertion has run.</returns>
	[Fact]
	public async Task StartAsync_CompletesSynchronouslyWithoutError()
	{
		// Arrange
		using NoopServer sut = new();

		// Act
		Task started = sut.StartAsync(new StubHttpApplication(), CancellationToken.None);

		// Assert
		Assert.True(started.IsCompletedSuccessfully);
		await started;
	}

	/// <summary>
	/// Verifies that <see cref="NoopServer.StopAsync"/> completes synchronously without error, matching its
	/// no-socket lifetime.
	/// </summary>
	/// <returns>A task that completes when the assertion has run.</returns>
	[Fact]
	public async Task StopAsync_CompletesSynchronouslyWithoutError()
	{
		// Arrange
		using NoopServer sut = new();

		// Act
		Task stopped = sut.StopAsync(CancellationToken.None);

		// Assert
		Assert.True(stopped.IsCompletedSuccessfully);
		await stopped;
	}

	/// <summary>
	/// Verifies that <see cref="NoopServer.Dispose"/> is a harmless no-op: the server never opened a socket or
	/// allocated an unmanaged resource, so disposing must not throw and must be safe to call more than once.
	/// </summary>
	[Fact]
	public void Dispose_WhenCalledRepeatedly_DoesNotThrow()
	{
		// Arrange
		NoopServer sut = new();

		// Act + Assert
		sut.Dispose();
		sut.Dispose();
	}

	/// <summary>
	/// A minimal <see cref="IHttpApplication{TContext}"/> that satisfies the generic constraint of
	/// <see cref="NoopServer.StartAsync{TContext}"/>. Its members are never invoked because the server is a
	/// no-op, so they need no real behavior.
	/// </summary>
	private sealed class StubHttpApplication : IHttpApplication<object>
	{
		/// <inheritdoc/>
		public object CreateContext(IFeatureCollection contextFeatures) => new();

		/// <inheritdoc/>
		public Task ProcessRequestAsync(object context) => Task.CompletedTask;

		/// <inheritdoc/>
		public void DisposeContext(object context, Exception? exception)
		{
			// No context state is created, so there is nothing to dispose.
		}
	}
}
