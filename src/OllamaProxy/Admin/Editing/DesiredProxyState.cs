// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.ComponentModel.DataAnnotations;

using OllamaProxy.Configuration;

namespace OllamaProxy.Admin.Editing;

/// <summary>
/// The whole proxy configuration as the admin editor holds it while the operator works. It has three parts:
/// the editable <see cref="Backends"/> (each a <see cref="DesiredBackend"/>), the proxy <see cref="ListenUrl"/>,
/// and the root <see cref="RequestTracing"/> diagnostics. This is the editor's draft model. The operator adds,
/// removes, renames, and reconfigures backends against it, and adjusts the global listener and tracing settings.
/// The materializer turns it into a real <see cref="ProxyOptions"/> for preview or commit.
/// </summary>
/// <remarks>
/// Unlike <see cref="ProxyOptions"/>, which keys backends by name, the editor holds them as an ordered list.
/// That way a rename is just an edit to a <see cref="DesiredBackend.Name"/>, not a remove-and-re-add. The list
/// order is the order the editor renders the backends. The materialized map keys them by their (validated
/// unique) names.
/// </remarks>
public sealed class DesiredProxyState
{
	/// <summary>
	/// Gets or sets the absolute URL the inner proxy host listens on. Defaults to
	/// <c>http://localhost:11434</c> so a fresh editor load is never blank. Edits here are carried into the
	/// materialized <see cref="ProxyOptions.ListenUrl"/>.
	/// </summary>
	[Required]
	[Url]
	public string ListenUrl { get; set; } = "http://localhost:11434";

	/// <summary>
	/// Gets the backends the operator is editing, in editor (render) order. Adding an entry stages a new backend,
	/// and removing one stages a deletion. Both take effect only when the desired state is applied. The list may
	/// be empty while the operator assembles a configuration from scratch, and an empty list is a valid
	/// configuration in its own right: it materializes to an empty <see cref="ProxyOptions.Backends"/> map and the
	/// proxy simply starts with no models until a backend is added.
	/// </summary>
	public IList<DesiredBackend> Backends { get; init; } = new List<DesiredBackend>();

	/// <summary>
	/// Gets the root request-tracing diagnostics the editor exposes alongside the backends. Exposing them here
	/// makes the full proxy configuration reachable from the UI, without hand-editing the file. The value is
	/// carried forward verbatim into the materialized <see cref="ProxyOptions.RequestTracing"/>.
	/// </summary>
	public RequestTracingOptions RequestTracing { get; init; } = new();
}
