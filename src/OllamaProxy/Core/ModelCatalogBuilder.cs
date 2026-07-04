// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.Options;

using OllamaProxy.Configuration;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Core;

/// <summary>
/// Assembles the model catalog at startup by combining each backend's effective operating mode with
/// live discovery. Every backend's mode is resolved through <c>IProviderCatalog.ResolveMode</c>, which
/// selects how that backend's nested registry and its discovered models combine: in
/// <see cref="OperatingMode.PlugAndPlay"/> the backend's registry is ignored and every discovered model
/// is exposed; in <see cref="OperatingMode.Hybrid"/> the backend's registry entries are honored (and win
/// on name collision) alongside its discovered models; in <see cref="OperatingMode.Explicit"/> only the
/// backend's registry entries are exposed and it runs no discovery. Registry entries are materialized
/// across all backends first so they take precedence over any discovered model of the same name. A
/// backend that fails discovery is logged and skipped so one unreachable backend cannot prevent the proxy
/// from starting with the others.
/// </summary>
// All logging here runs once during startup discovery (per backend / per discovered model), so the
// LoggerMessage delegate ceremony (CA1848) and the lazy-evaluation guard (CA1873) buy nothing.
[SuppressMessage(
	"Performance",
	"CA1848:Use the LoggerMessage delegates",
	Justification = "Startup-only discovery logging; the LoggerMessage delegate ceremony is not worth it here.")]
[SuppressMessage(
	"Performance",
	"CA1873:Avoid potentially expensive logging",
	Justification = "Startup-only discovery logging with already-materialized arguments.")]
sealed class ModelCatalogBuilder
{
	// The discovery orchestration is a stateless, pure pipeline shared with the admin fetch surface; the
	// catalog builder owns it directly rather than via DI because every test deliberately exercises the real
	// discover-then-probe path against stub adapters, so a substitutable seam here would add churn, not value.
	private readonly BackendModelDiscovery        mDiscovery = new();
	private readonly ILogger<ModelCatalogBuilder> mLogger;
	private readonly IOptions<ProxyOptions>       mOptions;
	private readonly IProviderResolver            mProviderResolver;
	private readonly IProviderCatalog             mProviderCatalog;

	/// <summary>
	/// Initializes a new instance of the <see cref="ModelCatalogBuilder"/> class.
	/// </summary>
	/// <param name="options">The validated proxy options carrying the backends, each with its own mode and registry.</param>
	/// <param name="providerResolver">Resolves each backend to its adapter for discovery calls.</param>
	/// <param name="providerCatalog">Resolves each backend's effective operating mode from its provider-aware default.</param>
	/// <param name="logger">Records discovery progress and per-backend failures.</param>
	/// <exception cref="ArgumentNullException">
	/// Any of <paramref name="options"/>, <paramref name="providerResolver"/>, <paramref name="providerCatalog"/>,
	/// or <paramref name="logger"/> is <see langword="null"/>.
	/// </exception>
	public ModelCatalogBuilder(
		IOptions<ProxyOptions>       options,
		IProviderResolver            providerResolver,
		IProviderCatalog             providerCatalog,
		ILogger<ModelCatalogBuilder> logger)
	{
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(providerResolver);
		ArgumentNullException.ThrowIfNull(providerCatalog);
		ArgumentNullException.ThrowIfNull(logger);

		mOptions = options;
		mProviderResolver = providerResolver;
		mProviderCatalog = providerCatalog;
		mLogger = logger;
	}

