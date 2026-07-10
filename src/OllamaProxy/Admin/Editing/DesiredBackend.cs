// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Configuration;

namespace OllamaProxy.Admin.Editing;

/// <summary>
/// One backend as the admin editor holds it while the operator works. It has three parts: the logical
/// <see cref="Name"/> (which the operator may rename), the <see cref="OriginalName"/> it carried when the editor
/// loaded it (<see langword="null"/> for a backend being added), and the full editable backend surface in
/// <see cref="Options"/>. Materializing a <see cref="DesiredProxyState"/> turns each entry into a
/// <see cref="BackendOptions"/> keyed by <see cref="Name"/>. This type is <see langword="public"/> so the admin
/// UI's component parameters can accept it. Otherwise, every parent would have to expose internals.
/// </summary>
/// <remarks>
///     <para>
///     <b>Write-only API key.</b> Secrets are never echoed back to the browser. So the editor loads an existing
///     backend with a <em>blank</em> <see cref="BackendOptions.ApiKey"/> in <see cref="Options"/> and shows only
///     a "saved" hint. On materialize, a blank key means <em>keep the existing key</em>. The materializer
///     recovers that key from the live configuration snapshot. It matches by <see cref="OriginalName"/>, so a
///     rename does not lose it. A non-blank key means <em>replace</em>. A backend being added has no existing
///     key to keep (<see cref="OriginalName"/> is <see langword="null"/>). Its blank key therefore stays blank,
///     the recycle's dry-run rejects it, and the missing secret surfaces immediately.
///     </para>
///     <para>
///     <b>The editor adds no validation.</b> Every domain rule (URL shape, provider support, key length, model
///     rules) is enforced by the recycle's dry-run when the desired state is applied. None of it runs here. The
///     editor enforces only one structural guard: each backend must have a non-blank <see cref="Name"/>, and the
///     names must be unique. This matters because <see cref="Name"/> becomes a dictionary key, which would
///     otherwise collide silently.
///     </para>
///     <para>
///     <b>The draft holds verbatim input; normalization is a boundary concern.</b> Distinct from validation, the
///     draft deliberately stores exactly what the operator typed so a field can pass through intermediate states
///     while editing. Light normalization of <em>free-text</em> fields (trimming, blank-to-null for the model
///     prefix, blank rejection for names) is therefore applied by the page's edit handlers, not by setters on
///     <see cref="Options"/>. A normalizing setter would be the wrong home twice over: it would run during config
///     binding (<see cref="BackendOptions"/> is also the settings-file bind target) and it would swallow the very
///     blank state <see cref="BackendOptions"/>'s own validation exists to reject. Typed fields (context length,
///     reasoning effort) need no such handling because Blazor already maps a cleared field to
///     <see langword="null"/>; only free text is normalized on the way into the draft.
///     </para>
/// </remarks>
public sealed class DesiredBackend
{
	/// <summary>
	/// Gets or sets the logical backend name. It becomes the backend's key in the materialized
	/// <see cref="ProxyOptions.Backends"/> map, so it must be non-blank and unique across the desired state. The
	/// operator may change it to rename a backend. <see cref="OriginalName"/> still points at the pre-rename
	/// identity, so the existing secret is carried forward.
	/// </summary>
	public string Name { get; set; } = string.Empty;

	/// <summary>
	/// Gets the name this backend carried when the editor loaded it, or <see langword="null"/> when the backend
	/// is being added. When <see cref="Options"/> carries a blank <see cref="BackendOptions.ApiKey"/>, the
	/// materializer uses this name to recover the existing API key from the live configuration snapshot. That
	/// way, renaming a backend (changing <see cref="Name"/>) does not drop its saved secret.
	/// </summary>
	public string? OriginalName { get; init; }

	/// <summary>
	/// Gets the full editable backend surface: base address, provider type, API key, operating mode, probing
	/// settings, default context length, model prefix, reasoning effort, and the explicit model registry. The
	/// editor binds directly to these members, so any backend option is editable without a parallel field here.
	/// </summary>
	/// <remarks>
	/// The <see cref="BackendOptions.ApiKey"/> follows the write-only, blank-means-keep contract described on the
	/// type. It is <em>not</em> the live secret. Everything else is the value the operator sees and edits.
	/// </remarks>
	public BackendOptions Options { get; init; } = new();
}
