// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Core;
using OllamaProxy.Hosting.Cascade;

namespace OllamaProxy.Admin.Catalog;

/// <summary>
/// The default <see cref="IAdminCatalogService"/>. It reads the live catalog through the
/// <see cref="IProxyHostSupervisor"/>, which resolves the active inner host's <see cref="IModelRouter"/> in
/// process. The admin surface, hosted on the non-recycling chassis, therefore observes exactly what the running
/// proxy serves without a loopback HTTP call (the chassis does not know, and cannot reliably reconstruct, the
/// inner proxy's bind address). The type is safe to share as a singleton because it is stateless and because
/// <see cref="IProxyHostSupervisor.GetLiveModels()"/> returns an immutable snapshot that is safe to read
/// concurrently.
/// </summary>
sealed class AdminCatalogService : IAdminCatalogService
{
	private readonly IProxyHostSupervisor mSupervisor;

	/// <summary>
	/// Initializes a new instance of the <see cref="AdminCatalogService"/> class.
	/// </summary>
	/// <param name="supervisor">Supplies the live catalog from the active inner proxy host.</param>
	/// <exception cref="ArgumentNullException"><paramref name="supervisor"/> is <see langword="null"/>.</exception>
	public AdminCatalogService(IProxyHostSupervisor supervisor)
	{
		ArgumentNullException.ThrowIfNull(supervisor);

		mSupervisor = supervisor;
	}

	/// <inheritdoc/>
	public LiveCatalog GetLiveCatalog()
	{
		// A null result is the supervisor's "no inner host serving" signal; map it to the not-ready state so the
		// UI shows a transient message rather than an empty table that would read as "the proxy offers nothing".
		IReadOnlyList<RegisteredModel>? models = mSupervisor.GetLiveModels();

		return models is null ? LiveCatalog.NotReady : LiveCatalog.Ready(models);
	}
}
