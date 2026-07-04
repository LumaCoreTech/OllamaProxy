// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Collections.Frozen;

using OllamaProxy.Configuration;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Providers;

/// <summary>
/// The default <see cref="IProviderCatalog"/>. It indexes the registered <see cref="ProviderDescriptor"/>s by
/// their <see cref="ProviderDescriptor.ProviderType"/> at construction and answers every provider-metadata
/// question from that index. The index is frozen after construction (backed by
/// <see cref="FrozenDictionary{TKey,TValue}"/> for a static guarantee of thread-safe, lock-free reads) and the
/// catalog holds no mutable state, so it is safe for concurrent callers. It mirrors <c>ProviderResolver</c>'s
/// construction shape (build mutable, detect duplicates, freeze) but aggregates the cheap, options-free
/// descriptors rather than the adapters, so it can be consulted during options validation without re-entering
/// the options graph.
/// </summary>
sealed class ProviderCatalog : IProviderCatalog
{
	private readonly FrozenDictionary<string, ProviderDescriptor> mByProviderType;

	/// <summary>
	/// Initializes a new instance of the <see cref="ProviderCatalog"/> class.
	/// </summary>
	/// <param name="descriptors">The provider descriptors registered in the container, one per provider adapter.</param>
	/// <exception cref="ArgumentNullException"><paramref name="descriptors"/> is <see langword="null"/>.</exception>
	/// <exception cref="InvalidOperationException">
	/// Two descriptors declare the same provider type, which would make selection ambiguous.
	/// </exception>
	public ProviderCatalog(IEnumerable<ProviderDescriptor> descriptors)
	{
		ArgumentNullException.ThrowIfNull(descriptors);

		// Materialize once so the registration order is preserved for the picker and the duplicate scan and the
		// freeze read the same sequence.
		ProviderDescriptor[] ordered = [.. descriptors];

		// Build the index in a mutable dictionary first so a duplicate provider type surfaces as a
		// domain-specific InvalidOperationException with a clear message; freeze it once the build is complete so
		// the catalog can serve concurrent readers without locking. This mirrors ProviderResolver's adapter index.
		var builder = new Dictionary<string, ProviderDescriptor>(StringComparer.OrdinalIgnoreCase);
		foreach (ProviderDescriptor descriptor in ordered)
		{
			if (!builder.TryAdd(descriptor.ProviderType, descriptor))
			{
				throw new InvalidOperationException(
					$"More than one provider descriptor is registered for provider type '{descriptor.ProviderType}'.");
			}
		}

		Providers = ordered;
		mByProviderType = builder.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
	}

	/// <inheritdoc/>
	public IReadOnlyList<ProviderDescriptor> Providers { get; }

	/// <inheritdoc/>
	public bool IsSupported(string? providerType) =>
		providerType is not null && mByProviderType.ContainsKey(providerType);

	/// <inheritdoc/>
	public OperatingMode DefaultModeFor(string? providerType) =>
		// Any unrecognized provider type falls back to Explicit, the conservative choice that keeps the operator
		// in control rather than auto-exposing an unreliable surface.
		providerType is not null && mByProviderType.TryGetValue(providerType, out ProviderDescriptor? descriptor)
			? descriptor.DefaultMode
			: OperatingMode.Explicit;

	/// <inheritdoc/>
	public string DefaultBaseUrlFor(string? providerType) =>
		// An unrecognized provider type has no canonical URL to suggest; an empty string means "no prefill".
		providerType is not null && mByProviderType.TryGetValue(providerType, out ProviderDescriptor? descriptor)
			? descriptor.DefaultBaseUrl
			: string.Empty;

	/// <inheritdoc/>
	public string DisplayNameFor(string? providerType) =>
		// Show an unrecognized value verbatim rather than throwing; the label is display-only.
		providerType is not null && mByProviderType.TryGetValue(providerType, out ProviderDescriptor? descriptor)
			? descriptor.DisplayName
			: providerType ?? string.Empty;

	/// <inheritdoc/>
	public OperatingMode ResolveMode(BackendOptions backend)
	{
		ArgumentNullException.ThrowIfNull(backend);

		return backend.Mode ?? DefaultModeFor(backend.ProviderType);
	}
}
