// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Configuration;

namespace OllamaProxy.Admin.Editing;

/// <summary>
/// Bridges the admin editor's draft (<see cref="DesiredProxyState"/>) and the real <see cref="ProxyOptions"/> in
/// both directions. <see cref="Materialize"/> turns a draft into options ready to preview against or commit
/// through the apply path. <see cref="Dematerialize"/> turns the live options into a fresh draft for the editor
/// to load. The two paths handle write-only key resolution, structural name validation, and the copy semantics
/// that keep a browser edit from reaching the running proxy. The remarks below detail each.
/// </summary>
/// <remarks>
///     <para>
///     <b>Write-only key resolution.</b> The editor never receives a saved secret, so it loads an existing
///     backend with a blank <see cref="BackendOptions.ApiKey"/>. A blank key therefore means
///     <em>
///     keep the
///     existing key
///     </em>
///     . It is recovered from the snapshot by <see cref="DesiredBackend.OriginalName"/>, which
///     is the pre-rename identity, so renaming a backend does not drop its secret. A non-blank key is an explicit
///     replacement. A backend being added has nothing to keep (no <see cref="DesiredBackend.OriginalName"/>). Its
///     blank key stays blank, and the recycle's dry-run rejects the missing secret rather than this type
///     pre-empting it. All domain validation stays in the dry-run.
///     </para>
///     <para>
///     <b>The only guard here is structural.</b> A backend's trimmed <see cref="DesiredBackend.Name"/> becomes its
///     key in <see cref="ProxyOptions.Backends"/>. So a blank or duplicate name is a defect the dry-run cannot
///     catch. (A duplicate would silently overwrite a sibling before the configuration is ever validated.) Those
///     two cases throw. Every other rule (URL shape, provider support, key length, per-model rules) is left to the
///     recycle's dry-run.
///     </para>
///     <para>
///     <b>Deep copy on load.</b> <see cref="Dematerialize"/> reads the live options snapshot, which the options
///     monitor hands out as a shared instance. The editor two-way-binds the draft's nested members (probing
///     toggles, per-model registry rows, request tracing). So the reverse path clones every reference-typed
///     member rather than sharing it. An edit in the browser must never reach back into the running proxy's
///     in-memory configuration. The forward path's per-backend copy (<see cref="MaterializeBackend"/>) is shallow
///     by contrast, because it builds a throwaway state that is immediately serialized or validated, never edited.
///     </para>
/// </remarks>
static class DesiredStateMaterializer
{
	/// <summary>
	/// Materializes the whole editor draft into a <see cref="ProxyOptions"/>. Each <see cref="DesiredBackend"/>
	/// becomes a <see cref="BackendOptions"/> with its write-only key resolved against
	/// <paramref name="currentBackends"/> and keyed by its trimmed logical name. The draft's
	/// <see cref="DesiredProxyState.RequestTracing"/> is carried forward verbatim.
	/// </summary>
	/// <param name="state">The editor draft to materialize.</param>
	/// <param name="currentBackends">
	/// The live configuration's backends keyed by logical name, used only to recover the saved API key of a
	/// backend whose draft left the key blank. A backend not present here (a new backend, or one whose original
	/// entry is gone) simply keeps its draft key.
	/// </param>
	/// <returns>
	/// The materialized configuration: backends keyed case-insensitively by trimmed name (matching the binding
	/// contract) with resolved keys, plus the draft's request tracing.
	/// </returns>
	/// <exception cref="ArgumentNullException">
	/// <paramref name="state"/> or <paramref name="currentBackends"/> is <see langword="null"/>.
	/// </exception>
	/// <exception cref="ArgumentException">
	/// A backend in <paramref name="state"/> has a blank <see cref="DesiredBackend.Name"/>, or two backends share a
	/// trimmed name (compared case-insensitively, as the routing layer keys them).
	/// </exception>
	public static ProxyOptions Materialize(
		DesiredProxyState                   state,
		IDictionary<string, BackendOptions> currentBackends)
	{
		ArgumentNullException.ThrowIfNull(state);
		ArgumentNullException.ThrowIfNull(currentBackends);

		// Key trimmed names case-insensitively so the materialized map matches ProxyOptions.Backends' binding
		// contract. It also lets duplicate detection catch names that differ only in surrounding whitespace or case,
		// which collide in the routing layer too.
		Dictionary<string, BackendOptions> backends = new(StringComparer.OrdinalIgnoreCase);

		foreach (DesiredBackend desired in state.Backends)
		{
			// A blank name cannot be a dictionary key. A duplicate would silently overwrite the sibling already
			// added, dropping a backend before the dry-run ever sees the configuration. Both are rejected here.
			if (string.IsNullOrWhiteSpace(desired.Name))
			{
				throw new ArgumentException(
					"A backend in the desired state has a blank name.",
					nameof(state));
			}

			string name = desired.Name.Trim();
			if (!backends.TryAdd(name, MaterializeBackend(desired, currentBackends)))
			{
				throw new ArgumentException(
					$"The desired state contains more than one backend named '{name}'.",
					nameof(state));
			}
		}

		return new ProxyOptions
		{
			ListenUrl = state.ListenUrl,
			Backends = backends,
			RequestTracing = state.RequestTracing
		};
	}

