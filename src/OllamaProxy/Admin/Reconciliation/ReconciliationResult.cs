// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Admin.Reconciliation;

/// <summary>
/// The outcome of reconciling one backend's existing registry pins against a freshly fetched snapshot. It holds
/// the full set of <see cref="Models"/> in stable snapshot order plus the three headline counts the admin
/// surface shows. Available pins and Discovered models are interleaved in snapshot order, so a model keeps its
/// position when toggled between pinned and unpinned; Unavailable pins are appended at the end. The engine
/// applies no further sorting or grouping, leaving that to the presentation layer.
/// </summary>
/// <param name="Models">
/// Every reconciled model: Available pins and Discovered candidates interleaved in snapshot order, followed by
/// Unavailable pins at the end. Never <see langword="null"/>; empty only when the backend had no pins and the
/// snapshot was empty.
/// </param>
public sealed record ReconciliationResult(IReadOnlyList<ReconciledModel> Models)
{
	/// <summary>
	/// Gets the number of pinned models the latest snapshot still offers
	/// (<see cref="ReconciledModelState.Available"/>).
	/// </summary>
	public int AvailableCount => CountIn(ReconciledModelState.Available);

	/// <summary>
	/// Gets the number of pinned models the latest snapshot no longer offers
	/// (<see cref="ReconciledModelState.Unavailable"/>). A non-zero value is the signal the admin surface
	/// highlights, since it means the backend dropped a model the operator had pinned.
	/// </summary>
	public int UnavailableCount => CountIn(ReconciledModelState.Unavailable);

	/// <summary>
	/// Gets the number of snapshot models that are not yet pinned
	/// (<see cref="ReconciledModelState.Discovered"/>) and can be promoted into the registry.
	/// </summary>
	public int DiscoveredCount => CountIn(ReconciledModelState.Discovered);

	/// <summary>
	/// Gets the number of available pins whose configured capabilities or context window no longer match what
	/// the backend reports (<see cref="ReconciledModel.IsDrifted"/>). A non-zero value is a second signal the
	/// admin surface highlights alongside <see cref="UnavailableCount"/>. The pin is still honored, but its
	/// recorded shape is stale and the operator may want to realign it. Drift is only possible for
	/// <see cref="ReconciledModelState.Available"/> rows, since it requires a backend value to compare against.
	/// </summary>
	public int DriftCount
	{
		get
		{
			int count = 0;
			foreach (ReconciledModel model in Models)
			{
				if (model.IsDrifted) count++;
			}

			return count;
		}
	}

	/// <summary>
	/// Counts the models in a given reconciled state.
	/// </summary>
	/// <param name="state">The state to count.</param>
	/// <returns>
	/// The number of <see cref="Models"/> whose <see cref="ReconciledModel.State"/> equals
	/// <paramref name="state"/>.
	/// </returns>
	private int CountIn(ReconciledModelState state)
	{
		int count = 0;
		foreach (ReconciledModel model in Models)
		{
			if (model.State == state) count++;
		}

		return count;
	}
}
