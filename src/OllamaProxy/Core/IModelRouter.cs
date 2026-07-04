// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Diagnostics.CodeAnalysis;

namespace OllamaProxy.Core;

/// <summary>
/// The routing surface over the resolved model catalog. It exposes the aggregated set of client-facing
/// models (for <c>/api/tags</c>) and resolves a requested model name to its
/// <see cref="RegisteredModel"/> (for chat, embeddings, and <c>/api/show</c>). The catalog is built
/// once during startup discovery and then read concurrently by request handlers, so implementations
/// must be safe for concurrent readers.
/// </summary>
interface IModelRouter
{
	/// <summary>
	/// Gets the aggregated, client-facing model catalog in a stable, name-sorted order.
	/// </summary>
	/// <returns>The registered models currently exposed to clients.</returns>
	IReadOnlyList<RegisteredModel> GetModels();

	/// <summary>
	/// Resolves a client-supplied model name to its registered entry. Resolution is case-insensitive,
	/// tolerant of surrounding whitespace, and tolerant of an Ollama-style <c>:latest</c> tag suffix
	/// that clients append by convention.
	/// </summary>
	/// <param name="modelName">The model name supplied by the client.</param>
	/// <param name="model">When found, the resolved model entry; otherwise <see langword="null"/>.</param>
	/// <returns><see langword="true"/> when the model was resolved; otherwise <see langword="false"/>.</returns>
	/// <exception cref="ArgumentException">
	/// <paramref name="modelName"/> is <see langword="null"/>, empty, or whitespace.
	/// </exception>
	bool TryResolve(string modelName, [NotNullWhen(true)] out RegisteredModel? model);
}
