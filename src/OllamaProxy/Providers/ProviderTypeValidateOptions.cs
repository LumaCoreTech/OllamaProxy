// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Microsoft.Extensions.Options;

using OllamaProxy.Configuration;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Providers;

/// <summary>
/// Validates, at startup, that every configured backend names a provider type the proxy actually ships, and that
/// the neutral <see cref="BackendOptions.DefaultProviderType"/> the configuration layer falls back to itself
/// resolves to a registered provider. This rule lives here, in the <c>Providers</c> namespace, rather than in
/// <see cref="BackendOptions.Validate"/>, for two reasons: the configuration layer deliberately does not depend
/// on <c>Providers</c> (so it cannot consult the catalog), and the "is this provider supported" question is only
/// answerable against the registered provider set, which the catalog owns. It is the safety net for the
/// single tolerated literal: rename or remove the OpenAI adapter and the proxy fails to start with a clear
/// message rather than defaulting backends to a provider that no longer exists.
/// </summary>
/// <remarks>
/// The validator depends only on <see cref="IProviderCatalog"/>, which aggregates the cheap, options-free
/// <see cref="ProviderDescriptor"/>s, never the adapters. This is essential: an
/// <see cref="IValidateOptions{TOptions}"/> for <see cref="ProxyOptions"/> runs while the options graph is being
/// materialized, so depending on the adapters (which themselves require <c>IOptions&lt;ProxyOptions&gt;</c>)
/// would re-enter that materialization. Reading descriptors only keeps validation acyclic. It is registered
/// exclusively on the inner proxy host, whose options binding is fail-fast; the chassis binds the same options
/// tolerantly and must not run this rule.
/// </remarks>
sealed class ProviderTypeValidateOptions : IValidateOptions<ProxyOptions>
{
	private readonly IProviderCatalog mCatalog;

	/// <summary>
	/// Initializes a new instance of the <see cref="ProviderTypeValidateOptions"/> class.
	/// </summary>
	/// <param name="catalog">The provider catalog whose registered descriptors define the supported types.</param>
	/// <exception cref="ArgumentNullException"><paramref name="catalog"/> is <see langword="null"/>.</exception>
	public ProviderTypeValidateOptions(IProviderCatalog catalog)
	{
		ArgumentNullException.ThrowIfNull(catalog);

		mCatalog = catalog;
	}

	/// <inheritdoc/>
	public ValidateOptionsResult Validate(string? name, ProxyOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);

		List<string> failures = [];

		// The neutral default the configuration layer falls back to must itself resolve to a registered provider.
		// This is the guard that makes Option A's single duplicated literal safe: if the OpenAI adapter is ever
		// renamed or dropped, a backend that configures no provider type would silently default to a dead type,
		// caught here at startup instead.
		if (!mCatalog.IsSupported(BackendOptions.DefaultProviderType))
		{
			failures.Add(
				$"The default provider type '{BackendOptions.DefaultProviderType}' does not resolve to a registered " +
				"provider. A provider adapter for it must be registered.");
		}

		// Every backend that pins a provider type must name one the proxy ships; an unsupported type would later
		// fail adapter resolution at routing time, so it is rejected at startup instead.
		foreach ((string backendName, BackendOptions backend) in options.Backends)
		{
			if (!mCatalog.IsSupported(backend.ProviderType))
			{
				failures.Add(
					$"Backend '{backendName}' uses provider type '{backend.ProviderType}', which is not supported. " +
					$"Supported provider types: {string.Join(", ", SupportedTypeList())}.");
			}
		}

		return failures.Count == 0
			       ? ValidateOptionsResult.Success
			       : ValidateOptionsResult.Fail(failures);
	}

	/// <summary>
	/// Lists the registered provider types for an actionable failure message, so the operator sees exactly which
	/// values are valid rather than just that theirs is not.
	/// </summary>
	/// <returns>The registered provider-type discriminators, in registration order.</returns>
	private IEnumerable<string> SupportedTypeList() => mCatalog.Providers.Select(descriptor => descriptor.ProviderType);
}
