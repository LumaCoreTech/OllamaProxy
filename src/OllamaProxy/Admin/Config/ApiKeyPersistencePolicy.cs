// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Admin.Config;

/// <summary>
/// Selects whether a backend's API key is persisted into the operator configuration file when the admin
/// surface rewrites it. This is a deployment-level decision, set once through
/// <see cref="Hosting.AdminOptions.ApiKeyPersistencePolicy"/> in <c>hostsettings.json</c> rather than chosen
/// per apply in the admin UI. Two modes exist: keep secrets out of the file and supply them through
/// environment variables, or accept them on disk for a self-contained configuration. The policy governs only
/// what is <em>written</em>; an environment variable always wins at runtime regardless, because environment
/// variables sit above the file in the configuration layering.
/// </summary>
/// <remarks>
/// The policy is applied to the operator-<em>entered</em> keys the admin surface submits. That surface
/// edits a file-only view (see <see cref="Hosting.CascadeHostingExtensions.BuildProxyOptionsConfiguration"/>),
/// so an environment-only secret is never present in the values flowing to the writer and therefore can
/// never be copied into the file by either policy.
/// </remarks>
public enum ApiKeyPersistencePolicy
{
	/// <summary>
	/// Persist each backend's entered API key into the configuration file verbatim, including for a
	/// brand-new backend. Choose this for a self-contained file an operator can copy between machines.
	/// </summary>
	WriteToFile,

	/// <summary>
	/// Blank every backend's API key in the written file, forcing the secret to be supplied through an
	/// environment variable (<c>OllamaProxy__Backends__&lt;name&gt;__ApiKey</c>) instead. The proxy then
	/// fails validation on the next recycle for any backend lacking that variable, surfacing the missing
	/// secret immediately rather than silently shipping a keyless configuration.
	/// </summary>
	EnvironmentOnly
}
