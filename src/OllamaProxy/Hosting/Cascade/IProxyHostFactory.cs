// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Hosting.Cascade;

/// <summary>
/// Builds a fresh, fully configured inner proxy host on demand. Each call constructs an independent host from a
/// current configuration snapshot (the named resilient HTTP clients, the provider adapters, and the lock-free
/// routing catalog) so reconfiguration is achieved by rebuilding the whole graph rather than mutating live,
/// immutable components. The supervisor uses this to build both throwaway dry-run candidates and the real host
/// it activates.
/// </summary>
interface IProxyHostFactory
{
	/// <summary>
	/// Builds a new inner proxy host from the current configuration, wired with the full proxy pipeline
	/// (options, backend clients, providers, routing core, tracing, and the Ollama endpoint surface).
	/// </summary>
	/// <param name="useDryRunServer">
	/// When <see langword="true"/>, the host is built with a non-binding server so it can be started for
	/// validation without contending for the proxy port the live host holds; when <see langword="false"/>, the
	/// host is built with the real Kestrel server and binds the proxy port when started.
	/// </param>
	/// <returns>
	/// A new, unstarted host. The caller owns its lifecycle and must start, stop, and dispose it.
	/// </returns>
	IHost CreateProxyHost(bool useDryRunServer);
}
