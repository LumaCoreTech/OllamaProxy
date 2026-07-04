// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Configuration;
using OllamaProxy.Hosting;

namespace OllamaProxy.Admin.Config;

/// <summary>
/// Applies a proxy configuration change end to end: it persists the desired state through the
/// <see cref="IProxyConfigWriter"/> and then recycles the inner proxy host so the change takes effect,
/// reporting the combined outcome. It is the single entry point the admin surface calls to "save and
/// apply", so callers never write the file and trigger a recycle separately and the two stay consistent.
/// </summary>
/// <remarks>
///     <para>
///     <b>Validation lives in the recycle, not here.</b> The inner host's recycle rebuilds the proxy from
///     the freshly written file and dry-run validates it before swapping. The applier therefore must write
///     before it can validate, which means a rejected change has already touched disk by the time it is
///     known bad.
///     </para>
///     <para>
///     <b>Rollback keeps disk consistent.</b> A rejected configuration left on disk would arm a failure on
///     the next process restart, when the bad file is loaded directly with no dry-run to catch it. To avoid
///     that, the applier snapshots the previous file content and restores it on a rejected recycle. The net
///     effect is transactional from the operator's perspective: either the change is live and on disk, or the
///     previous configuration is live and on disk, never a rejected one.
///     </para>
///     <para>
///     <b>The API-key persistence policy is a deployment setting.</b> The active policy is read by the
///     writer from <see cref="AdminOptions"/> so every admin page that applies configuration uses the same
///     behavior; it is not a per-apply operator choice.
///     </para>
/// </remarks>
interface IProxyConfigApplier
{
	/// <summary>
	/// Persists <paramref name="desiredState"/> and recycles the inner host onto it, returning whether the
	/// change went live, was rejected and rolled back, or could not be written.
	/// </summary>
	/// <param name="desiredState">The complete desired proxy configuration to persist and activate.</param>
	/// <param name="cancellationToken">A token to cancel the operation.</param>
	/// <returns>
	/// An <see cref="ApplyResult"/> describing the outcome. On any non-success outcome the previously active
	/// configuration is still live and is what remains on disk.
	/// </returns>
	/// <exception cref="ArgumentNullException"><paramref name="desiredState"/> is <see langword="null"/>.</exception>
	Task<ApplyResult> ApplyAsync(
		ProxyOptions      desiredState,
		CancellationToken cancellationToken);
}
