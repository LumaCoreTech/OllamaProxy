// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Configuration;
using OllamaProxy.Hosting;

namespace OllamaProxy.Admin.Config;

/// <summary>
/// Persists a complete desired <see cref="ProxyOptions"/> state as the authoritative <c>OllamaProxy</c>
/// section of the operator configuration file. The write is whole-section and authoritative: the entire
/// <c>OllamaProxy</c> section is replaced by the desired state, so a backend or model the operator removed
/// is genuinely gone rather than lingering from a previous version. Every sibling section of the file
/// (<c>Logging</c>, <c>Kestrel</c>, <c>AllowedHosts</c>, and anything else the operator added) is
/// preserved untouched.
/// </summary>
/// <remarks>
///     <para>
///     <b>Secrets are the entered keys the desired state carries.</b> The admin configuration view is
///     file-only: it never merges environment variables (see
///     <see cref="Hosting.CascadeHostingExtensions.BuildProxyOptionsConfiguration"/>), so the
///     <see cref="BackendOptions.ApiKey"/> of every backend in the desired state is an operator-entered,
///     file-sourced value, not an environment-only secret. Under
///     <see cref="ApiKeyPersistencePolicy.WriteToFile"/> those keys are persisted verbatim, including for a
///     brand-new backend. This is leak-proof by construction: an environment-only secret is never present in
///     the file-only view the admin surface edits, so it can never reach the desired state to be written.
///     </para>
///     <para>
///     <b>Environment-only scrubbing.</b> Under <see cref="ApiKeyPersistencePolicy.EnvironmentOnly"/> every
///     backend's key is blanked instead of written, forcing the secret to be supplied through an environment
///     variable that only the running proxy reads. This is the single path that drops a key; it applies to
///     existing and new backends alike. The active policy is read from <see cref="AdminOptions"/> because it
///     is a deployment-level decision, not a per-apply choice.
///     </para>
///     <para>
///     <b>Comments are not preserved.</b> The file is rewritten from a parsed JSON model, so hand-written
///     <c>//</c> annotations in the live file are lost on the first write. The shipped, never-loaded
///     <c>appsettings.reference.json</c> is the durable documentation home for those annotations.
///     </para>
/// </remarks>
interface IProxyConfigWriter
{
	/// <summary>
	/// Writes <paramref name="desiredState"/> as the complete <c>OllamaProxy</c> section of the operator
	/// configuration file, applying the configured <see cref="AdminOptions.ApiKeyPersistencePolicy"/> to each
	/// backend's secret and preserving all sibling sections. The underlying write is atomic, so a concurrent
	/// reader never observes a partially written file. The most important such reader is the inner host
	/// rebuilding during a recycle.
	/// </summary>
	/// <param name="desiredState">
	/// The complete desired proxy configuration. Its backends' <see cref="BackendOptions.ApiKey"/> values are
	/// the operator-entered keys; under <see cref="ApiKeyPersistencePolicy.WriteToFile"/> they are persisted
	/// verbatim, under <see cref="ApiKeyPersistencePolicy.EnvironmentOnly"/> they are blanked.
	/// </param>
	/// <param name="cancellationToken">A token to cancel the operation.</param>
	/// <returns>A task that completes when the configuration has been durably written.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="desiredState"/> is <see langword="null"/>.</exception>
	/// <exception cref="System.Text.Json.JsonException">
	/// The existing file content is not valid JSON. The file is left untouched rather than overwritten, so a
	/// corrupt file never causes the sibling sections to be lost.
	/// </exception>
	/// <exception cref="IOException">
	/// The file could not be written (for example the directory is read-only or the disk is
	/// full).
	/// </exception>
	Task WriteAsync(
		ProxyOptions      desiredState,
		CancellationToken cancellationToken);
}
