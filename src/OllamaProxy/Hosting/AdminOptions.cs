// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.ComponentModel.DataAnnotations;

using OllamaProxy.Admin.Config;

namespace OllamaProxy.Hosting;

/// <summary>
/// Binds the <c>Admin</c> section of <c>hostsettings.json</c>, configuring the outer chassis's own HTTP
/// endpoint (the stable address the Service Control Manager, or a foreground shell, keeps anchored while the
/// inner proxy host is recycled beneath it), whether the management UI is served on it at all, and how the
/// admin surface persists backend secrets. It is deliberately distinct from the inner proxy's Kestrel
/// configuration so the two hosts never contend for the same port or configuration file.
/// </summary>
sealed class AdminOptions
{
	/// <summary>
	/// The configuration section name this options object binds to.
	/// </summary>
	public const string SectionName = "Admin";

	/// <summary>
	/// Gets or sets whether the admin surface (the Blazor management UI) is served. Defaults to
	/// <see langword="true"/> so a fresh install is manageable immediately on <c>localhost</c>. Set it to
	/// <see langword="false"/> to disable the surface entirely: no admin page is reachable and no realtime
	/// connection is accepted, leaving only the <c>/health</c> and <c>/ready</c> probes on the chassis port.
	/// </summary>
	public bool Enabled { get; set; } = true;

	/// <summary>
	/// Gets or sets the absolute URL the outer chassis listens on, kept separate from the inner proxy's port
	/// (<c>:11434</c>) so the two hosts never collide. Defaults to <c>http://localhost:11435</c>; override it
	/// via configuration or the <c>Admin__Url</c> environment variable (for example to bind all interfaces
	/// inside a container).
	/// </summary>
	[Required]
	[Url]
	public string Url { get; set; } = "http://localhost:11435";

	/// <summary>
	/// Gets or sets whether the admin surface persists backend API keys into the proxy configuration file or
	/// blanks them so secrets must be supplied through environment variables. This is a deployment-level
	/// decision, not a per-apply choice: every page that writes the configuration uses the same policy. Defaults
	/// to <see cref="Admin.Config.ApiKeyPersistencePolicy.WriteToFile"/> for a self-contained file an operator can copy
	/// between machines. Set it to <see cref="Admin.Config.ApiKeyPersistencePolicy.EnvironmentOnly"/> in deployments where
	/// secrets are managed outside the file (for example through a secrets manager or container orchestrator).
	/// </summary>
	public ApiKeyPersistencePolicy ApiKeyPersistencePolicy { get; set; } = ApiKeyPersistencePolicy.WriteToFile;
}
