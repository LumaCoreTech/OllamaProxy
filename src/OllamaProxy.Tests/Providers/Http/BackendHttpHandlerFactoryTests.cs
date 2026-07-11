// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Configuration;
using OllamaProxy.Providers.Http;

namespace OllamaProxy.Tests.Providers.Http;

/// <summary>
/// Tests for <see cref="BackendHttpHandlerFactory"/>, the shared builder that turns
/// <see cref="BackendConnectionOptions"/> into the tuned <see cref="SocketsHttpHandler"/> backing every
/// outbound backend client. The factory's job is small but load-bearing: it must copy the configured
/// lifetime and connect timeout onto the handler so DNS refresh and fast fail-over actually take effect.
/// </summary>
[Trait("Category", "Unit")]
public sealed class BackendHttpHandlerFactoryTests
{
	/// <summary>
	/// Verifies that <see cref="BackendHttpHandlerFactory.Create"/> projects both tuning values onto the
	/// returned handler.
	/// </summary>
	[Fact]
	public void Create_WithOptions_AppliesLifetimeAndConnectTimeout()
	{
		// Arrange: distinct, non-default values so a copy error surfaces instead of matching by accident.
		BackendConnectionOptions options = new()
		{
			PooledConnectionLifetimeSeconds = 90,
			ConnectTimeoutSeconds = 7
		};

		// Act
		using SocketsHttpHandler handler = BackendHttpHandlerFactory.Create(options);

		// Assert
		Assert.Equal(TimeSpan.FromSeconds(90), handler.PooledConnectionLifetime);
		Assert.Equal(TimeSpan.FromSeconds(7), handler.ConnectTimeout);
	}

	/// <summary>
	/// Verifies that <see cref="BackendHttpHandlerFactory.Create"/> rejects a <see langword="null"/> options
	/// argument.
	/// </summary>
	[Fact]
	public void Create_WhenOptionsIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() => BackendHttpHandlerFactory.Create(null!));
		Assert.Equal("options", exception.ParamName);
	}
}
