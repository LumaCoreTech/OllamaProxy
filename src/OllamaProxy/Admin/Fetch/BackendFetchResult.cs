// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Core;

namespace OllamaProxy.Admin.Fetch;

/// <summary>
/// The outcome of fetching one backend's models for the admin surface: either the resolved
/// <see cref="Models"/> on success, or a classified <see cref="ErrorKind"/> with a human-readable
/// <see cref="ErrorMessage"/> on failure. A single backend's failure is captured as data rather than thrown.
/// That lets the admin service render a partial result (the backends that answered alongside the ones that did
/// not) instead of letting one unreachable backend blank the whole page.
/// </summary>
/// <param name="BackendName">
/// The configured backend name this result describes. Always populated, on both success and failure, so a
/// failed result is still attributable to its backend in the UI.
/// </param>
/// <param name="Succeeded">
/// <see langword="true"/> when the fetch completed and <see cref="Models"/> is populated;
/// <see langword="false"/> when it failed and <see cref="ErrorKind"/> and <see cref="ErrorMessage"/> are
/// populated instead. The discriminator that tells the two payload halves apart.
/// </param>
/// <param name="Models">
/// On success, the discovered models after the proxy's naming, context-window, and capability-resolution rules
/// were applied, in the backend's reported order; <see langword="null"/> on failure. Whether each model carries
/// resolved capabilities depends on the <see cref="DiscoveryProbePolicy"/> the fetch ran under. Under
/// <see cref="DiscoveryProbePolicy.ProbeAll"/> every model is probed. Under
/// <see cref="DiscoveryProbePolicy.NeverProbe"/> only the capabilities a provider already lists are present,
/// and the rest stay <see langword="null"/> until an explicit probe.
/// </param>
/// <param name="ErrorKind">
/// On failure, how far the failure could be attributed (credentials, upstream, or unknown);
/// <see langword="null"/> on success.
/// </param>
/// <param name="ErrorMessage">
/// On failure, a human-readable, English description of what went wrong; <see langword="null"/> on success.
/// </param>
public sealed record BackendFetchResult(
	string                             BackendName,
	bool                               Succeeded,
	IReadOnlyList<DiscoveryCandidate>? Models,
	BackendFetchErrorKind?             ErrorKind,
	string?                            ErrorMessage)
{
	/// <summary>
	/// Creates a successful result carrying the backend's resolved models.
	/// </summary>
	/// <param name="backendName">The configured backend name the fetch ran against.</param>
	/// <param name="models">The resolved models in the backend's reported order.</param>
	/// <returns>A result with <see cref="Succeeded"/> set and <see cref="Models"/> populated.</returns>
	/// <exception cref="ArgumentException">
	/// <paramref name="backendName"/> is <see langword="null"/>, empty, or white-space.
	/// </exception>
	/// <exception cref="ArgumentNullException"><paramref name="models"/> is <see langword="null"/>.</exception>
	public static BackendFetchResult Success(string backendName, IReadOnlyList<DiscoveryCandidate> models)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(backendName);
		ArgumentNullException.ThrowIfNull(models);

		return new BackendFetchResult(backendName, Succeeded: true, models, ErrorKind: null, ErrorMessage: null);
	}

	/// <summary>
	/// Creates a failed result carrying the classified error and its message.
	/// </summary>
	/// <param name="backendName">The configured backend name the fetch ran against.</param>
	/// <param name="errorKind">How far the failure could be attributed.</param>
	/// <param name="errorMessage">A human-readable, English description of the failure.</param>
	/// <returns>
	/// A result with <see cref="Succeeded"/> cleared and <see cref="ErrorKind"/> and <see cref="ErrorMessage"/>
	/// populated.
	/// </returns>
	/// <exception cref="ArgumentException">
	/// <paramref name="backendName"/> or <paramref name="errorMessage"/> is <see langword="null"/>, empty, or
	/// white-space.
	/// </exception>
	public static BackendFetchResult Failure(
		string                backendName,
		BackendFetchErrorKind errorKind,
		string                errorMessage)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(backendName);
		ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);

		return new BackendFetchResult(backendName, Succeeded: false, Models: null, errorKind, errorMessage);
	}
}
