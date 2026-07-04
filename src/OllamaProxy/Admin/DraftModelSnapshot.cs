// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Admin.Fetch;
using OllamaProxy.Core;

namespace OllamaProxy.Admin;

/// <summary>
/// The result of fetching one draft backend's <em>raw</em> model snapshot for the editor. On success it carries
/// the resolved <see cref="Snapshot"/> of candidates the backend currently offers; on failure it carries the
/// classified <see cref="ErrorKind"/> and its <see cref="ErrorMessage"/>. The candidates are handed back
/// unreconciled so the editor can reconcile them locally as the operator pins, unpins, or changes the backend's
/// mode, re-running <see cref="Reconciliation.ModelReconciler.ReconcileBackend"/> against the live draft without
/// a fetch per click.
/// </summary>
/// <param name="Succeeded">
/// <see langword="true"/> when the fetch completed and <see cref="Snapshot"/> is populated;
/// <see langword="false"/> when it failed and <see cref="ErrorKind"/> and <see cref="ErrorMessage"/> are
/// populated instead. The discriminator that tells the two payload halves apart.
/// </param>
/// <param name="Snapshot">
/// On success, the resolved candidates the backend currently offers, in the backend's reported order. They are
/// named, sized, and capability-resolved by <see cref="IBackendModelDiscovery"/>; <see langword="null"/> on
/// failure. Whether each candidate carries resolved capabilities depends on the <see cref="DiscoveryProbePolicy"/>
/// the fetch ran under. A refresh (<see cref="DiscoveryProbePolicy.NeverProbe"/>) surfaces only the capabilities
/// the provider already lists; a probe (<see cref="DiscoveryProbePolicy.ProbeAll"/>) resolves every model.
/// </param>
/// <param name="ErrorKind">
/// On failure, how far the failure could be attributed (credentials, upstream, or unknown);
/// <see langword="null"/> on success.
/// </param>
/// <param name="ErrorMessage">
/// On failure, a human-readable, English description of what went wrong; <see langword="null"/> on success.
/// </param>
/// <remarks>
///     <para>
///     <b>The mode and pins are deliberately not carried here.</b> The editor always reconciles the snapshot
///     against the current draft options (mode, model prefix, registry), so capturing them at fetch time would
///     only risk a stale copy. A mode or pin change between fetches is reflected immediately by re-reconciling
///     the same snapshot. The snapshot itself is mode-independent, since discovery lists the same candidates
///     regardless of how the registry is honored, so one fetch serves every mode the operator might switch to.
///     </para>
///     <para>
///     A discovered candidate carries the client-facing name it had at fetch time, but the editor does not
///     display that frozen value. Each reconcile recomputes a discovered row's exposed name from the draft's
///     <em>current</em> model prefix (the same rule pinned rows already follow), so changing the prefix updates
///     both pinned and discovered rows' exposed names immediately, without a refetch. A refetch is only needed to
///     pick up models the backend has started or stopped offering, not to realign names after a prefix edit.
///     </para>
/// </remarks>
public sealed record DraftModelSnapshot(
	bool                               Succeeded,
	IReadOnlyList<DiscoveryCandidate>? Snapshot,
	BackendFetchErrorKind?             ErrorKind,
	string?                            ErrorMessage)
{
	/// <summary>
	/// Creates a successful snapshot carrying the backend's resolved candidates for local reconciliation.
	/// </summary>
	/// <param name="snapshot">The resolved candidates the backend currently offers, in its reported order.</param>
	/// <returns>A result with <see cref="Succeeded"/> set and <see cref="Snapshot"/> populated.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
	public static DraftModelSnapshot Success(IReadOnlyList<DiscoveryCandidate> snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		return new DraftModelSnapshot(Succeeded: true, snapshot, ErrorKind: null, ErrorMessage: null);
	}

	/// <summary>
	/// Creates a failed snapshot from the backend's fetch failure, propagating its classified error verbatim.
	/// </summary>
	/// <param name="result">
	/// The failed fetch result whose <see cref="BackendFetchResult.ErrorKind"/> and
	/// <see cref="BackendFetchResult.ErrorMessage"/> are propagated.
	/// </param>
	/// <returns>
	/// A result with <see cref="Succeeded"/> cleared and <see cref="ErrorKind"/> and <see cref="ErrorMessage"/>
	/// populated from <paramref name="result"/>.
	/// </returns>
	/// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
	/// <exception cref="ArgumentException">
	/// <paramref name="result"/> represents a success (<see cref="BackendFetchResult.Succeeded"/> is
	/// <see langword="true"/>), which has no failure to propagate.
	/// </exception>
	public static DraftModelSnapshot FromFailedFetch(BackendFetchResult result)
	{
		ArgumentNullException.ThrowIfNull(result);

		if (result.Succeeded)
		{
			throw new ArgumentException(
				"A successful fetch result cannot be turned into a failed draft snapshot.",
				nameof(result));
		}

		return new DraftModelSnapshot(Succeeded: false, Snapshot: null, result.ErrorKind, result.ErrorMessage);
	}
}
