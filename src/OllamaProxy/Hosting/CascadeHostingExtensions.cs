// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Runtime.Versioning;

using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace OllamaProxy.Hosting;

/// <summary>
/// Wires up the partitioned two-host cascade and the writable data layout it requires. The outer chassis
/// reads only <c>hostsettings.json</c> (admin endpoint, run mode, chassis logging) and, under the Service
/// Control Manager, owns the service lifetime and Event Log; the inner proxy host reads only
/// <c>appsettings.json</c> (backends, models, the proxy port, tracing) and, under the service, attaches its
/// own Event Log provider and writes artifacts to <c>%ProgramData%\OllamaProxy\data</c>. Both files are layered
/// the same way: the shipped copy under the content root supplies the defaults, an optional operator copy under
/// <c>%ProgramData%\OllamaProxy</c> overrides it, and an environment variable always wins. Console and
/// container hosting keep writing artifacts beside the application under the content root.
/// </summary>
static class CascadeHostingExtensions
{
	/// <summary>
	/// The folder created beneath <see cref="Environment.SpecialFolder.CommonApplicationData"/>
	/// (<c>%ProgramData%</c>) that holds the service's configuration and its writable data subtree.
	/// </summary>
	private const string ProgramDataFolderName = "OllamaProxy";

	/// <summary>
	/// The subfolder of <see cref="ProgramDataFolderName"/> that holds mutable, runtime-produced
	/// artifacts (request traces, the generated effective configuration), kept separate from the
	/// read-only configuration files so the service account can be granted write access to it alone.
	/// </summary>
	private const string DataFolderName = "data";

	/// <summary>
	/// The operator-editable inner-proxy configuration file name the service reads from
	/// <c>%ProgramData%\OllamaProxy</c>. It mirrors the shipped <c>appsettings.json</c> shape so the
	/// same options bind, but lives outside the read-only install directory.
	/// </summary>
	private const string ProxyConfigFileName = "appsettings.json";

	/// <summary>
	/// The operator-editable outer-chassis configuration file name the service reads from
	/// <c>%ProgramData%\OllamaProxy</c>. It mirrors the shipped <c>hostsettings.json</c> shape so the
	/// same options bind, but lives outside the read-only install directory.
	/// </summary>
	private const string HostConfigFileName = "hostsettings.json";

	/// <summary>
	/// The Event Log source name shared by the outer chassis and the inner proxy host so all of the proxy's
	/// Windows Event Log entries appear under one recognizable source.
	/// </summary>
	private const string EventLogSourceName = "OllamaProxy";

	/// <summary>
	/// Configures the outer chassis host: it replaces the default <c>appsettings.json</c> configuration with
	/// the chassis's own <c>hostsettings.json</c>, registers the <see cref="IWritableProxyConfigFile"/> seam the
	/// admin surface persists operator edits through, and under the Service Control Manager it enables the
	/// Windows Service lifetime, attaches the Event Log, and layers the operator copy of <c>hostsettings.json</c>
	/// from <c>%ProgramData%\OllamaProxy</c> below the environment variables. Console and container hosting read
	/// only the shipped <c>hostsettings.json</c>.
	/// </summary>
	/// <param name="builder">The web application builder for the outer chassis.</param>
	/// <returns>The same <paramref name="builder"/> instance, to support call chaining.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
	public static WebApplicationBuilder AddOuterChassisHosting(this WebApplicationBuilder builder)
	{
		ArgumentNullException.ThrowIfNull(builder);

		// The chassis is configured by hostsettings.json, not the proxy's appsettings.json. Strip the default
		// JSON sources WebApplication.CreateBuilder added and layer the chassis file in their place, so the two
		// hosts never read each other's configuration.
		ReplaceJsonSourcesWithChassisConfig(builder.Configuration, builder.Environment.ContentRootPath);

		// The admin surface, hosted by the chassis, rewrites the operator-editable proxy configuration. Register
		// the write seam here (where the on-disk layout is owned) so the writer targets exactly the file the
		// inner host reloads on its next recycle: appsettings.json beside the app under the content root in a
		// foreground run, or the writable %ProgramData%\OllamaProxy copy under the Windows Service. This mirrors
		// how AddInnerProxyHosting registers IDataDirectory for the same disk-layout reason.
		builder.Services.AddSingleton<IWritableProxyConfigFile>(
			new WritableProxyConfigFile(ResolveOperatorConfigPath(builder.Environment.ContentRootPath)));

		if (!WindowsServiceHelpers.IsWindowsService())
		{
			return builder;
		}

		// Running under the SCM: the outer chassis is the process anchor, so it owns the service lifetime and
		// the Event Log. The content root has already been pinned to the install directory in
		// WebApplicationOptions, because a service starts with its working directory in System32.
		builder.Services.AddWindowsService(options => options.ServiceName = EventLogSourceName);

		string appRoot = GetProgramDataRoot();

		// The operator-editable chassis configuration lives in ProgramData (the install directory is read-only
		// to the service account). It overrides the shipped defaults but still loses to environment variables.
		InsertJsonFileBeforeEnvironmentVariables(builder.Configuration, Path.Combine(appRoot, HostConfigFileName));

		return builder;
	}

