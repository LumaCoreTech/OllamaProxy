// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OllamaProxy.Diagnostics;

/// <summary>
/// Registers the request-tracing subsystem: the ambient trace accessor that bridges the request
/// pipeline to the singleton provider layer, the file sink that persists completed traces, and the
/// middleware that bookends each request. Registration is unconditional (the middleware itself
/// short-circuits when tracing is disabled), so the wiring stays simple and the on/off decision lives
/// in one place (<see cref="Configuration.RequestTracingOptions.Enabled"/>).
/// </summary>
static class DiagnosticsServiceCollectionExtensions
{
	/// <summary>
	/// Adds the request-tracing services to the container.
	/// </summary>
	/// <param name="services">The service collection to register the tracing services with.</param>
	/// <returns>The same <paramref name="services"/> instance, to support call chaining.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
	public static IServiceCollection AddRequestTracing(this IServiceCollection services)
	{
		ArgumentNullException.ThrowIfNull(services);

		// Provide a default system clock for the tracing timestamps unless the host already registered one.
		services.TryAddSingleton(TimeProvider.System);

		// The accessor must be a singleton so the same AsyncLocal slot is shared by the middleware and the
		// singleton provider adapters; a scoped accessor would hand the providers a different instance. TryAdd
		// because the shared backend-discovery block already registers the same accessor for hosts that have no
		// tracing middleware (the chassis); on the inner host whichever block runs first wins and both resolve
		// the identical RequestTraceAccessor, so there is never a dead duplicate descriptor.
		services.TryAddSingleton<IRequestTraceAccessor, RequestTraceAccessor>();
		services.AddSingleton<IRequestTraceSink, FileRequestTraceSink>();
		services.AddSingleton<RequestTracingMiddleware>();

		return services;
	}
}
