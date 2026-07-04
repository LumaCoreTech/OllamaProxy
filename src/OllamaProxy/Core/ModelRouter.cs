// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Diagnostics.CodeAnalysis;

namespace OllamaProxy.Core;

/// <summary>
/// The default <see cref="IModelRouter"/>. The catalog is produced asynchronously by startup
/// discovery (which queries backends over HTTP), so the router begins empty and is populated exactly
/// once via <see cref="Initialize"/>. Internally it holds an immutable <see cref="Snapshot"/> behind a
/// <see langword="volatile"/> reference: readers take the current snapshot with a single volatile read
/// and never lock, and initialization swaps in a fully built snapshot in one atomic assignment. This
/// satisfies the "built once, read concurrently" contract without per-read synchronization. Resolution
/// is case-insensitive and tolerant of an Ollama-style <c>:latest</c> tag suffix.
/// </summary>
sealed class ModelRouter : IModelRouter, IModelCatalogInitializer
{
	/// <summary>
	/// The conventional Ollama tag suffix that clients append to bare model names.
	/// </summary>
	private const string LatestSuffix = ":latest";

	/// <summary>
	/// The current catalog snapshot. Declared <see langword="volatile"/> so a reader always observes a
	/// fully constructed snapshot published by <see cref="Initialize"/>, never a torn or stale one.
	/// </summary>
	private volatile Snapshot mSnapshot = Snapshot.Empty;

	/// <summary>
	/// Populates the router with the resolved catalog. Intended to be called exactly once during
	/// startup discovery, before the router serves any request. The supplied models are copied into an
	/// immutable, name-sorted snapshot indexed for case-insensitive resolution; when two models
	/// normalize to the same key, the first in sorted order wins so the catalog is deterministic.
	/// </summary>
	/// <param name="models">The resolved models that make up the catalog.</param>
	/// <exception cref="ArgumentNullException"><paramref name="models"/> is <see langword="null"/>.</exception>
	/// <exception cref="InvalidOperationException">The router was already initialized.</exception>
	public void Initialize(IEnumerable<RegisteredModel> models)
	{
		ArgumentNullException.ThrowIfNull(models);

		if (mSnapshot.IsInitialized)
			throw new InvalidOperationException("The model router has already been initialized.");

		mSnapshot = Snapshot.Build(models);
	}

	/// <inheritdoc/>
	public IReadOnlyList<RegisteredModel> GetModels() => mSnapshot.Models;

	/// <inheritdoc/>
	public bool TryResolve(string modelName, [NotNullWhen(true)] out RegisteredModel? model)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

		return mSnapshot.Index.TryGetValue(NormalizeName(modelName), out model);
	}

	/// <summary>
	/// Normalizes a model name for lookup by trimming surrounding whitespace and stripping a trailing
	/// <c>:latest</c> tag so that <c>name</c> and <c>name:latest</c> resolve to the same entry.
	/// </summary>
	/// <param name="name">The raw model name to normalize.</param>
	/// <returns>The normalized lookup key.</returns>
	private static string NormalizeName(string name)
	{
		string trimmed = name.Trim();
		return trimmed.EndsWith(LatestSuffix, StringComparison.OrdinalIgnoreCase)
			       ? trimmed[..^LatestSuffix.Length]
			       : trimmed;
	}

	/// <summary>
	/// An immutable view of the catalog: the name-sorted model list returned to clients and the
	/// case-insensitive lookup index keyed without the <c>:latest</c> suffix. Instances are fully
	/// constructed before publication, so they can be shared freely across reading threads.
	/// </summary>
	private sealed class Snapshot
	{
		/// <summary>The empty snapshot a router exposes before initialization.</summary>
		public static readonly Snapshot Empty = new(
			[],
			new Dictionary<string, RegisteredModel>(StringComparer.OrdinalIgnoreCase),
			isInitialized: false);

		private Snapshot(
			IReadOnlyList<RegisteredModel>               models,
			IReadOnlyDictionary<string, RegisteredModel> index,
			bool                                         isInitialized)
		{
			Models = models;
			Index = index;
			IsInitialized = isInitialized;
		}

		/// <summary>
		/// Gets the client-facing catalog in stable, name-sorted order.
		/// </summary>
		public IReadOnlyList<RegisteredModel> Models { get; }

		/// <summary>
		/// Gets the normalized-name lookup index keyed without the <c>:latest</c> suffix.
		/// </summary>
		public IReadOnlyDictionary<string, RegisteredModel> Index { get; }

		/// <summary>
		/// Gets a value indicating whether this snapshot came from <see cref="Build"/>.
		/// </summary>
		public bool IsInitialized { get; }

		/// <summary>
		/// Builds an initialized snapshot from the supplied models, sorting them by name and indexing
		/// them under their normalized (suffix-stripped) names.
		/// </summary>
		/// <param name="models">The models to include in the snapshot.</param>
		/// <returns>The constructed snapshot.</returns>
		public static Snapshot Build(IEnumerable<RegisteredModel> models)
		{
			RegisteredModel[] ordered = models
				.OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
				.ToArray();

			var index = new Dictionary<string, RegisteredModel>(ordered.Length, StringComparer.OrdinalIgnoreCase);
			foreach (RegisteredModel model in ordered) index.TryAdd(NormalizeName(model.Name), model);

			return new Snapshot(ordered, index, isInitialized: true);
		}
	}
}