	/// <summary>
	/// Configures the inner proxy host: it always registers an <see cref="IDataDirectory"/> appropriate to the
	/// active hosting model, and under the Service Control Manager it layers the operator copy of
	/// <c>appsettings.json</c> from <c>%ProgramData%\OllamaProxy</c> below the environment variables, attaches
	/// the Event Log provider, and routes writable artifacts to <c>%ProgramData%\OllamaProxy\data</c>. It does
	/// not register the Windows Service lifetime; that belongs to the outer chassis. Console and container
	/// hosting point the data directory at the executable's own directory (<see cref="AppContext.BaseDirectory"/>).
	/// </summary>
	/// <param name="builder">The web application builder for the inner proxy host.</param>
	/// <returns>The same <paramref name="builder"/> instance, to support call chaining.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
	public static WebApplicationBuilder AddInnerProxyHosting(this WebApplicationBuilder builder)
	{
		ArgumentNullException.ThrowIfNull(builder);

		if (!WindowsServiceHelpers.IsWindowsService())
		{
			// Foreground hosting (console / container): write runtime artifacts beside the executable
			// (AppContext.BaseDirectory) rather than under the content root. In a container the two are the
			// same directory (/app), so this is a no-op there; in a published console deployment they are also
			// the same. The only place they differ is `dotnet run` / IDE debugging, where the content root is
			// the project folder (src/OllamaProxy) but the binary lives under bin/<config>/<tfm>. Anchoring to
			// the binary keeps traces out of the source tree (they would otherwise grow, unseen by Git, deep in
			// the project folder), lands them under the already-ignored bin/ directory, and makes them
			// self-cleaning on a `dotnet clean`/rebuild. An operator who wants traces at a fixed, persistent
			// location (e.g. a mounted container volume) sets RequestTracing.Directory to an absolute path,
			// which DataDirectory.Resolve honors verbatim; that, not the content root, is the volume seam.
			builder.Services.AddSingleton<IDataDirectory>(new DataDirectory(AppContext.BaseDirectory));

			return builder;
		}

		string appRoot = GetProgramDataRoot();

		// The operator-editable proxy configuration lives in ProgramData (the install directory is read-only to
		// the service account). It overrides the shipped defaults but is still trumped by environment variables.
		InsertJsonFileBeforeEnvironmentVariables(builder.Configuration, Path.Combine(appRoot, ProxyConfigFileName));

		// The inner host does not own the service lifetime, but it still routes its log output to the Windows
		// Event Log so operational messages land where an administrator looks for them. The wiring is extracted
		// into a platform-annotated helper because the deferred settings lambda is invoked by the framework
		// later, so the platform analyzer cannot see this call site's OS guard flow into it.
		if (OperatingSystem.IsWindows())
		{
			AddWindowsEventLog(builder.Logging);
		}

		// Mutable, runtime-produced artifacts go to the writable data subtree the installer grants the service
		// account modify rights on, never into the read-only install directory.
		builder.Services.AddSingleton<IDataDirectory>(new DataDirectory(Path.Combine(appRoot, DataFolderName)));

		return builder;
	}

