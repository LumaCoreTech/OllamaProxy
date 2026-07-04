// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Runtime.CompilerServices;

using OllamaProxy.Configuration;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Core;

/// <summary>
/// The default <see cref="IBackendModelDiscovery"/>. It is a pure orchestration over its inputs: it holds no
/// state and needs no collaborators beyond the <see cref="ResolvedBackend"/> handed to it, so it is safe to
/// share as a singleton across concurrent callers. The discover-then-resolve flow it owns was previously inlined
/// in <see cref="ModelCatalogBuilder"/>; extracting it lets the admin fetch reuse the identical path. It exposes
/// the flow in two shapes that share the same per-model resolution: <see cref="DiscoverAsync"/> buffers the
/// whole batch in reported order for the startup catalog, while <see cref="DiscoverStreamingAsync"/> yields each
/// candidate in client-name order (matching the admin table's sort) so the admin surface's incremental probe
/// fills the list top-to-bottom while still probing the models below the current row concurrently.
/// </summary>
sealed class BackendModelDiscovery : IBackendModelDiscovery
{
	/// <inheritdoc/>
	public async Task<IReadOnlyList<DiscoveryCandidate>> DiscoverAsync(
		ResolvedBackend      resolved,
		BackendOptions       backend,
		DiscoveryProbePolicy probePolicy,
		CancellationToken    cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(resolved);
		ArgumentNullException.ThrowIfNull(backend);

		IReadOnlyList<DiscoveredModel> discovered = await resolved.Adapter
			                                            .DiscoverModelsAsync(resolved.Context, cancellationToken)
			                                            .ConfigureAwait(false);

		// One gate per backend bounds this backend's concurrent probes without throttling other backends.
		// Probe limits are a per-backend concern (rate limits differ per provider). Task.WhenAll preserves
		// the input order, so the returned array keeps the discovered model order for a deterministic merge.
		using SemaphoreSlim gate = new(backend.Probing.MaxConcurrentProbes);

		return await Task
			       .WhenAll(
				       discovered.Select(model =>
					       ResolveCandidateAsync(
						       resolved,
						       backend,
						       model,
						       probePolicy,
						       // ReSharper disable once AccessToDisposedClosure
						       gate,
						       cancellationToken)))
			       .ConfigureAwait(false);
	}

	/// <inheritdoc/>
	public async IAsyncEnumerable<DiscoveryCandidate> DiscoverStreamingAsync(
		ResolvedBackend                            resolved,
		BackendOptions                             backend,
		DiscoveryProbePolicy                       probePolicy,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(resolved);
		ArgumentNullException.ThrowIfNull(backend);

		IReadOnlyList<DiscoveredModel> discovered = await resolved.Adapter
			                                            .DiscoverModelsAsync(resolved.Context, cancellationToken)
			                                            .ConfigureAwait(false);

		// Order the models by their client-facing name so the stream fills the admin table top-to-bottom in the
		// same order the UI shows it, instead of in the order each probe happens to finish. The admin surface
		// sorts the snapshot by client name (case-insensitively) before rendering, so matching that order here is
		// what makes the rows resolve from the top down rather than popping into place mid-list as each probe
		// settles. The prefix is applied uniformly, so this resolved name is exactly the key the surface sorts on.
		(DiscoveredModel Model, string ClientName)[] ordered = discovered
			.Select(model => (Model: model,
			                  ClientName: ModelExposureRules.ApplyClientFacingPrefix(backend.ModelPrefix, model.Id)))
			.OrderBy(static entry => entry.ClientName, StringComparer.OrdinalIgnoreCase)
			.ToArray();

		// Stop still-running probes when the enumeration ends early. The cause is a fault from an awaited
		// resolution, or the consumer breaking out of the await foreach (the operator navigated away or hit
		// Cancel). Without this an abandoned probe would keep hitting the backend after nobody is reading its
		// result. Linked to the caller's token so an external cancellation still flows through.
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

		// One gate per backend bounds this backend's concurrent probes (probe limits are a per-backend concern).
		// Every model's resolution is STARTED eagerly here, so up to MaxConcurrentProbes run at once, but they are
		// AWAITED in client-name order below. So a slow early model delays only the rows beneath it and the
		// stream still emits strictly top-to-bottom. The gate is NOT disposed in a using scope: many resolver
		// tasks hold it concurrently, and it must outlive them all, so it is left to the GC once every task has
		// settled (the finally awaits them). No wait handle is touched, so leaving it undisposed is safe.
		SemaphoreSlim gate = new(backend.Probing.MaxConcurrentProbes);
		var resolutions = new Task<DiscoveryCandidate>[ordered.Length];
		for (int i = 0; i < ordered.Length; i++)
		{
			resolutions[i] = ResolveCandidateAsync(
				resolved,
				backend,
				ordered[i].Model,
				probePolicy,
				gate,
				linked.Token);
		}

		try
		{
			// Emit in client-name order: await each model's resolution in turn and yield it. Because the tasks
			// were started eagerly above, the models below the current row keep probing while this one is awaited,
			// so the bounded concurrency is preserved while the visible order stays stable top-to-bottom. The
			// first resolution that faults throws here, completing the stream with that exception; candidates
			// yielded before it stay valid, exactly as the buffered path's failure contract.
			foreach (Task<DiscoveryCandidate> resolution in resolutions)
			{
				yield return await resolution.ConfigureAwait(false);
			}
		}
		finally
		{
			// The enumeration is ending: normally (every candidate yielded), by a fault (an awaited resolution
			// threw), or because the consumer stopped reading. Cancel any still-running probes and then observe
			// every task so a probe that faults after we stopped awaiting it does not surface as an unobserved
			// task exception. The first fault was already propagated to the consumer by the yield-await above, and
			// the cancellation faults from abandoned probes are expected, so both are swallowed here.
			await linked.CancelAsync().ConfigureAwait(false);
			try
			{
				await Task.WhenAll(resolutions).ConfigureAwait(false);
			}
			catch
			{
				// Intentionally ignored: see the comment above. The meaningful fault already reached the consumer.
			}
		}
	}

