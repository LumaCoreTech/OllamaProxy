// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Hosting.Cascade;

/// <summary>
/// The outcome of an inner-proxy-host recycle: whether the new configuration was activated and, when it was
/// not, the validation errors that caused the candidate to be rejected. On failure the previously active host
/// keeps serving uninterrupted, so a rejected recycle never takes the proxy offline.
/// </summary>
/// <param name="Success">
/// <see langword="true"/> when the candidate host validated and became the active host;
/// otherwise <see langword="false"/>.
/// </param>
/// <param name="ValidationErrors">
/// The reasons the candidate was rejected when <paramref name="Success"/> is <see langword="false"/>; empty on
/// success.
/// </param>
sealed record RecycleResult(bool Success, IReadOnlyList<string> ValidationErrors)
{
	/// <summary>
	/// A shared successful result carrying no validation errors.
	/// </summary>
	public static RecycleResult Succeeded { get; } = new(true, []);

	/// <summary>
	/// Creates a failed result carrying the validation errors that caused the candidate to be rejected.
	/// </summary>
	/// <param name="validationErrors">The reasons the candidate configuration was rejected.</param>
	/// <returns>A failed <see cref="RecycleResult"/> wrapping <paramref name="validationErrors"/>.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="validationErrors"/> is <see langword="null"/>.</exception>
	public static RecycleResult Failed(IReadOnlyList<string> validationErrors)
	{
		ArgumentNullException.ThrowIfNull(validationErrors);

		return new RecycleResult(false, validationErrors);
	}
}