	/// <summary>
	/// Builds the full catalog by resolving each backend's effective mode. Discovery runs <em>first</em>, so a
	/// backend in <see cref="OperatingMode.Hybrid"/> can let its registry pins inherit the context window their
	/// backend currently reports, exactly as the admin preview resolves them. Registry entries from every
	/// backend in <see cref="OperatingMode.Hybrid"/> or <see cref="OperatingMode.Explicit"/> are then
	/// materialized before discovered models are merged, so they take precedence over any discovered model of
	/// the same name. A backend in <see cref="OperatingMode.PlugAndPlay"/> with configured registry entries has
	/// them ignored, which is logged as a warning so the skipped configuration is never silent.
	/// </summary>
	/// <param name="cancellationToken">A token to cancel discovery.</param>
	/// <returns>The resolved models that make up the catalog.</returns>
	/// <exception cref="InvalidOperationException">
	/// A registry entry references a backend that neither reports a context length nor specifies one
	/// itself. This is a configuration error the proxy surfaces at startup rather than silently dropping a
	/// model the operator asked for.
	/// </exception>
	public async Task<IReadOnlyList<RegisteredModel>> BuildAsync(CancellationToken cancellationToken)
	{
		ProxyOptions options = mOptions.Value;

		Dictionary<string, RegisteredModel> catalog = new(StringComparer.OrdinalIgnoreCase);

		// Phase 1: discover every auto-exposed backend in parallel, BEFORE materializing the registry. A Hybrid
		// backend's pins must be able to inherit the window their backend currently reports, exactly as the
		// admin preview resolves them, so discovery has to run first; otherwise a pin without an explicit
		// override would be capped at the backend default, or fail outright when no default is set.
		IReadOnlyList<DiscoveredBackend> discovered =
			await DiscoverAllAsync(options, cancellationToken).ConfigureAwait(false);

		Dictionary<string, IReadOnlyList<DiscoveryCandidate>> candidatesByBackend =
			discovered.ToDictionary(
				entry => entry.BackendName,
				entry => entry.Candidates,
				StringComparer.OrdinalIgnoreCase);

		// Phase 2: materialize every backend's registry, across all backends, so a pinned model always wins a
		// name collision against a later auto-exposed model, regardless of which backend exposes it. PlugAndPlay
		// backends deliberately ignore their registry (honoring pins would make PlugAndPlay indistinguishable
		// from Hybrid); a non-empty registry there is surfaced as a startup warning.
		//
		// Both Hybrid and Explicit backends benefit from discovery metadata enrichment: Hybrid pins inherit
		// the backend's reported context window, while all pins (Hybrid and Explicit) inherit provider metadata
		// (CreatedAtUtc, Metadata) when the listing supplies it. Explicit backends run metadata-only discovery
		// (NeverProbe), so their candidates carry provider metadata but no capability probes.
		foreach ((string backendName, BackendOptions backend) in options.Backends)
		{
			if (mProviderCatalog.ResolveMode(backend) == OperatingMode.PlugAndPlay)
			{
				if (backend.Models.Count > 0)
				{
					mLogger.LogWarning(
						"Backend {Backend} is in PlugAndPlay mode and does not honor its model registry; " +
						"{Count} configured model(s) will be ignored. This mode exposes every discovered model. " +
						"Switch the backend to Hybrid mode to pin or override models while still auto-exposing it.",
						backendName,
						backend.Models.Count);
				}

				continue;
			}

			// Index the backend's discovered candidates by upstream model id. Hybrid pins use this to inherit
			// the reported context window; all pins (Hybrid and Explicit) use it to inherit provider metadata
			// (CreatedAtUtc, Metadata). The match mirrors ModelReconciler (ordinal upstream id, first candidate wins).
			IReadOnlyDictionary<string, DiscoveryCandidate> discoveredByUpstream =
				IndexDiscoveredCandidates(candidatesByBackend.GetValueOrDefault(backendName) ?? []);

			foreach (ModelRegistrationOptions registration in backend.Models)
			{
				DiscoveryCandidate? matchedCandidate =
					discoveredByUpstream.GetValueOrDefault(registration.ResolveUpstreamModel());
				RegisteredModel model = BuildFromRegistration(registration, backendName, backend, matchedCandidate);
				catalog[model.Name] = model;
				mLogger.LogInformation(
					"Registered model {Model} -> backend {Backend} (upstream {Upstream}).",
					model.Name,
					model.BackendName,
					model.UpstreamModel);
			}
		}

		// Captured after registry materialization so a collision can tell an expected registry win apart from a
		// silent clash between two auto-exposed backends.
		HashSet<string> registryNames = new(catalog.Keys, StringComparer.OrdinalIgnoreCase);

		// Phase 3: merge discovered candidates into the catalog, in configured backend order, without
		// overwriting registry entries. The sequential merge keeps name-collision resolution and logging
		// deterministic regardless of which backend's probes completed first. Metadata-only discoveries
		// (from Explicit backends) are skipped here: they only enrich registry pins in Phase 2.
		foreach (DiscoveredBackend backendDiscovery in discovered)
		{
			if (backendDiscovery.IsMetadataOnly)
				continue;

			MergeCandidates(
				catalog,
				registryNames,
				backendDiscovery.BackendName,
				backendDiscovery.Backend,
				backendDiscovery.Candidates);
		}

		mLogger.LogInformation("Model catalog assembled with {Count} model(s).", catalog.Count);

		return catalog.Values.ToArray();
	}

