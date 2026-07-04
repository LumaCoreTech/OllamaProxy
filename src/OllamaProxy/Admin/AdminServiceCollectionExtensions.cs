// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Admin.Catalog;
using OllamaProxy.Admin.Config;
using OllamaProxy.Admin.Fetch;
using OllamaProxy.Configuration;
using OllamaProxy.Core;

namespace OllamaProxy.Admin;

/// <summary>
/// Composes the admin model surface on the outer chassis: it binds the inner proxy's
/// <see cref="ProxyOptions"/> <b>tolerantly</b> against a configuration snapshot of the proxy's own files,
/// adds the shared backend-discovery stack, registers the fetch and orchestration services the admin UI reads,
/// and registers the configuration persistence path (writer plus recycle-coupled applier) the admin UI writes
/// through. Keeping this on the non-recycling chassis is what lets the admin surface observe, steer, and
/// reconfigure the inner proxy without depending on the recyclable host it controls.
/// </summary>
/// <remarks>
///     <para>
///     <b>Why tolerant binding.</b> The chassis's own configuration is <c>hostsettings.json</c>; it does not load
///     the proxy's <c>appsettings.json</c>. The caller therefore supplies a dedicated proxy-configuration snapshot
///     (see <see cref="Hosting.CascadeHostingExtensions.BuildProxyOptionsConfiguration"/>), and this block binds it
///     <em>without</em> <c>ValidateDataAnnotations</c> or <c>ValidateOnStart</c>: a proxy configuration that is
///     invalid for the running proxy must still let the admin surface load so the operator can <em>fix</em> it. A
///     backend that bound incompletely simply surfaces as a failure row when its fetch is attempted, never as a
///     chassis startup crash. Validation is the inner host's job
///     (<see cref="ConfigurationServiceCollectionExtensions.AddProxyOptions"/>); the chassis only reads.
///     </para>
///     <para>
///     <b>Why the draft path needs no committed clients.</b> The shared block's resolver and providers are present,
///     but the chassis deliberately does <em>not</em> register the per-backend named HTTP clients. The fetcher
///     resolves every backend through the draft path, which builds an ad-hoc client from the freshly bound options,
///     so no name-keyed client is needed and the fetch always reflects the current snapshot.
///     </para>
/// </remarks>
static class AdminServiceCollectionExtensions
{
	/// <summary>
	/// Adds the chassis-side admin model services to the container, binding <see cref="ProxyOptions"/> tolerantly
	/// against the supplied proxy-configuration snapshot.
	/// </summary>
	/// <param name="services">The service collection to register the admin services with.</param>
	/// <param name="proxyConfiguration">
	/// A configuration root over the inner proxy's files, watched for changes so the bound options track operator
	/// edits. The same instance is kept alive through the options binding for the chassis (process) lifetime; its
	/// change-watching file providers are released at process exit.
	/// </param>
	/// <returns>The same <paramref name="services"/> instance, to support call chaining.</returns>
	/// <exception cref="ArgumentNullException">
	/// <paramref name="services"/> or <paramref name="proxyConfiguration"/> is <see langword="null"/>.
	/// </exception>
	public static IServiceCollection AddAdminModelServices(
		this IServiceCollection services,
		IConfiguration          proxyConfiguration)
	{
		ArgumentNullException.ThrowIfNull(services);
		ArgumentNullException.ThrowIfNull(proxyConfiguration);

		// Tolerant bind: no ValidateDataAnnotations / ValidateOnStart, so a proxy config that is invalid for the
		// running proxy still lets the admin surface load to fix it. Binding the section (rather than the whole
		// root) also registers a ConfigurationChangeTokenSource, so IOptionsMonitor<ProxyOptions> reflects file
		// reloads, meaning the admin view always reads the current on-disk configuration.
		services.AddOptions<ProxyOptions>()
			.Bind(proxyConfiguration.GetSection(ProxyOptions.SectionName));

		// The shared, host-agnostic discovery stack (resolver, providers, prober, client provider, discovery
		// orchestration). It requires the options graph above, which is registered first for readability.
		// Registration order is irrelevant to resolution, but binding before consuming reads clearly.
		services.AddBackendDiscovery();

		// The backend-level fetcher (draft path + caller-selected probe policy + honest failure classification)
		// and the admin-level orchestration that fetches every backend, isolates per-backend failures, and
		// reconciles successes. The default view load fetches without probing for speed; capability enrichment is
		// an explicit, per-backend action.
		services.AddSingleton<IBackendModelFetcher, BackendModelFetcher>();
		services.AddSingleton<IAdminModelService, AdminModelService>();

		// The read-only live-catalog view. It reads the running inner host's resolved catalog through the
		// supervisor (registered on the chassis in Program.cs), so the Models page shows exactly what the proxy
		// serves right now rather than a per-backend re-discovery like the Backends editor does.
		services.AddSingleton<IAdminCatalogService, AdminCatalogService>();

		// The configuration persistence path: the writer rewrites only the OllamaProxy section of the operator
		// file (preserving sibling sections and applying the AdminOptions persistence policy), and the applier
		// ties that write to a validated inner-host recycle with rollback-on-reject. Both depend on
		// IWritableProxyConfigFile and IProxyHostSupervisor, registered on the chassis (see
		// AddOuterChassisHosting and Program.cs), and the writer depends on IOptions<AdminOptions>.
		services.AddSingleton<IProxyConfigWriter, ProxyConfigWriter>();
		services.AddSingleton<IProxyConfigApplier, ProxyConfigApplier>();

		return services;
	}
}
