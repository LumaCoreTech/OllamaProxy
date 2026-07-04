// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Admin.Catalog;

/// <summary>
/// The admin surface's read-only view of the <em>live</em> model catalog: the exact set of models the running
/// inner proxy is serving right now. Unlike <see cref="IAdminModelService"/>, which discovers each backend's
/// models afresh against the editable draft, this reads the already-merged catalog the live host assembled
/// (collisions, prefixes, and shadowing resolved), so the page shows what a client such as Copilot would
/// actually see rather than a recomputation.
/// </summary>
interface IAdminCatalogService
{
	/// <summary>
	/// Reads the live catalog from the active inner proxy host. Returns a not-ready result when no host is
	/// currently serving (startup, a recycle's brief unbound window, or a failed start under the daemon policy),
	/// so the caller can distinguish "the proxy is not serving" from "the proxy serves an empty catalog".
	/// </summary>
	/// <returns>The live catalog, or <see cref="LiveCatalog.NotReady"/> when no host is currently serving.</returns>
	LiveCatalog GetLiveCatalog();
}