	/// <summary>
	/// Builds a standalone configuration root over the inner proxy's <c>appsettings.json</c> layering, so a host
	/// that does not itself load the proxy configuration (the outer chassis, whose own configuration is
	/// <c>hostsettings.json</c>) can still read the live backend definitions for its admin surface. The file
	/// sources mirror <see cref="AddInnerProxyHosting"/>: the shipped file under the content root supplies the
	/// defaults, the environment-specific overlay layers on top, and an optional operator copy under
	/// <c>%ProgramData%\OllamaProxy</c> overrides them under the Service Control Manager. Unlike the inner host,
	/// this view <b>deliberately omits environment variables</b> so it reflects exactly what is on disk (see
	/// remarks). Every file source is watched for changes, so binding the result through
	/// <see cref="Microsoft.Extensions.Options.IOptionsMonitor{TOptions}"/> (or calling
	/// <see cref="IConfigurationRoot.Reload"/> before a fetch) observes operator edits (and the admin surface's
	/// own rewrites) without restarting the chassis.
	/// </summary>
	/// <param name="contentRootPath">The content root the shipped <c>appsettings.json</c> is resolved against.</param>
	/// <param name="environmentName">
	/// The host environment name used to layer the optional <c>appsettings.{environmentName}.json</c> overlay,
	/// matching the default host builder the inner proxy is created with.
	/// </param>
	/// <returns>
	/// A configuration root over the proxy's configuration <em>files</em> only; environment variables are
	/// intentionally excluded (see remarks). The caller owns the returned root and is responsible for its
	/// disposal, which a singleton DI registration satisfies automatically (the root holds change-watching
	/// file providers).
	/// </returns>
	/// <exception cref="ArgumentNullException">
	/// <paramref name="contentRootPath"/> or <paramref name="environmentName"/> is <see langword="null"/>.
	/// </exception>
	/// <exception cref="ArgumentException">
	/// <paramref name="contentRootPath"/> or <paramref name="environmentName"/> is empty or consists only of
	/// white-space characters.
	/// </exception>
	/// <remarks>
	///     <para>
	///     This reconstructs the inner host's proxy-config <em>file</em> layering because the chassis has no
	///     default proxy sources to build on (its own builder reads <c>hostsettings.json</c>). The inner host
	///     obtains <c>appsettings.json</c> and <c>appsettings.{Environment}.json</c> from the default host
	///     builder and only adds the ProgramData overlay itself; this method rebuilds the same file chain. Keep
	///     the <em>file</em> sources in sync: a change to the inner host's proxy-config files must be reflected
	///     here, or the admin surface and the running proxy would read different backend definitions.
	///     </para>
	///     <para>
	///     The environment-variable source is the one source that is <b>intentionally not</b> mirrored. The
	///     inner host layers environment variables on top so they win at runtime in production; this admin view
	///     omits them so it stays file-only. That asymmetry is the design: the admin surface edits and persists
	///     the file, while environment variables remain a production-only override consumed solely by the
	///     running proxy. Do not add <c>AddEnvironmentVariables()</c> here to "complete" the parity; doing so
	///     would surface environment-only secrets in the admin view and risk persisting them to disk.
	///     </para>
	/// </remarks>
	public static IConfigurationRoot BuildProxyOptionsConfiguration(string contentRootPath, string environmentName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);

		ConfigurationBuilder builder = new();

		// The shipped appsettings.json under the content root supplies the defaults, exactly as the inner host's
		// default builder loads it. Optional so a config-less deployment yields an empty, tolerant catalog
		// rather than crashing the admin surface.
		builder.AddJsonFile(
			Path.Combine(contentRootPath, ProxyConfigFileName),
			optional: true,
			reloadOnChange: true);

		// The environment-specific overlay the default host builder layers on top, kept for fidelity with what
		// the inner proxy actually binds (for example appsettings.Development.json for a console run).
		builder.AddJsonFile(
			Path.Combine(contentRootPath, $"appsettings.{environmentName}.json"),
			optional: true,
			reloadOnChange: true);

		// Under the Service Control Manager the operator copy in ProgramData overrides the shipped defaults, the
		// same overlay AddInnerProxyHosting inserts for the inner host. Foreground hosting has no such overlay.
		if (WindowsServiceHelpers.IsWindowsService())
		{
			builder.AddJsonFile(
				Path.Combine(GetProgramDataRoot(), ProxyConfigFileName),
				optional: true,
				reloadOnChange: true);
		}

