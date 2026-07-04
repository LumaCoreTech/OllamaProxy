// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Core;

namespace OllamaProxy.Admin.Catalog;

/// <summary>
/// The result of reading the live, client-facing model catalog for the admin surface: whether the inner proxy
/// host is currently serving, and, when it is, the models it offers. The two states are kept distinct so the
/// UI can tell "the proxy is not serving yet" apart from "the proxy is serving an empty catalog": the former is
/// a transient condition (startup, a recycle's brief unbound window, or a failed start under the daemon policy),
/// the latter a genuine configuration outcome.
/// </summary>
/// <param name="ProxyReady">
/// <see langword="true"/> when an inner proxy host is active and its catalog was read; <see langword="false"/>
/// when no host is currently serving, in which case <see cref="Models"/> is empty.
/// </param>
/// <param name="Models">
/// The live, name-sorted catalog the proxy is serving, or an empty list when <see cref="ProxyReady"/> is
/// <see langword="false"/>. Never <see langword="null"/>.
/// </param>
public sealed record LiveCatalog(bool ProxyReady, IReadOnlyList<RegisteredModel> Models)
{
	/// <summary>
	/// The catalog state reported when no inner proxy host is currently serving: not ready, with an empty model
	/// list. Shared because it carries no per-call data.
	/// </summary>
	public static LiveCatalog NotReady { get; } = new(ProxyReady: false, []);

	/// <summary>
	/// Creates a ready catalog state carrying the live models the proxy is serving.
	/// </summary>
	/// <param name="models">The live catalog read from the active inner host.</param>
	/// <returns>A ready <see cref="LiveCatalog"/> wrapping <paramref name="models"/>.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="models"/> is <see langword="null"/>.</exception>
	public static LiveCatalog Ready(IReadOnlyList<RegisteredModel> models)
	{
		ArgumentNullException.ThrowIfNull(models);

		return new LiveCatalog(ProxyReady: true, models);
	}
}
