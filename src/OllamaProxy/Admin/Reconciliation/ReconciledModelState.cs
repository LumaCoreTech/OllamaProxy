// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Admin.Reconciliation;

/// <summary>
/// The reconciled state of a single model after an operator's existing registry pins are compared against a
/// freshly fetched backend snapshot. The three states are exhaustive and mutually exclusive: a model is
/// either pinned-and-present, pinned-but-gone, or present-but-unpinned.
/// </summary>
public enum ReconciledModelState
{
	/// <summary>
	/// The model is pinned in the registry and the latest snapshot still offers it. The pin is confirmed by
	/// the backend and remains fully usable.
	/// </summary>
	Available,

	/// <summary>
	/// The model is pinned in the registry but the latest snapshot no longer offers it: the backend stopped
	/// advertising the upstream model. The pin is deliberately <em>retained</em>, never silently dropped, and
	/// surfaced in this state so the operator can decide whether to keep it (the backend may restore it) or
	/// remove it.
	/// </summary>
	Unavailable,

	/// <summary>
	/// The model is offered by the latest snapshot but is not yet pinned in the registry. It is a candidate the
	/// operator can promote into an explicit pin.
	/// </summary>
	Discovered
}
