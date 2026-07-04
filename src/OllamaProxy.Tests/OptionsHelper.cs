// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Microsoft.Extensions.Options;

using OllamaProxy.Admin.Config;
using OllamaProxy.Hosting;

namespace OllamaProxy.Tests;

/// <summary>
/// Factory methods for <see cref="IOptions{TOptions}"/> instances used across tests. Centralizing these
/// keeps test setup short and makes the wrapped options object explicit.
/// </summary>
static class OptionsHelper
{
	/// <summary>
	/// Gets the default <see cref="AdminOptions"/> wrapped as <see cref="IOptions{AdminOptions}"/>.
	/// </summary>
	internal static IOptions<AdminOptions> DefaultAdminOptions { get; } =
		Options.Create(new AdminOptions());

	/// <summary>
	/// Creates <see cref="AdminOptions"/> with <see cref="AdminOptions.ApiKeyPersistencePolicy"/> set to the
	/// supplied value.
	/// </summary>
	/// <param name="policy">The persistence policy to set.</param>
	/// <returns>The wrapped options.</returns>
	internal static IOptions<AdminOptions> AdminOptionsWith(ApiKeyPersistencePolicy policy) =>
		Options.Create(new AdminOptions { ApiKeyPersistencePolicy = policy });
}
