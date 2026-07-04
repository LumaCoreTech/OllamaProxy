// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.ComponentModel.DataAnnotations;

namespace OllamaProxy.Configuration;

/// <summary>
/// Registration helpers that bind and validate the proxy's options graph. Validation runs at startup
/// (fail-fast) so a misconfigured deployment surfaces immediately rather than on first request.
/// </summary>
static class ConfigurationServiceCollectionExtensions
{
	/// <summary>
	/// Binds the <see cref="ProxyOptions.SectionName"/> configuration section to
	/// <see cref="ProxyOptions"/>, enabling data-annotation and <see cref="IValidatableObject"/>
	/// validation that is enforced when the host starts.
	/// </summary>
	/// <param name="services">The service collection to add the options registration to.</param>
	/// <returns>The same <paramref name="services"/> instance, to support call chaining.</returns>
	public static IServiceCollection AddProxyOptions(this IServiceCollection services)
	{
		ArgumentNullException.ThrowIfNull(services);

		services.AddOptions<ProxyOptions>()
			.BindConfiguration(ProxyOptions.SectionName)
			.ValidateDataAnnotations()
			.ValidateOnStart();

		return services;
	}
}
