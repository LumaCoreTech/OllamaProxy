// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Admin.Editing;

/// <summary>
/// Validates the two <em>structural</em> properties that must hold before a <see cref="DesiredProxyState"/> can
/// be keyed by backend name: every backend has a non-blank name, and no two backends share a name (compared
/// case-insensitively, as the routing layer keys them). These are exactly the conditions the apply path throws
/// an <see cref="ArgumentException"/> for, so the editor consults this validator to block Apply client-side
/// rather than letting the call throw. Every other rule (URL shape, key length, provider support, per-model
/// rules) is a domain rule left to the recycle's dry-run, which reports it as a graceful rejection.
/// </summary>
/// <remarks>
/// The validation lives here as a pure function rather than in the editor page's code-behind so it can be
/// unit-tested without rendering the page. An empty draft is deliberately <em>not</em> a structural error — and
/// not an error at all: an empty backend set is a valid configuration (the proxy simply starts with no models
/// until a backend is added), so Apply stays enabled.
/// </remarks>
static class DesiredStateStructuralValidator
{
	/// <summary>
	/// Gets the structural problem that would make the desired state impossible to key by name, or
	/// <see langword="null"/> when the draft is structurally sound (or empty, which is not a structural error).
	/// </summary>
	/// <param name="state">The editor draft to validate, or <see langword="null"/> before the first load.</param>
	/// <returns>
	/// A human-readable message describing the first structural problem found (a blank backend name, otherwise
	/// duplicate backend names), or <see langword="null"/> when the draft is structurally sound, empty, or not
	/// yet loaded.
	/// </returns>
	public static string? Validate(DesiredProxyState? state)
	{
		if (state is null || state.Backends.Count == 0)
		{
			// An empty configuration is not a structural key collision, and an empty backend set is a valid state
			// in its own right (the proxy starts with no models until a backend is added), so Apply stays enabled.
			return null;
		}

		if (state.Backends.Any(backend => string.IsNullOrWhiteSpace(backend.Name)))
		{
			return "Every backend needs a non-blank name.";
		}

		List<string> duplicates = state.Backends
			.GroupBy(backend => backend.Name.Trim(), StringComparer.OrdinalIgnoreCase)
			.Where(group => group.Count() > 1)
			.Select(group => group.Key)
			.ToList();

		return duplicates.Count > 0
			       ? $"Backend names must be unique. Duplicated: {string.Join(", ", duplicates)}."
			       : null;
	}
}
