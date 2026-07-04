// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Providers.Http;

/// <summary>
/// Produces the named-<see cref="System.Net.Http.HttpClient"/> keys used to register and resolve a
/// dedicated client per backend. Centralizing the naming scheme keeps registration and resolution in
/// lock-step and avoids leaking the convention across the codebase.
/// </summary>
static class BackendHttpClientNames
{
	/// <summary>
	/// The prefix applied to every backend client name to namespace it within the factory.
	/// </summary>
	private const string Prefix = "backend:";

	/// <summary>
	/// Builds the factory client name for the supplied logical backend.
	/// </summary>
	/// <param name="backendName">The logical backend name from configuration.</param>
	/// <returns>The named-client key to pass to the HTTP client factory.</returns>
	public static string ForBackend(string backendName) => Prefix + backendName;
}
