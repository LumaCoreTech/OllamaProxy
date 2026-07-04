// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Collections.Frozen;

using Microsoft.Extensions.Options;

using OllamaProxy.Configuration;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Core;

/// <summary>
/// The default <see cref="IProviderResolver"/>. It indexes the registered provider adapters by their
/// <see cref="IProviderAdapter.ProviderType"/> at construction and, for each lookup, reads the named
/// backend from the validated <see cref="ProxyOptions"/> to select the adapter matching that backend's
/// provider type. Both the adapter index and the backend set are frozen after construction (backed
/// by <see cref="FrozenDictionary{TKey,TValue}"/> for a static guarantee of thread-safe, lock-free
/// reads) and the resolver holds no mutable state, so it is safe for concurrent callers.
/// </summary>
sealed class ProviderResolver : IProviderResolver
{
	// A draft backend has no configuration key; this synthetic name labels its context for diagnostics
	// only. The inline draft options, not this name, drive client creation and probing.
	private const string DraftBackendName = "(draft)";

	private readonly FrozenDictionary<string, IProviderAdapter> mAdaptersByProviderType;
	private readonly FrozenDictionary<string, BackendOptions>   mBackends;

	/// <summary>
	/// Initializes a new instance of the <see cref="ProviderResolver"/> class.
	/// </summary>
	/// <param name="options">The validated proxy options carrying the configured backends.</param>
	/// <param name="adapters">The provider adapters registered in the container.</param>
	/// <exception cref="ArgumentNullException">
	/// <paramref name="options"/> or <paramref name="adapters"/> is <see langword="null"/>.
	/// </exception>
	/// <exception cref="InvalidOperationException">
	/// Two adapters declare the same provider type, which would make selection ambiguous.
	/// </exception>
	public ProviderResolver(IOptions<ProxyOptions> options, IEnumerable<IProviderAdapter> adapters)
	{
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(adapters);

		// Build the adapter index in a mutable dictionary first so duplicate provider types surface as
		// a domain-specific InvalidOperationException with a clear message; freeze it once the build
		// is complete so the resolver can serve concurrent readers without locking.
		var adapterBuilder = new Dictionary<string, IProviderAdapter>(StringComparer.OrdinalIgnoreCase);
		foreach (IProviderAdapter adapter in adapters)
		{
			if (!adapterBuilder.TryAdd(adapter.ProviderType, adapter))
			{
				throw new InvalidOperationException(
					$"More than one provider adapter is registered for provider type '{adapter.ProviderType}'.");
			}
		}

		mAdaptersByProviderType = adapterBuilder.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
		mBackends = options.Value.Backends.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
	}

	/// <inheritdoc/>
	public ResolvedBackend Resolve(string backendName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(backendName);

		if (!mBackends.TryGetValue(backendName, out BackendOptions? backend))
			throw new InvalidOperationException($"Backend '{backendName}' is not configured.");

		if (!mAdaptersByProviderType.TryGetValue(backend.ProviderType, out IProviderAdapter? adapter))
		{
			throw new InvalidOperationException(
				$"No provider adapter is registered for provider type '{backend.ProviderType}' " +
				$"required by backend '{backendName}'.");
		}

		return new ResolvedBackend(adapter, new BackendContext(backendName));
	}

	/// <inheritdoc/>
	public ResolvedBackend ResolveDraft(BackendOptions draft)
	{
		ArgumentNullException.ThrowIfNull(draft);

		if (!mAdaptersByProviderType.TryGetValue(draft.ProviderType, out IProviderAdapter? adapter))
		{
			throw new InvalidOperationException(
				$"No provider adapter is registered for provider type '{draft.ProviderType}' " +
				$"required by the draft backend.");
		}

		// The draft is not in the committed backend set, so there is no configuration key to route by.
		// Its name is synthetic (used only for diagnostics) while the inline Draft carries the
		// authoritative base address, credentials, and probing settings the adapter and HTTP client read.
		return new ResolvedBackend(adapter, new BackendContext(DraftBackendName, draft));
	}
}
