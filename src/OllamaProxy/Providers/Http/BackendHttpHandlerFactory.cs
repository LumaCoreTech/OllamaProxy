// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Configuration;

namespace OllamaProxy.Providers.Http;

/// <summary>
/// The single source of truth for the primary <see cref="SocketsHttpHandler"/> that backs every outbound
/// backend <see cref="HttpClient"/>. Both the named, resilience-wrapped clients registered at startup and
/// the one-shot ad-hoc draft client build their handler here, so their transport behavior (DNS refresh
/// via pooled-connection lifetime, and fast fail-over via a bounded connect timeout) never drifts apart.
/// </summary>
static class BackendHttpHandlerFactory
{
	/// <summary>
	/// Builds a <see cref="SocketsHttpHandler"/> tuned for talking to DNS-named cloud backends: a finite
	/// <see cref="SocketsHttpHandler.PooledConnectionLifetime"/> so the pool re-resolves DNS periodically,
	/// and a finite <see cref="SocketsHttpHandler.ConnectTimeout"/> so a dead IP among several A-records is
	/// abandoned quickly instead of stalling on the OS default SYN timeout.
	/// </summary>
	/// <param name="options">The connection tuning supplying the lifetime and connect-timeout values.</param>
	/// <returns>A configured handler; the caller owns it and is responsible for its disposal.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
	public static SocketsHttpHandler Create(BackendConnectionOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);

		return new SocketsHttpHandler
		{
			PooledConnectionLifetime = options.PooledConnectionLifetime,
			ConnectTimeout = options.ConnectTimeout
		};
	}
}
