// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Core;

namespace OllamaProxy.Hosting.Cascade;

/// <summary>
/// Supervises the recyclable inner proxy host beneath the stable outer chassis. As the chassis's hosted
/// service it builds and starts the inner host during startup and stops it during shutdown; between those it
/// can rebuild the host from a fresh configuration snapshot and atomically swap the live instance, so the proxy
/// is reconfigured without terminating the process or dropping the Service Control Manager's contact.
/// </summary>
interface IProxyHostSupervisor : IHostedService
{
	/// <summary>
	/// Gets a value indicating whether the inner proxy host is currently active and serving: that is, a host
	/// has been started and has since been neither stopped nor discarded by a failed start.
	/// </summary>
	/// <remarks>
	/// This is the readiness signal the chassis surfaces (for example through a <c>/ready</c> probe), and it is
	/// independent of the chassis's own liveness: under the daemon policy the chassis stays up (and a liveness
	/// probe keeps answering) even when a failed inner-host start leaves no host active. The value is read
	/// without taking the lifecycle gate, so during a successful recycle's swap it can momentarily report
	/// <see langword="false"/> for the brief unbound window between retiring the old host and starting the new
	/// one.
	/// </remarks>
	bool IsInnerHostActive { get; }

	/// <summary>
	/// Reads the live, client-facing model catalog from the currently active inner proxy host: the exact set
	/// of models the proxy is serving right now, with their resolved capabilities, context windows, backends,
	/// and pinned reasoning effort. This is how the chassis-hosted admin surface observes what the running
	/// proxy actually offers without crossing back out over HTTP: it resolves the inner host's
	/// <see cref="IModelRouter"/> in process, so it reflects the live configuration (collisions, prefixes, and
	/// shadowing already resolved) rather than a recomputation.
	/// </summary>
	/// <returns>
	/// The live catalog snapshot (a name-sorted, immutable list safe to read concurrently), or
	/// <see langword="null"/> when no inner host is currently active, for example during the brief unbound
	/// window of a recycle, or under the daemon policy after a failed start. A <see langword="null"/> result is
	/// the "proxy is not serving" signal, distinct from an empty-but-non-null catalog of a proxy serving no
	/// models.
	/// </returns>
	IReadOnlyList<RegisteredModel>? GetLiveModels();

	/// <summary>
	/// Rebuilds the inner proxy host from the current configuration, validates it with a dry-run start on a
	/// non-binding server, and, only if that succeeds, stops the active host and activates the new one.
	/// </summary>
	/// <param name="cancellationToken">A token to cancel the operation.</param>
	/// <returns>
	/// A <see cref="RecycleResult"/> describing the outcome: success when the new host became active, or failure
	/// carrying the validation errors that caused the candidate to be rejected.
	/// </returns>
	/// <remarks>
	/// When dry-run validation fails the active host is left untouched and keeps serving, so a rejected recycle
	/// never interrupts traffic. Because the inner host binds a fixed port, activating a validated candidate
	/// requires releasing the previous host first, which opens a brief unbound window on the proxy port; in the
	/// rare case the validated host then fails to bind, the proxy can be left offline until the next successful
	/// recycle.
	/// </remarks>
	Task<RecycleResult> RecycleAsync(CancellationToken cancellationToken);
}
