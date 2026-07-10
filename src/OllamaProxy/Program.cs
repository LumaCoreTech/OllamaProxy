// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Microsoft.Extensions.Options;

using OllamaProxy.Admin;
using OllamaProxy.Admin.Ui;
using OllamaProxy.Hosting;
using OllamaProxy.Hosting.Cascade;

namespace OllamaProxy;

static class Program
{
	private static void Main(string[] args)
	{
		// The process entry point hosts the OUTER chassis: a stable, non-recycling host that anchors the
		// Service Control Manager (or the foreground shell) and exposes only the admin endpoint. The actual
		// proxy engine runs in a separate inner host that the supervisor builds, starts, and can recycle
		// beneath this one, so the proxy is reconfigured without ever dropping the chassis or the SCM contact.

		// One service-environment probe drives every hosting decision that differs between a managed service and
		// a foreground run: the content-root pin here, the chassis overlays in AddOuterChassisHosting, the inner
		// host composition through the factory, and the HostMode.Auto resolution in ResolveSupervisor. Routing
		// them all through the same seam (rather than scattered static WindowsServiceHelpers.IsWindowsService()
		// calls) guarantees the whole process is composed for exactly one hosting model and makes each of those
		// decisions deterministically testable.
		var serviceEnvironment = WindowsServiceEnvironment.Instance;

		// A Windows Service starts with its working directory in System32, so the content root must be pinned
		// to the executable's directory before the builder reads the shipped hostsettings.json. Foreground
		// hosting (console / container) leaves it at the default so nothing changes there.
		WebApplicationOptions options = new()
		{
			Args = args,
			ContentRootPath = serviceEnvironment.IsWindowsService ? AppContext.BaseDirectory : null
		};

		WebApplicationBuilder builder = WebApplication.CreateBuilder(options);

		// Configure the chassis from hostsettings.json (replacing the proxy's appsettings.json sources) and,
		// under the service, enable the service lifetime, Event Log, and the ProgramData chassis overlay.
		builder.AddOuterChassisHosting(serviceEnvironment);

		// Expose the resolved environment so the factory (and the supervisor's mode resolution) compose against
		// the same hosting model this entry point pinned the content root for.
		builder.Services.AddSingleton(serviceEnvironment);

		// Bind and validate the chassis options. Admin carries the listening URL; Host carries the run mode
		// that decides whether an inner-host start failure is fatal.
		builder.Services.AddOptions<AdminOptions>()
			.BindConfiguration(AdminOptions.SectionName)
			.ValidateDataAnnotations()
			.ValidateOnStart();
		builder.Services.AddOptions<ChassisOptions>()
			.BindConfiguration(ChassisOptions.SectionName)
			.ValidateOnStart();

		// The admin surface lives on the chassis so it can observe and steer the inner proxy without depending on
		// the recyclable host it controls. The chassis reads only hostsettings.json, so it builds a dedicated,
		// change-watching snapshot of the inner proxy's appsettings.* layering and binds ProxyOptions tolerantly
		// against it: a proxy config that is invalid for the running proxy must still let the admin surface load
		// to fix it. The snapshot is kept alive for the process by the options binding (its change-token source
		// captures the configuration), so IOptionsMonitor<ProxyOptions> tracks on-disk edits; the file watchers
		// are released when the process exits.
		IConfigurationRoot proxyConfiguration = CascadeHostingExtensions.BuildProxyOptionsConfiguration(
			builder.Environment.ContentRootPath,
			builder.Environment.EnvironmentName);
		builder.Services.AddAdminModelServices(proxyConfiguration);

		// The admin UI is a Blazor Server (Interactive Server) app rendered on the chassis. Registering the
		// component services is unconditional and harmless on its own: nothing is reachable until the endpoints
		// are mapped below, which happens only when the admin surface is enabled. Hosting the UI on the
		// non-recycling chassis is what lets it trigger an inner-proxy recycle without dropping its own realtime
		// connection.
		builder.Services
			.AddRazorComponents()
			.AddInteractiveServerComponents();

		// The factory builds inner proxy hosts on demand; the supervisor owns the live inner host and performs
		// validated recycles. One supervisor instance is exposed through both its own interface (for a future
		// recycle trigger) and IHostedService (so the chassis starts and stops it), mirroring how the router is
		// shared behind two intent-revealing interfaces.
		builder.Services.AddSingleton<IProxyHostFactory>(static sp =>
			new ProxyHostFactory(sp.GetRequiredService<IServiceEnvironment>()));
		builder.Services.AddSingleton(ResolveSupervisor);
		builder.Services.AddSingleton<IProxyHostSupervisor>(static sp => sp.GetRequiredService<ProxyHostSupervisor>());
		builder.Services.AddHostedService(static sp => sp.GetRequiredService<ProxyHostSupervisor>());

		// Pin the chassis to its own admin URL. Two ambient, process-global settings would otherwise move it:
		// ASPNETCORE_URLS (targets the proxy port in dev) and, inside a container, the data-plane override
		// OllamaProxy__ListenUrl that rebinds the inner proxy to 0.0.0.0:11434. Both hosts read the same
		// environment, so without isolation the chassis inherits the proxy's listener address and collides on
		// :11434. UseUrls sets the chassis address explicitly; PreferHostingUrls then makes that address win over
		// any Kestrel:Endpoints or hosting-URL config (which by default overrides UseUrls, the well-known
		// "Overriding address(es)" behavior) so the chassis stays on its admin port regardless of the inner
		// proxy binding.
		string adminUrl = builder.Configuration.GetValue<string>($"{AdminOptions.SectionName}:Url")
		                  ?? new AdminOptions().Url;
		builder.WebHost.UseUrls(adminUrl);
		builder.WebHost.PreferHostingUrls(true);

		WebApplication app = builder.Build();

		// The chassis exposes two probes; the full Ollama surface lives in the inner proxy host.
		//   /health: liveness, the chassis process is up. Under the daemon policy it answers even when the inner
		//             proxy failed to start, which is exactly what keeps the SCM anchor (and the admin surface)
		//             reachable for a recovering recycle.
		//   /ready:  readiness, the inner proxy host is actually active and serving. An orchestrator or load
		//             balancer routes on this, so a chassis that is alive but has no serving proxy reports 503.
		app.MapGet("/health", static () => Results.Ok(new { status = "ok" }));
		app.MapGet(
			"/ready",
			static (IProxyHostSupervisor supervisor) => supervisor.IsInnerHostActive
				                                            ? Results.Ok(new { status = "ready" })
				                                            : Results.Json(
					                                            new { status = "not_ready" },
					                                            statusCode: StatusCodes.Status503ServiceUnavailable));

		// The admin surface is opt-out: enabled by default so a fresh install is manageable on localhost, but
		// gated here so AdminOptions.Enabled=false maps no admin route and starts no realtime hub. The chassis
		// then serves only the probes above. Mapping (not service registration) is the gate, so the disabled
		// surface leaves no reachable endpoint behind.
		AdminOptions adminOptions = app.Services.GetRequiredService<IOptions<AdminOptions>>().Value;
		if (adminOptions.Enabled)
		{
			// A full-page request to an unknown path (e.g. /bla typed into the address bar) is resolved by
			// server-side endpoint routing, not by the Blazor Router. No @page matches, so routing returns a
			// bodyless 404 and the browser shows its own error page. The Router's NotFoundPage never runs here
			// because the request never reaches a component. Re-execution closes that gap. The 404 is caught and
			// the pipeline is replayed against /not-found, which the NotFound page owns via @page, so the Router
			// now matches and renders it. The original 404 status is preserved, so the client gets a proper 404
			// that carries the styled not-found page instead of the browser default. In-app navigation and
			// NavigationManager.NotFound() still use the Router's NotFoundPage directly.
			app.UseStatusCodePagesWithReExecute("/not-found");

			// Antiforgery is required by the interactive server components' form handling; MapStaticAssets serves
			// the scoped-CSS bundle and wwwroot baseline the UI links.
			app.MapStaticAssets();
			app.UseAntiforgery();
			app.MapRazorComponents<App>()
				.AddInteractiveServerRenderMode();
		}

		app.Run();
	}

