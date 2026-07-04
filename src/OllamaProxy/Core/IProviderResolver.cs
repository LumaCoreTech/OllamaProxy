// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Configuration;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Core;

/// <summary>
/// Resolves a logical backend name to the <see cref="ResolvedBackend"/> that can service it: the
/// configured backend selects a provider type, and the resolver pairs the matching provider adapter
/// with a backend context. This keeps the endpoints free of any adapter-selection logic: they ask
/// for a backend by name and receive a ready-to-call adapter plus context. The configured backend set
/// is fixed at startup, so resolution is a read-only lookup safe for concurrent callers.
/// </summary>
interface IProviderResolver
{
	/// <summary>
	/// Resolves the adapter and context for the named backend.
	/// </summary>
	/// <param name="backendName">The logical backend name from configuration.</param>
	/// <returns>The adapter/context pair that services the backend.</returns>
	/// <exception cref="ArgumentNullException">
	/// <paramref name="backendName"/> is <see langword="null"/>.
	/// </exception>
	/// <exception cref="ArgumentException">
	/// <paramref name="backendName"/> is empty or whitespace.
	/// </exception>
	/// <exception cref="InvalidOperationException">
	/// The backend is not configured, or no provider adapter handles its provider type.
	/// </exception>
	ResolvedBackend Resolve(string backendName);

	/// <summary>
	/// Resolves the adapter and context for a <b>draft</b> (not-yet-committed) backend, selecting the
	/// adapter by the draft's <see cref="BackendOptions.ProviderType"/> and pairing it with a context
	/// that carries the draft options inline. This powers preview-before-commit discovery: the operator
	/// can fetch a backend's models before saving it, without registering a named HTTP client or
	/// mutating the committed options. The returned context's <see cref="ResolvedBackend.Context"/> has
	/// its <see cref="BackendContext.Draft"/> set, so the adapter builds an ad-hoc client and reads
	/// probing settings from the draft rather than by name.
	/// </summary>
	/// <param name="draft">The draft backend options to resolve an adapter for.</param>
	/// <returns>The adapter/context pair that can service the draft backend.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="draft"/> is <see langword="null"/>.</exception>
	/// <exception cref="InvalidOperationException">
	/// No provider adapter handles the draft's provider type.
	/// </exception>
	ResolvedBackend ResolveDraft(BackendOptions draft);
}
