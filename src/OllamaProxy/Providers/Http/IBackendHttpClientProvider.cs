// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Providers.Http;

/// <summary>
/// Supplies a pre-configured <see cref="HttpClient"/> for a named backend. Each returned client
/// carries the backend's base address, bearer authentication, an infinite client-level timeout (so
/// long streaming responses are bounded by the resilience attempt timeout and the caller's
/// cancellation token rather than abruptly cut), and the shared resilience pipeline. Provider
/// adapters depend on this abstraction instead of the factory directly, keeping the client naming and
/// configuration details in one place.
/// </summary>
interface IBackendHttpClientProvider
{
	/// <summary>
	/// Creates an <see cref="HttpClient"/> configured for the specified backend.
	/// </summary>
	/// <param name="backendName">The logical backend name to obtain a client for.</param>
	/// <returns>A configured client; the caller owns its lifetime and should dispose it.</returns>
	HttpClient CreateClient(string backendName);

	/// <summary>
	/// Creates an <see cref="HttpClient"/> for the backend identified by the supplied context, choosing
	/// the path that matches the context's shape:
	/// <list type="bullet">
	///     <item>
	///         <description>
	///         A <b>committed</b> context (<see cref="BackendContext.Draft"/> is <see langword="null"/>)
	///         resolves the pre-configured, resilience-wrapped named client via
	///         <see cref="CreateClient(string)"/>, the steady-state routing path.
	///         </description>
	///     </item>
	///     <item>
	///         <description>
	///         A <b>draft</b> context (<see cref="BackendContext.Draft"/> is non-<see langword="null"/>)
	///         builds a one-shot ad-hoc client from the inline draft options, because no named client was
	///         registered for a backend that is not yet committed. The draft client carries no resilience
	///         pipeline by design: a preview fetch is rare and interactive.
	///         </description>
	///     </item>
	/// </list>
	/// </summary>
	/// <param name="backend">The backend context identifying the committed or draft backend.</param>
	/// <returns>A configured client; the caller owns its lifetime and should dispose it.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="backend"/> is <see langword="null"/>.</exception>
	/// <exception cref="NotSupportedException">
	/// The context is a draft (carries inline options) but this provider does not support draft clients.
	/// The default implementation throws for drafts so a stub that has not opted in fails loudly rather
	/// than silently misrouting a draft fetch to a committed backend's client.
	/// </exception>
	HttpClient CreateClient(BackendContext backend)
	{
		ArgumentNullException.ThrowIfNull(backend);

		// A draft context has no named client to resolve; an implementation must opt in to draft support
		// explicitly. Throwing here (rather than falling back to the name path) prevents a draft fetch
		// from being silently misrouted to whatever (if anything) is registered under the draft's name.
		if (backend.Draft is not null)
		{
			throw new NotSupportedException(
				"This IBackendHttpClientProvider does not support draft backend contexts. " +
				"Override CreateClient(BackendContext) to build an ad-hoc client from the inline options.");
		}

		return CreateClient(backend.Name);
	}
}