	/// <summary>
	/// Runs discovery against every backend, returning each backend's ordered candidate list <em>without</em>
	/// touching the catalog. Hybrid and PlugAndPlay backends run full discovery with
	/// <see cref="DiscoveryProbePolicy.SkipContextless"/> probing; Explicit backends run metadata-only discovery
	/// with <see cref="DiscoveryProbePolicy.NeverProbe"/>: the listing enriches registry pins with provider
	/// metadata (CreatedAtUtc, description, pricing) but unpinned models are not auto-exposed. Backends are
	/// discovered and probed concurrently so the cold start of many backends overlaps; the results are returned
	/// in configured backend order so the caller's registry materialization and candidate merge stay deterministic
	/// regardless of which backend's probes complete first. Each backend is processed independently so a failure
	/// isolates to that backend.
	/// </summary>
	/// <param name="options">The active proxy options.</param>
	/// <param name="cancellationToken">A token to cancel discovery.</param>
	/// <returns>The discovered candidates per backend, in configured backend order.</returns>
	private async Task<IReadOnlyList<DiscoveredBackend>> DiscoverAllAsync(
		ProxyOptions      options,
		CancellationToken cancellationToken)
	{
		// Discover every backend in parallel. Hybrid and PlugAndPlay run full discovery (SkipContextless probing,
		// candidates are auto-exposed). Explicit runs metadata-only discovery (NeverProbe, candidates enrich
		// registry pins but are never auto-exposed). Each backend yields an ordered candidate list; the probe
		// I/O, and of all models within a backend (bounded by MaxConcurrentProbes), overlaps instead of
		// running serially.
		List<(string BackendName, BackendOptions Backend, Task<IReadOnlyList<DiscoveryCandidate>> Candidates, bool
			IsMetadataOnly)> pending = [];

		foreach ((string backendName, BackendOptions backend) in options.Backends)
		{
			bool isExplicit = mProviderCatalog.ResolveMode(backend) == OperatingMode.Explicit;

			// Explicit backends: metadata-only. The listing enriches registry pins with provider metadata
			// (CreatedAtUtc, description, pricing) but unpinned models are never auto-exposed. NeverProbe
			// skips all capability probes since Explicit pins already have their capabilities configured.
			// Hybrid and PlugAndPlay: full discovery with SkipContextless probing. Candidates are both
			// auto-exposed and available for pin enrichment.
			pending.Add(
				(
					backendName,
					backend,
					DiscoverBackendCandidatesAsync(
						backendName,
						backend,
						isExplicit ? DiscoveryProbePolicy.NeverProbe : DiscoveryProbePolicy.SkipContextless,
						cancellationToken),
					isExplicit));
		}

		await Task.WhenAll(pending.Select(entry => entry.Candidates)).ConfigureAwait(false);

		// Materialize the results in configured backend order so the caller's downstream phases (registry
		// inheritance, collision resolution, logging) are deterministic regardless of probe completion order.
		return
		[
			.. pending.Select(entry =>
				new DiscoveredBackend(entry.BackendName, entry.Backend, entry.Candidates.Result, entry.IsMetadataOnly))
		];
	}

	/// <summary>
	/// Indexes a backend's discovered candidates by their upstream model identifier, mapping each to the full
	/// candidate record. This lets registry pins (both Hybrid and Explicit) inherit data from the backend's
	/// listing: Hybrid pins inherit the reported context window (matched by upstream id), while all pins
	/// (Hybrid and Explicit) can inherit provider metadata (CreatedAtUtc, Metadata) when the listing
	/// supplies it. The match uses the same ordinal upstream id the admin reconciliation uses, with the
	/// first candidate winning on the rare duplicate id (discovery already de-duplicates by client-facing name,
	/// so a clash here would only be a backend listing the same upstream id twice).
	/// </summary>
	/// <param name="candidates">The backend's discovered candidates, or an empty list when no discovery ran.</param>
	/// <returns>A map from upstream model id to its full discovery candidate (which may carry partial data).</returns>
	private static Dictionary<string, DiscoveryCandidate> IndexDiscoveredCandidates(
		IReadOnlyList<DiscoveryCandidate> candidates)
	{
		Dictionary<string, DiscoveryCandidate> discoveredByUpstream = new(StringComparer.Ordinal);
		foreach (DiscoveryCandidate candidate in candidates)
		{
			discoveredByUpstream.TryAdd(candidate.UpstreamModel, candidate);
		}

		return discoveredByUpstream;
	}

