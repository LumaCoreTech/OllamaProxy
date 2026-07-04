// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Configuration;

namespace OllamaProxy.Providers.Abstractions;

/// <summary>
/// The single, data-driven source of truth about which provider families the proxy ships and what defaults each
/// one carries. It aggregates the registered <see cref="ProviderDescriptor"/>s (one per
/// <see cref="IProviderAdapter"/>) and answers the questions: is a configured provider type supported, what mode
/// and base URL should a freshly added backend of that type default to, and what human-facing display label does
/// the admin picker show. Because it reads only the descriptors (never the adapters), it is cheap and free of the
/// adapters' options dependency, so it is safe to consult during configuration validation. Adding a provider means
/// registering one more adapter; every answer here updates automatically, with no edit to this seam.
/// </summary>
/// <summary>
/// The provider-catalog abstraction the admin UI uses to populate the provider-type dropdown. Declared
/// <see langword="public"/> so the admin UI's component parameters can accept it without forcing every parent
/// to expose internals.
/// </summary>
public interface IProviderCatalog
{
	/// <summary>
	/// Gets the descriptors of every registered provider, in a stable order suitable for driving the admin
	/// provider picker. The order follows registration so the operator sees a consistent list across loads.
	/// </summary>
	IReadOnlyList<ProviderDescriptor> Providers { get; }

	/// <summary>
	/// Determines whether the supplied provider-type discriminator corresponds to a registered provider,
	/// comparing case-insensitively. This is the membership check configuration validation uses to reject a
	/// backend whose provider type no adapter handles.
	/// </summary>
	/// <param name="providerType">The provider-type value to check.</param>
	/// <returns>
	/// <see langword="true"/> when a matching provider is registered; otherwise <see langword="false"/>.
	/// </returns>
	bool IsSupported(string? providerType);

	/// <summary>
	/// Returns the <see cref="OperatingMode"/> a freshly added backend of the given provider type should start in
	/// when the operator pins no explicit mode, taken from the matching descriptor's
	/// <see cref="ProviderDescriptor.DefaultMode"/>. An unrecognized provider type falls back to the conservative
	/// <see cref="OperatingMode.Explicit"/>. The comparison is case-insensitive.
	/// </summary>
	/// <param name="providerType">The provider-type discriminator of the backend being defaulted.</param>
	/// <returns>The recommended initial <see cref="OperatingMode"/> for the provider type.</returns>
	OperatingMode DefaultModeFor(string? providerType);

	/// <summary>
	/// Returns the canonical base URL prefilled for a freshly added backend of the given provider type, taken
	/// from the matching descriptor's <see cref="ProviderDescriptor.DefaultBaseUrl"/>, or <see cref="string.Empty"/>
	/// when the provider has no fixed public endpoint or the type is unrecognized. A convenience prefill for the
	/// admin UI only, never validated as "the correct" URL. The comparison is case-insensitive.
	/// </summary>
	/// <param name="providerType">The provider-type discriminator of the backend being defaulted.</param>
	/// <returns>The canonical base URL, or <see cref="string.Empty"/> when none exists.</returns>
	string DefaultBaseUrlFor(string? providerType);

	/// <summary>
	/// Returns the human-facing display label for the given provider type, taken from the matching descriptor's
	/// <see cref="ProviderDescriptor.DisplayName"/>. An unrecognized provider type is returned verbatim rather
	/// than throwing, since the label is display-only. The comparison is case-insensitive.
	/// </summary>
	/// <param name="providerType">The provider-type discriminator to label.</param>
	/// <returns>The display label for the provider type, or the value itself when unrecognized.</returns>
	string DisplayNameFor(string? providerType);

	/// <summary>
	/// Resolves the effective <see cref="OperatingMode"/> for a backend: its explicit
	/// <see cref="BackendOptions.Mode"/> when set, otherwise the provider-aware default from
	/// <see cref="DefaultModeFor"/> for the backend's <see cref="BackendOptions.ProviderType"/>. This is the
	/// single place the runtime catalog, the admin reconciler, and the editor read a backend's effective mode, so
	/// none of them can drift in how an unset mode is defaulted.
	/// </summary>
	/// <param name="backend">The backend whose effective mode to resolve.</param>
	/// <returns>The configured <see cref="BackendOptions.Mode"/>, or the provider default when none was set.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="backend"/> is <see langword="null"/>.</exception>
	OperatingMode ResolveMode(BackendOptions backend);
}
