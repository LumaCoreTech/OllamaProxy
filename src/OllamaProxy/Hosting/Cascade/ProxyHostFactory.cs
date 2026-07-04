// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting.WindowsServices;

using OllamaProxy.Configuration;
using OllamaProxy.Core;
using OllamaProxy.Diagnostics;
using OllamaProxy.Endpoints;
using OllamaProxy.Providers;
using OllamaProxy.Providers.Http;

namespace OllamaProxy.Hosting.Cascade;

/// <summary>
/// The production <see cref="IProxyHostFactory"/>: assembles the inner proxy host using the exact pipeline the
/// proxy ran as a single host before the cascade: options binding and validation, the per-backend resilient
/// HTTP clients, the provider adapters, the routing core with its startup discovery, request tracing, and the
/// Ollama-compatible endpoint surface. The only build-time variation is the server: a dry-run build swaps
/// Kestrel for a <see cref="NoopServer"/> so the candidate can be validated without binding the proxy port.
/// </summary>
sealed class ProxyHostFactory : IProxyHostFactory
{
	/// <inheritdoc/>
	public IHost CreateProxyHost(bool useDryRunServer)
	{
		// A Windows Service starts with its working directory in System32, so the content root must be pinned
		// to the executable's directory before the builder reads the shipped appsettings.json. Foreground
		// hosting (console / container) leaves it at the default so nothing changes there.
		WebApplicationOptions options = new()
		{
			ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null
		};

		WebApplicationBuilder builder = WebApplication.CreateBuilder(options);

		// Inner-host hosting concerns only: the writable data directory, the ProgramData appsettings overlay,
		// and the Event Log provider under the service. The service lifetime itself belongs to the outer
		// chassis, so it is deliberately not registered here.
		builder.AddInnerProxyHosting();

		// Options graph (bound + validated at startup) and the per-backend resilient HTTP clients the
		// providers send requests through.
		builder.Services.AddProxyOptions();
		builder.Services.AddBackendHttpClients(builder.Configuration);

		// Pin the inner proxy to its configured listener address. Without this the ambient ASPNETCORE_URLS
		// (set by launchSettings to :11434 in dev) or a container data-plane override would still work, but
		// only by accident and only when expressed as Kestrel config. Using the bound ProxyOptions.ListenUrl
		// makes the address explicit, validated, and admin-editable. PreferHostingUrls makes UseUrls win
		// over any stray Kestrel:Endpoints configuration in the same file.
		string listenUrl =
			builder.Configuration.GetValue<string>($"{ProxyOptions.SectionName}:{nameof(ProxyOptions.ListenUrl)}") ??
			new ProxyOptions().ListenUrl;
		builder.WebHost.UseUrls(listenUrl);
		builder.WebHost.PreferHostingUrls(true);

		// The shared backend-discovery stack (provider adapters and their capability prober, the backend
		// client provider, the resolver, and the discovery orchestration) composed the same way the outer
		// chassis composes it. Sharing this one block is what keeps the runtime catalog (built here) and the
		// admin fetch surface (on the chassis) discovering models through an identical path.
		builder.Services.AddBackendDiscovery();

		// Fail-fast provider-type validation, registered on the inner host only: its options binding validates on
		// start, so an unsupported backend provider type (or a default that no longer resolves) stops the proxy
		// here with a clear message rather than failing adapter resolution on the first request. The chassis binds
		// the same options tolerantly and deliberately does not register this rule.
		builder.Services.AddProviderTypeValidation();

		// The routing core that resolves model names against the catalog and runs startup discovery; it
		// consumes the resolver and clock the shared block registered above.
		builder.Services.AddProxyCore();

		// Optional per-request diagnostic tracing. Registered unconditionally; the middleware itself is a
		// no-op unless tracing is enabled in configuration.
		builder.Services.AddRequestTracing();

		// The pipeline safety net that converts an unexpected exception into a protocol-shaped error body.
		// A singleton IMiddleware resolved by the UseMiddleware call below.
		builder.Services.AddSingleton<ProxyExceptionHandlingMiddleware>();

		// Responses returned directly from endpoint handlers (for example the tags listing) are serialized by
		// these options; null omission keeps them consistent with the manually written Ollama payloads.
		builder.Services.ConfigureHttpJsonOptions(static jsonOptions =>
		{
			jsonOptions.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
		});

		if (useDryRunServer)
		{
			// Replace Kestrel with a non-binding server so a dry-run build can start, validating DI, options,
			// and discovery, without contending for the proxy port the live host already holds.
			builder.Services.RemoveAll<IServer>();
			builder.Services.AddSingleton<IServer, NoopServer>();
		}

		WebApplication app = builder.Build();

		// Tracing wraps the whole pipeline so it observes both the inbound request and the final response;
		// it sits before endpoint routing for that reason.
		app.UseMiddleware<RequestTracingMiddleware>();

		// The exception safety net sits inside tracing (so a synthesized 500 is still recorded) but around
		// the endpoints (so it catches anything they throw that is not an already-mapped ProviderException).
		app.UseMiddleware<ProxyExceptionHandlingMiddleware>();

		app.MapOllamaApi();

		return app;
	}
}