	/// <summary>
	/// Discovers a single backend's models through the shared <see cref="IBackendModelDiscovery"/>,
	/// producing an ordered list of candidates for the deterministic merge phase. Discovery runs under
	/// the supplied <paramref name="probePolicy"/>: typically <see cref="DiscoveryProbePolicy.SkipContextless"/>
	/// for full discovery (the merge drops a window-less model anyway, so probing one would waste upstream
	/// round trips) or <see cref="DiscoveryProbePolicy.NeverProbe"/> for metadata-only enrichment
	/// (Explicit backends only need provider metadata, not capability probes). Any failure (whether
	/// listing the models or probing their capabilities) is logged and yields an empty list, so one
	/// unreachable or misbehaving backend never blocks startup for the others.
	/// </summary>
	/// <param name="backendName">The logical backend to discover.</param>
	/// <param name="backend">The configured options for the backend, carrying any context-length default.</param>
	/// <param name="probePolicy">The capability-probing policy to apply.</param>
	/// <param name="cancellationToken">A token to cancel discovery.</param>
	/// <returns>The ordered candidates discovered for the backend, or an empty list when discovery failed.</returns>
	private async Task<IReadOnlyList<DiscoveryCandidate>> DiscoverBackendCandidatesAsync(
		string               backendName,
		BackendOptions       backend,
		DiscoveryProbePolicy probePolicy,
		CancellationToken    cancellationToken)
	{
		ResolvedBackend resolved = mProviderResolver.Resolve(backendName);

		try
		{
			return await mDiscovery
				       .DiscoverAsync(resolved, backend, probePolicy, cancellationToken)
				       .ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			mLogger.LogWarning(
				exception,
				"Discovery failed for backend {Backend}; its models will not be exposed.",
				backendName);
			return [];
		}
	}