		// Deliberately NO AddEnvironmentVariables() here: this is where the admin snapshot intentionally
		// diverges from the inner host. The admin surface authors the file: it must show, edit, and persist
		// exactly what is on disk, so an environment-only secret must not bleed into this view (an operator
		// cannot meaningfully edit a value that lives in an environment variable, and persisting it would copy
		// the secret onto disk). Environment variables remain authoritative for the running proxy alone, where
		// the inner host's own default builder layers them on top (see AddInnerProxyHosting). Do NOT "re-sync"
		// this by adding env vars back; the divergence is the design, not an oversight.
		return builder.Build();
	}

	/// <summary>
	/// Resolves the <c>%ProgramData%\OllamaProxy</c> root that holds the operator configuration copies and the
	/// writable data subtree.
	/// </summary>
	/// <returns>The absolute path to the ProgramData application root.</returns>
	private static string GetProgramDataRoot() => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
		ProgramDataFolderName);

	/// <summary>
	/// Resolves the absolute path of the single operator-editable proxy configuration file the admin surface
	/// rewrites, matching the file the inner host reloads on its next recycle. Under the Windows Service that is
	/// the writable operator copy at <c>%ProgramData%\OllamaProxy\appsettings.json</c> (the install directory is
	/// read-only to the service account); a foreground run writes <c>appsettings.json</c> beside the application
	/// under the content root, which there serves as both the shipped defaults and the operator file.
	/// </summary>
	/// <param name="contentRootPath">The content root the foreground operator file is resolved against.</param>
	/// <returns>The absolute path the admin surface persists the proxy configuration to.</returns>
	private static string ResolveOperatorConfigPath(string contentRootPath) => Path.Combine(
		WindowsServiceHelpers.IsWindowsService() ? GetProgramDataRoot() : contentRootPath,
		ProxyConfigFileName);

	/// <summary>
	/// Attaches the Windows Event Log provider with the shared source name. Isolated into a
	/// <see cref="SupportedOSPlatformAttribute"/>-annotated method because the configuration callback runs
	/// later (when the framework materializes the options), so the platform analyzer needs the annotation here
	/// rather than relying on the caller's runtime <see cref="OperatingSystem.IsWindows"/> guard.
	/// </summary>
	/// <param name="logging">The logging builder the Event Log provider is added to.</param>
	[SupportedOSPlatform("windows")]
	private static void AddWindowsEventLog(ILoggingBuilder logging) =>
		logging.AddEventLog(settings => settings.SourceName = EventLogSourceName);

	/// <summary>
	/// Replaces the JSON configuration sources <see cref="WebApplication.CreateBuilder(WebApplicationOptions)"/>
	/// added for the proxy's <c>appsettings.json</c> with the outer chassis's <c>hostsettings.json</c>, so the
	/// chassis is configured independently of the inner proxy. The shipped file is resolved from the content
	/// root and watched for changes, matching the framework's own behavior.
	/// </summary>
	/// <param name="configuration">The configuration manager whose sources are reordered.</param>
	/// <param name="contentRootPath">The content root the shipped <c>hostsettings.json</c> is resolved against.</param>
	private static void ReplaceJsonSourcesWithChassisConfig(ConfigurationManager configuration, string contentRootPath)
	{
		IList<IConfigurationSource> sources = configuration.Sources;

		// Drop the proxy's appsettings.json (and its environment overlay) the default builder added; the chassis
		// must not inherit the inner proxy's configuration.
		for (int index = sources.Count - 1; index >= 0; index--)
		{
			if (sources[index] is JsonConfigurationSource { Path: { } path } &&
			    path.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase))
			{
				sources.RemoveAt(index);
			}
		}

		JsonConfigurationSource chassis = new()
		{
			Path = Path.Combine(contentRootPath, HostConfigFileName),
			Optional = false,
			ReloadOnChange = true
		};
		chassis.ResolveFileProvider();

		// Insert the chassis file ahead of the environment-variables source so files lose to env vars, matching
		// the proxy's own layering contract.
		sources.Insert(FindEnvironmentVariablesIndex(sources), chassis);
	}

	/// <summary>
	/// Adds an optional JSON configuration file immediately ahead of the environment-variables source,
	/// so the file overrides the shipped defaults yet still yields to any environment variable override.
	/// The file is resolved from an absolute path and watched for changes, matching the behavior of the
	/// framework's own configuration sources.
	/// </summary>
	/// <param name="configuration">The configuration manager whose sources are reordered.</param>
	/// <param name="path">The absolute path to the JSON file to layer in.</param>
	private static void InsertJsonFileBeforeEnvironmentVariables(ConfigurationManager configuration, string path)
	{
		JsonConfigurationSource source = new()
		{
			Path = path,
			Optional = true,
			ReloadOnChange = true
		};

		// An absolute path has no file provider yet; resolving it roots a physical provider at the file's
		// directory so the source can be read like any other configuration file.
		source.ResolveFileProvider();

		configuration.Sources.Insert(FindEnvironmentVariablesIndex(configuration.Sources), source);
	}

	/// <summary>
	/// Finds the index of the environment-variables source so a file source can be inserted directly before it
	/// (files lose to environment variables). Falls back to the end of the list when no such source is present,
	/// though one always is under <see cref="WebApplication.CreateBuilder(WebApplicationOptions)"/>.
	/// </summary>
	/// <param name="sources">The configuration sources to scan.</param>
	/// <returns>The insertion index directly before the environment-variables source, or the list length.</returns>
	private static int FindEnvironmentVariablesIndex(IList<IConfigurationSource> sources)
	{
		for (int candidate = 0; candidate < sources.Count; candidate++)
		{
			if (sources[candidate] is EnvironmentVariablesConfigurationSource)
			{
				return candidate;
			}
		}

		return sources.Count;
	}
}