	/// <summary>
	/// Resolves a single discovered model into a <see cref="DiscoveryCandidate"/> by computing its client-facing
	/// name, carrying the backend's <em>raw</em> reported context window, and, when warranted by the
	/// <paramref name="probePolicy"/>, probing its capabilities under the supplied concurrency gate. The
	/// candidate carries the reported window verbatim so the admin surface can show the honest backend value; the
	/// effective window (reported value falling back to the backend default) is computed locally only to gate the
	/// probe decision. Under <see cref="DiscoveryProbePolicy.SkipContextless"/> a model whose <em>effective</em>
	/// window is unresolvable is not probed at all. It carries a <see langword="null"/> window and
	/// <see langword="null"/> capabilities, so a model exposable solely via the backend default is still probed.
	/// Under <see cref="DiscoveryProbePolicy.ProbeAll"/> the model is probed regardless, so its capabilities are
	/// resolved even when no window is reported. Under <see cref="DiscoveryProbePolicy.NeverProbe"/> no probe is
	/// issued: the capabilities are taken verbatim from the provider's listing (non-<see langword="null"/> for
	/// metadata-rich providers) or left <see langword="null"/> when the listing carried no signal.
	/// </summary>
	/// <param name="resolved">The resolved backend adapter and context used for the capability probe.</param>
	/// <param name="backend">The backend options supplying the model prefix and context-length default.</param>
	/// <param name="model">The discovered model to resolve.</param>
	/// <param name="probePolicy">Whether, and for which models, a capability probe is issued.</param>
	/// <param name="gate">The semaphore bounding concurrent probes for this backend.</param>
	/// <param name="cancellationToken">A token to cancel the probe.</param>
	/// <returns>The resolved candidate, carrying its capabilities when the policy required a probe.</returns>
	private static async Task<DiscoveryCandidate> ResolveCandidateAsync(
		ResolvedBackend      resolved,
		BackendOptions       backend,
		DiscoveredModel      model,
		DiscoveryProbePolicy probePolicy,
		SemaphoreSlim        gate,
		CancellationToken    cancellationToken)
	{
		// Auto-exposed models may carry a backend prefix to disambiguate the same model served by several
		// backends; the prefix changes only the client-facing name, never the upstream id.
		string clientName = ModelExposureRules.ApplyClientFacingPrefix(backend.ModelPrefix, model.Id);

		// The candidate carries the backend's reported window verbatim so the admin surface stays honest. The
		// effective window (reported value falling back to the configured default) is only needed to decide
		// whether SkipContextless should drop the probe, so it lives as a local and never reaches the candidate.
		// Discovery knows no per-model override (those live in the registry), so the override term is null here.
		long? effectiveContextLength = ModelExposureRules.ResolveEffectiveContextWindow(
			explicitOverride: null,
			reported: model.ContextLength,
			backendDefault: backend.ContextLength);

		// Under NeverProbe we never issue a round trip: carry whatever the provider's listing already supplied
		// (authoritative metadata for rich providers, null for poor ones). This is the non-blocking admin fetch
		// default. The operator opts into ProbeAll explicitly when they want the unknown capabilities resolved.
		if (probePolicy == DiscoveryProbePolicy.NeverProbe)
		{
			return new DiscoveryCandidate(
				clientName,
				model.Id,
				model.ContextLength,
				model.Capabilities,
				model.Created,
				model.Metadata);
		}

		// Under SkipContextless a model with no effective window is dropped by the catalog merge anyway, so
		// probing it would be wasted round trips. The gate keys on the effective window (default included) so a
		// model exposable solely via the backend default is still probed. Under ProbeAll (the admin fetch) we
		// always probe, because capabilities are independent of the context window and the operator wants the
		// real answer for every model on offer. The reported window stays null here because an unresolvable
		// effective window means the backend reported none either.
		if (effectiveContextLength is null && probePolicy == DiscoveryProbePolicy.SkipContextless)
		{
			return new DiscoveryCandidate(
				clientName,
				model.Id,
				ReportedContextLength: null,
				Capabilities: null,
				CreatedAtUtc: model.Created,
				model.Metadata);
		}

		await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			ModelCapabilities capabilities = await resolved.Adapter
				                                 .DetermineCapabilitiesAsync(resolved.Context, model, cancellationToken)
				                                 .ConfigureAwait(false);

			return new DiscoveryCandidate(
				clientName,
				model.Id,
				model.ContextLength,
				capabilities,
				model.Created,
				model.Metadata);
		}
		finally
		{
			gate.Release();
		}
	}
}