	/// <summary>
	/// Merges a backend's resolved candidates into <paramref name="catalog"/> without overwriting
	/// registry entries, applying the collision, context-window and usable-capability rules along with
	/// their logging. The candidate carries the backend's <em>raw</em>
	/// reported window; this is where the client-facing <em>effective</em> window is computed, falling back
	/// to <paramref name="backend"/>'s configured default via
	/// <see cref="ModelExposureRules.ResolveEffectiveContextWindow"/>, so a backend that reports nothing can
	/// still expose a model through its default. Runs single-threaded during the merge phase, so the catalog
	/// mutation needs no synchronization.
	/// </summary>
	/// <param name="catalog">The catalog being assembled.</param>
	/// <param name="registryNames">The names claimed by explicit registry entries, for collision diagnostics.</param>
	/// <param name="backendName">The logical backend the candidates were discovered from.</param>
	/// <param name="backend">The backend's options, supplying the context-length fallback for exposed models.</param>
	/// <param name="candidates">The ordered candidates resolved for the backend.</param>
	private void MergeCandidates(
		Dictionary<string, RegisteredModel> catalog,
		HashSet<string>                     registryNames,
		string                              backendName,
		BackendOptions                      backend,
		IReadOnlyList<DiscoveryCandidate>   candidates)
	{
		foreach (DiscoveryCandidate candidate in candidates)
		{
			string clientName = candidate.ClientName;

			// The name is already taken: either by an explicit registry entry (expected, it wins by
			// design) or by an earlier auto-exposed backend (a silent collision worth warning about,
			// since the client has no way to reach this shadowed model under the same name).
			if (catalog.TryGetValue(clientName, out RegisteredModel? existing))
			{
				if (registryNames.Contains(clientName))
				{
					// A registry pin wins routing/capabilities/context, but discovery may carry metadata the pin
					// lacks (CreatedAtUtc, provider metadata like description/pricing). When the discovery candidate
					// is from the same backend that owns the pin, enrich the pin with those discovery fields so the
					// admin surface shows the richest picture without changing routing semantics.
					if (string.Equals(existing.BackendName, backendName, StringComparison.OrdinalIgnoreCase) &&
					    (candidate.CreatedAtUtc is not null || candidate.Metadata is not null))
					{
						catalog[clientName] = existing with
						{
							CreatedAtUtc = candidate.CreatedAtUtc ?? existing.CreatedAtUtc,
							Metadata = candidate.Metadata ?? existing.Metadata
						};
					}

					mLogger.LogInformation(
						"Skipping discovered model {Model} on backend {Backend}: a registry entry already pins this name.",
						clientName,
						backendName);
				}
				else
				{
					mLogger.LogWarning(
						"Model name collision: {Model} is already exposed by backend {Owner}, so the copy on " +
						"backend {Backend} is not exposed and is unreachable under this name. Set a distinct " +
						"'ModelPrefix' on one of the backends, or pin distinct names via the model registry, " +
						"to serve both.",
						clientName,
						existing.BackendName,
						backendName);
				}

				continue;
			}

			// A discovered model whose context window is unknown is skipped rather than fatal: the backend
			// reported none and the operator configured no default, so the proxy cannot advertise or enforce a
			// correct window. The candidate carries the backend's raw reported value; the effective window
			// computed here falls back to the backend default, so a backend that reports nothing can still
			// expose the model through its configured default. An auto-exposed model has no per-model override
			// (those live in the registry), so the override term is null. Crashing the whole catalog over one
			// silent backend would be the wrong trade: the operator can recover by setting a backend or registry
			// 'ContextLength'.
			long? effectiveContext = ModelExposureRules.ResolveEffectiveContextWindow(
				explicitOverride: null,
				reported: candidate.ReportedContextLength,
				backendDefault: backend.ContextLength);

			if (effectiveContext is not { } window)
			{
				// ReSharper disable DuplicateItemInLoggerTemplate
				mLogger.LogWarning(
					"Skipping discovered model {Model} on backend {Backend}: the backend did not report a " +
					"context length and none is configured, so the proxy cannot advertise or enforce a " +
					"context window. Set 'OllamaProxy:Backends:{Backend}:ContextLength' as a backend default, " +
					"or pin this model under 'OllamaProxy:Backends:{Backend}:Models' with an explicit " +
					"'ContextLength', to expose it.",
					clientName,
					backendName,
					backendName,
					backendName);
				// ReSharper restore DuplicateItemInLoggerTemplate

				continue;
			}

			// Capabilities are always populated when a context window resolved: the catalog runs discovery under
			// DiscoveryProbePolicy.SkipContextless, so a context-less model is never probed and was already
			// handled by the branch above.
			ModelCapabilities capabilities = candidate.Capabilities!;

			// A model that can neither chat nor embed has no usable Ollama surface: the native API
			// exposes only completion and embedding endpoints, with no route for generation-only models
			// (e.g. image generation). Skipping it keeps such models out of the client's model picker
			// instead of advertising a model that every Ollama request would reject. This is an expected
			// outcome, not a fault, so it is logged at Information.
			if (capabilities is { SupportsCompletion: false, SupportsEmbeddings: false })
			{
				mLogger.LogInformation(
					"Skipping discovered model {Model} on backend {Backend}: it supports neither completion " +
					"nor embeddings (capabilities from {Source}), so it has no usable Ollama endpoint and is " +
					"not exposed.",
					clientName,
					backendName,
					capabilities.Source);

				continue;
			}

			catalog[clientName] = new RegisteredModel(
				clientName,
				backendName,
				candidate.UpstreamModel,
				capabilities,
				window,
				ReasoningEffort: null,
				candidate.CreatedAtUtc,
				candidate.Metadata);

			mLogger.LogInformation(
				"Discovered model {Model} on backend {Backend} (upstream {Upstream}, capabilities from {Source}, context {Context}).",
				clientName,
				backendName,
				candidate.UpstreamModel,
				capabilities.Source,
				window);
		}
	}

