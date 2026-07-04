// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Tests.Integration.Live;

/// <summary>
/// The single point of access to the environment variables that drive the live provider-conformance suite.
/// It reads credentials and model ids by name and turns a set of required variables into a skip reason, so
/// the gating attributes (<see cref="LiveBackendFactAttribute"/>, <see cref="LiveBackendTheoryAttribute"/>)
/// and the per-backend configuration (<see cref="LiveBackendConfig"/>) share one consistent view of which
/// variables are present. Centralizing the access keeps the variable names and the blank-handling rule in a
/// single place rather than scattered across attributes and fixtures.
/// </summary>
static class LiveEnvironment
{
	/// <summary>
	/// Returns the trimmed value of the named environment variable, or <see langword="null"/> when it is
	/// unset or consists only of white-space. Treating blank as absent means a variable set to an empty
	/// string (a common CI artifact) gates exactly like an unset one.
	/// </summary>
	/// <param name="name">The environment variable name to read.</param>
	/// <returns>The trimmed value, or <see langword="null"/> when absent or blank.</returns>
	/// <exception cref="ArgumentException">
	/// <paramref name="name"/> is empty or consists only of white-space characters.
	/// </exception>
	public static string? Get(string name)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);

		string? value = Environment.GetEnvironmentVariable(name);
		return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
	}

	/// <summary>
	/// Returns the trimmed value of the named environment variable, falling back to
	/// <paramref name="fallback"/> when it is unset or blank.
	/// </summary>
	/// <param name="name">The environment variable name to read.</param>
	/// <param name="fallback">The value to use when the variable is absent or blank.</param>
	/// <returns>The resolved value.</returns>
	/// <exception cref="ArgumentException">
	/// <paramref name="name"/> is empty or consists only of white-space characters.
	/// </exception>
	public static string GetOrDefault(string name, string fallback) => Get(name) ?? fallback;

	/// <summary>
	/// Determines whether a live test should be skipped, returning a human-readable reason naming the first
	/// required variable that is absent or blank, or <see langword="null"/> when all are present so the test
	/// may run.
	/// </summary>
	/// <param name="requiredEnvironmentVariables">The environment variable names the test requires.</param>
	/// <returns>The skip reason, or <see langword="null"/> when the test may run.</returns>
	public static string? ResolveSkipReason(params string[] requiredEnvironmentVariables)
	{
		ArgumentNullException.ThrowIfNull(requiredEnvironmentVariables);

		foreach (string name in requiredEnvironmentVariables)
		{
			if (Get(name) is null)
			{
				return $"Live test skipped: environment variable '{name}' is not set. " +
				       "Set it to run the live provider-conformance suite against a real backend.";
			}
		}

		return null;
	}
}
