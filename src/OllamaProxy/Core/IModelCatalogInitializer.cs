// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Core;

/// <summary>
/// The write side of the model catalog, kept separate from the read-only <see cref="IModelRouter"/>
/// so request handlers depend only on the query surface while startup discovery holds the one
/// component allowed to populate the catalog. The same singleton implements both interfaces; this
/// split is an intent boundary, not a second instance.
/// </summary>
interface IModelCatalogInitializer
{
	/// <summary>
	/// Publishes the resolved catalog to the router. Called exactly once during startup discovery,
	/// before any request is served.
	/// </summary>
	/// <param name="models">The resolved models that make up the catalog.</param>
	/// <exception cref="ArgumentNullException">
	/// <paramref name="models"/> is <see langword="null"/>.
	/// </exception>
	/// <exception cref="InvalidOperationException">The catalog was already initialized.</exception>
	void Initialize(IEnumerable<RegisteredModel> models);
}