	/// <summary>
	/// Materializes a registry entry into a <see cref="RegisteredModel"/>, applying any explicit
	/// capability overrides over a completion-capable baseline. An unset completion flag defaults to
	/// <see langword="true"/> (the proxy's baseline modality), while the additive tools, vision, and
	/// embeddings flags default to <see langword="false"/>. Any pinned flag marks the source
	/// <see cref="CapabilitySource.Configured"/>; otherwise the defaults apply because an explicit
	/// registry entry intentionally bypasses live detection. The context window follows the shared
	/// three-tier rule (<see cref="ModelExposureRules.ResolveEffectiveContextWindow"/>): the entry's explicit
	/// override wins, else the window the backend reported for this pin, else the backend default. The
	/// client-facing name is resolved through
	/// <see cref="ModelExposureRules.ApplyClientFacingPrefix"/> from the entry's bare
	/// <see cref="ModelRegistrationOptions.Name"/> and the backend's <see cref="BackendOptions.ModelPrefix"/>,
	/// exactly as a discovered model is named, so a registry entry and an auto-exposed model are prefixed
	/// identically; the upstream identifier the proxy requests stays unprefixed. When the backend's listing
	/// supplies provider metadata (CreatedAtUtc, Metadata), the pin inherits it so the admin surface shows
	/// the richest picture available.
	/// </summary>
	/// <param name="registration">The registry entry to materialize.</param>
	/// <param name="backendName">The logical name of the backend that owns the registry entry.</param>
	/// <param name="backend">The owning backend's options, used to read its context-length default and prefix.</param>
	/// <param name="discovered">
	/// The full discovery candidate for this pin's upstream id from the backend's listing, or
	/// <see langword="null"/> when no candidate was found. The candidate's
	/// <see cref="DiscoveryCandidate.ReportedContextLength"/> feeds the three-tier context window rule
	/// (Hybrid pins inherit the reported window), while
	/// <see cref="DiscoveryCandidate.CreatedAtUtc"/> and <see cref="DiscoveryCandidate.Metadata"/>
	/// enrich both Hybrid and Explicit pins with provider metadata.
	/// </param>
	/// <returns>The resolved model for the registry entry.</returns>
	private static RegisteredModel BuildFromRegistration(
		ModelRegistrationOptions registration,
		string                   backendName,
		BackendOptions           backend,
		DiscoveryCandidate?      discovered)
	{
		ModelCapabilities capabilities = ModelExposureRules.ResolveRegisteredCapabilities(registration);

		// A registry entry stores the bare model name; the client-facing name applies the backend prefix at
		// exposure exactly as a discovered model does, so the same model pinned and auto-exposed is named
		// identically. The upstream id requested from the backend stays unprefixed.
		string clientName = ModelExposureRules.ApplyClientFacingPrefix(backend.ModelPrefix, registration.Name);

		// The shared three-tier rule: an explicit per-model override wins, else the window the backend reported
		// for this pin during discovery (Hybrid), else the backend default. An Explicit backend's metadata-only
		// discovery may carry a reported window in the candidate, so the pin benefits from it too.
		long? contextLength = ModelExposureRules.ResolveEffectiveContextWindow(
			explicitOverride: registration.ContextLength,
			reported: discovered?.ReportedContextLength,
			backendDefault: backend.ContextLength);

		// Unlike a discovered model (which is skipped when its window is unknown), a registry entry is an
		// explicit operator promise: pinning a model without a resolvable context length is a configuration
		// error, so the proxy fails fast at startup rather than silently dropping a model the operator asked for.
		if (contextLength is not { } window)
		{
			throw new InvalidOperationException(
				$"Model '{registration.Name}' on backend '{backendName}' has no context length: the backend " +
				"reported none, the backend default is unset, and the registry entry does not specify one. Set " +
				$"'ContextLength' on the 'OllamaProxy:Backends:{backendName}:Models' entry, or " +
				$"'OllamaProxy:Backends:{backendName}:ContextLength' as a backend default, so the proxy can " +
				"advertise and enforce the correct context window.");
		}

		return new RegisteredModel(
			clientName,
			backendName,
			registration.ResolveUpstreamModel(),
			capabilities,
			window,
			registration.ReasoningEffort,
			discovered?.CreatedAtUtc,
			discovered?.Metadata);
	}

	/// <summary>
	/// One backend's discovery result: the logical backend name, its options, the ordered candidates it offered,
	/// and whether this discovery is metadata-only. Captured during the parallel discovery phase so the sequential
	/// registry-inheritance and candidate-merge phases can consume the same per-backend result without re-running
	/// discovery. Metadata-only discoveries (from Explicit backends) enrich registry pins with provider metadata
	/// but do not auto-expose unpinned models.
	/// </summary>
	/// <param name="BackendName">The logical backend the candidates were discovered from.</param>
	/// <param name="Backend">The backend's options, supplying the context-length fallback and model prefix.</param>
	/// <param name="Candidates">The ordered candidates the backend offered, already named and capability-resolved.</param>
	/// <param name="IsMetadataOnly">
	/// <see langword="true"/> when this discovery is metadata-only (from an Explicit backend): candidates enrich
	/// registry pins with provider metadata (CreatedAtUtc, Metadata) but are not auto-exposed.
	/// <see langword="false"/> when this is a full discovery (from Hybrid or PlugAndPlay backends): candidates are
	/// both auto-exposed and available for pin enrichment.
	/// </param>
	private sealed record DiscoveredBackend(
		string                            BackendName,
		BackendOptions                    Backend,
		IReadOnlyList<DiscoveryCandidate> Candidates,
		bool                              IsMetadataOnly = false);
}