	/// <summary>
	/// Builds the single <see cref="ProxyHostSupervisor"/>, resolving the configured <see cref="HostMode"/> into
	/// the concrete fail-fast policy through <see cref="HostModeResolver"/>: <see cref="HostMode.Auto"/> becomes
	/// <see cref="HostMode.Daemon"/> under the Service Control Manager and <see cref="HostMode.Foreground"/>
	/// otherwise, so a managed service stays resident on a start failure while an interactive run fails fast.
	/// The resolution keys off the same shared <see cref="IServiceEnvironment"/> the entry point composed the
	/// rest of the process against.
	/// </summary>
	/// <param name="serviceProvider">The application service provider supplying the factory, options, and logger.</param>
	/// <returns>The configured supervisor instance.</returns>
	private static ProxyHostSupervisor ResolveSupervisor(IServiceProvider serviceProvider)
	{
		HostMode configuredMode = serviceProvider.GetRequiredService<IOptions<ChassisOptions>>().Value.Mode;
		var environment = serviceProvider.GetRequiredService<IServiceEnvironment>();

		return new ProxyHostSupervisor(
			serviceProvider.GetRequiredService<IProxyHostFactory>(),
			failFastOnStartFailure: HostModeResolver.ShouldFailFastOnStartFailure(configuredMode, environment),
			serviceProvider.GetRequiredService<ILogger<ProxyHostSupervisor>>());
	}
}