	/// <summary>
	/// Turns the live <see cref="ProxyOptions"/> into a fresh editor draft. Each backend becomes a
	/// <see cref="DesiredBackend"/>. Its <see cref="DesiredBackend.Name"/> and
	/// <see cref="DesiredBackend.OriginalName"/> are the current map key, and its <see cref="DesiredBackend.Options"/>
	/// is a deep copy with the API key blanked. The root <see cref="ProxyOptions.RequestTracing"/> is copied
	/// into the draft. This is the reverse of <see cref="Materialize"/>. The editor loads a draft with this, edits
	/// it, and commits it back through <see cref="Materialize"/> (and the apply path).
	/// </summary>
	/// <param name="options">The live configuration snapshot to project into an editable draft.</param>
	/// <returns>
	/// A draft mirroring <paramref name="options"/>. It has one backend per entry, in the snapshot's enumeration
	/// order, each with a blank, write-only API key and an <see cref="DesiredBackend.OriginalName"/> pinned to the
	/// current name. It also carries a copy of the request tracing. Every reference-typed member is freshly
	/// allocated, so editing the draft never mutates the snapshot.
	/// </returns>
	/// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
	/// <remarks>
	/// The returned draft shares no mutable state with <paramref name="options"/>. Each backend's
	/// <see cref="BackendOptions.Probing"/>, its <see cref="BackendOptions.Models"/> (and each entry within it),
	/// and the <see cref="RequestTracingOptions"/> are all cloned. The API key is deliberately <em>not</em> carried
	/// over. A blank key is the editor's "keep the saved secret" sentinel. <see cref="Materialize"/> resolves it
	/// back to the real key by <see cref="DesiredBackend.OriginalName"/> on commit.
	/// </remarks>
	public static DesiredProxyState Dematerialize(ProxyOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);

		List<DesiredBackend> backends = [];
		foreach ((string name, BackendOptions backend) in options.Backends)
		{
			backends.Add(
				new DesiredBackend
				{
					// The current map key serves as both the editable name and the pre-rename identity. The commit
					// path uses that identity to recover the saved secret, so a rename still finds the original key.
					Name = name,
					OriginalName = name,
					Options = CloneWithoutKey(backend)
				});
		}

		return new DesiredProxyState
		{
			ListenUrl = options.ListenUrl,
			Backends = backends,
			RequestTracing = options.RequestTracing.DeepClone()
		};
	}

	/// <summary>
	/// Materializes a single draft backend into a <see cref="BackendOptions"/> with its write-only API key
	/// resolved against <paramref name="currentBackends"/>. This is the per-backend step <see cref="Materialize"/>
	/// runs for each entry. It is exposed on its own so the admin surface can preview one backend's models against
	/// its unsaved settings. The key resolution is identical, so the preview sees the same secret a commit would.
	/// </summary>
	/// <param name="desired">
	/// The draft backend to materialize. Its <see cref="DesiredBackend.Name"/> is not used here. Only the
	/// options and key matter.
	/// </param>
	/// <param name="currentBackends">
	/// The live backends keyed by name, used to recover a blank key by <see cref="DesiredBackend.OriginalName"/>.
	/// </param>
	/// <returns>A <see cref="BackendOptions"/> identical to the draft's options apart from the resolved API key.</returns>
	/// <exception cref="ArgumentNullException">
	/// <paramref name="desired"/> or <paramref name="currentBackends"/> is <see langword="null"/>.
	/// </exception>
	public static BackendOptions MaterializeBackend(
		DesiredBackend                      desired,
		IDictionary<string, BackendOptions> currentBackends)
	{
		ArgumentNullException.ThrowIfNull(desired);
		ArgumentNullException.ThrowIfNull(currentBackends);

		return desired.Options.WithApiKey(ResolveApiKey(desired, currentBackends));
	}

	/// <summary>
	/// Resolves a draft backend's effective API key. A non-blank draft key is an explicit replacement. A blank
	/// key is recovered from the snapshot by <see cref="DesiredBackend.OriginalName"/>, which keeps the saved
	/// secret across a rename. When nothing can be recovered, the key is left blank for the dry-run to reject.
	/// </summary>
	/// <param name="desired">The draft backend whose key to resolve.</param>
	/// <param name="currentBackends">The live backends keyed by name.</param>
	/// <returns>The key to persist: the entered key, the recovered saved key, or the empty string.</returns>
	private static string ResolveApiKey(
		DesiredBackend                      desired,
		IDictionary<string, BackendOptions> currentBackends)
	{
		// A non-blank entered key replaces the saved one outright.
		if (!string.IsNullOrWhiteSpace(desired.Options.ApiKey))
			return desired.Options.ApiKey;

		// A blank key means "keep the saved secret". OriginalName is the pre-rename identity, so the lookup
		// survives a rename. An exact-match lookup is correct because OriginalName was captured from this very
		// dictionary's keys. A new backend (null OriginalName) or a vanished original keeps the blank key. The
		// recycle's dry-run then rejects that as a missing required secret.
		if (desired.OriginalName is { } originalName &&
		    currentBackends.TryGetValue(originalName, out BackendOptions? current))
		{
			return current.ApiKey;
		}

		return desired.Options.ApiKey;
	}

	/// <summary>
	/// Deep-copies a live backend into an editable one with its API key blanked.
	/// <see cref="BackendOptions.DeepClone"/> produces a fully independent backend (a fresh
	/// <see cref="BackendOptions.Models"/> list with cloned entries and a cloned <see cref="BackendOptions.Probing"/>),
	/// so the editor can mutate the copy without reaching back into the live snapshot. The key is then blanked. A
	/// blank key is the editor's "keep the saved secret" sentinel, resolved back to the real key by
	/// <see cref="DesiredBackend.OriginalName"/> on commit, so the secret never leaves the server.
	/// </summary>
	/// <param name="backend">The live backend to copy.</param>
	/// <returns>A standalone copy carrying every setting except the (blanked, write-only) API key.</returns>
	private static BackendOptions CloneWithoutKey(BackendOptions backend)
	{
		// WithApiKey shallow-copies the throwaway deep clone, which is safe here: nothing else references that
		// clone's Models or Probing, so the result still shares no mutable state with the live snapshot.
		return backend.DeepClone().WithApiKey(string.Empty);
	}
}
