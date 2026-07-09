// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Bunit;

using OllamaProxy.Admin.Editing;
using OllamaProxy.Admin.Reconciliation;
using OllamaProxy.Admin.Ui.Components.Backends;
using OllamaProxy.Configuration;
using OllamaProxy.Core;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Tests.Admin.Ui.Components.Backends;

public sealed partial class BackendModelsTests
{
	/// <summary>
	/// A single discovery candidate used only to satisfy the component's "a snapshot has loaded" gate
	/// (<see cref="ModelListState.Snapshot"/> is non-<see langword="null"/>). The visible table is driven by the
	/// separate <see cref="ReconciliationResult"/> parameter, so the candidate's own fields are never rendered.
	/// </summary>
	private static readonly DiscoveryCandidate SampleCandidate = new(
		ClientName: "sample",
		UpstreamModel: "sample",
		ReportedContextLength: null,
		Capabilities: null);

	/// <summary>
	/// Renders <see cref="BackendModels"/> with sensible defaults for every required parameter, so a test only
	/// supplies the inputs relevant to the branch it exercises. The four page-supplied closures
	/// (<see cref="BackendModels.IsDetailsExpanded"/>, <see cref="BackendModels.GetPinnedName"/>,
	/// <see cref="BackendModels.GetPinnedReasoning"/>, <see cref="BackendModels.GetContextOverride"/>) default to
	/// harmless implementations because the render path invokes them unconditionally for metadata-bearing and
	/// pinned rows; leaving them unset would throw a <see cref="NullReferenceException"/> during render.
	/// </summary>
	/// <param name="backend">The backend whose model list is rendered; defaults to an Explicit, unprefixed backend.</param>
	/// <param name="state">The model-list state; defaults to a loaded state with a non-null snapshot.</param>
	/// <param name="reconciliation">The reconciliation result to render, or <see langword="null"/> for the empty branch.</param>
	/// <param name="isBusy">Whether the owning page is busy, mirrored to every interactive control.</param>
	/// <param name="modeIgnoresPins">Whether the effective mode ignores pins (plug-and-play).</param>
	/// <param name="duplicateNames">The backend's duplicate client-facing names, or <see langword="null"/> for none.</param>
	/// <param name="isDetailsExpanded">The detail-panel predicate; defaults to "never expanded".</param>
	/// <param name="getPinnedName">The pinned-name accessor; defaults to the model's own <see cref="ReconciledModel.Name"/>.</param>
	/// <param name="getPinnedReasoning">The reasoning-override accessor; defaults to "inherit" (<see langword="null"/>).</param>
	/// <param name="getContextOverride">The context-override accessor; defaults to "inherit" (<see langword="null"/>).</param>
	/// <param name="configure">An optional hook to add event-callback parameters the test wants to observe.</param>
	/// <returns>The rendered <see cref="BackendModels"/> component.</returns>
	private IRenderedComponent<BackendModels> RenderModels(
		DesiredBackend?                                             backend            = null,
		ModelListState?                                             state              = null,
		ReconciliationResult?                                       reconciliation     = null,
		bool                                                        isBusy             = false,
		bool                                                        modeIgnoresPins    = false,
		IReadOnlySet<string>?                                       duplicateNames     = null,
		Func<ReconciledModel, bool>?                                isDetailsExpanded  = null,
		Func<ReconciledModel, string>?                              getPinnedName      = null,
		Func<ReconciledModel, ReasoningEffort?>?                    getPinnedReasoning = null,
		Func<ReconciledModel, int?>?                                getContextOverride = null,
		Action<ComponentParameterCollectionBuilder<BackendModels>>? configure          = null)
	{
		backend ??= CreateBackend();
		state ??= LoadedState();
		isDetailsExpanded ??= static _ => false;
		getPinnedName ??= static model => model.Name;
		getPinnedReasoning ??= static _ => null;
		getContextOverride ??= static _ => null;

		return Render<BackendModels>(parameters =>
		{
			parameters
				.Add(component => component.Backend, backend)
				.Add(component => component.State, state)
				.Add(component => component.Reconciliation, reconciliation)
				.Add(component => component.IsBusy, isBusy)
				.Add(component => component.ModeIgnoresPins, modeIgnoresPins)
				.Add(component => component.IsDetailsExpanded, isDetailsExpanded)
				.Add(component => component.GetPinnedName, getPinnedName)
				.Add(component => component.GetPinnedReasoning, getPinnedReasoning)
				.Add(component => component.GetContextOverride, getContextOverride);

			// Only override the duplicate-name set when a test provides one; the component defaults it to an empty
			// set, so leaving it unset is the correct "no duplicates" baseline.
			if (duplicateNames is not null)
			{
				parameters.Add(component => component.DuplicateModelNames, duplicateNames);
			}

			configure?.Invoke(parameters);
		});
	}

	/// <summary>
	/// Builds a <see cref="DesiredBackend"/> for the render fixtures, defaulting to an Explicit, unprefixed backend.
	/// </summary>
	/// <param name="modelPrefix">The backend's model prefix, or <see langword="null"/> for none.</param>
	/// <param name="mode">The backend's operating mode; defaults to <see cref="OperatingMode.Explicit"/>.</param>
	/// <returns>The configured backend.</returns>
	private static DesiredBackend CreateBackend(
		string?        modelPrefix = null,
		OperatingMode? mode        = OperatingMode.Explicit) => new()
	{
		Name = "primary",
		Options = new BackendOptions
		{
			BaseUrl = "http://localhost:11434",
			Mode = mode,
			ModelPrefix = modelPrefix
		}
	};

	/// <summary>
	/// The loaded state: a non-null snapshot with no in-flight fetch and no error, so the component renders the
	/// reconciliation table (or the "reports no models" placeholder when the result is empty).
	/// </summary>
	/// <returns>A loaded <see cref="ModelListState"/>.</returns>
	private static ModelListState LoadedState() => new(
		Snapshot: [SampleCandidate],
		IsFetching: false,
		Error: null);

	/// <summary>
	/// The fetching state before the first snapshot arrives: no snapshot, a fetch in flight, and no probe stream,
	/// which drives the "Fetching models…" placeholder and the "Fetching…" refresh label.
	/// </summary>
	/// <returns>A fetching <see cref="ModelListState"/>.</returns>
	private static ModelListState FetchingState() => new(
		Snapshot: null,
		IsFetching: true,
		Error: null);

	/// <summary>
	/// The streaming-probe state before the first row arrives: no snapshot yet, a fetch in flight, and the
	/// streaming flag set with a running resolved count, which drives the progress banner and the "Probing…" label.
	/// </summary>
	/// <returns>A streaming <see cref="ModelListState"/> with <see cref="ModelListState.ProbedCount"/> of 2.</returns>
	private static ModelListState StreamingState() => new(
		Snapshot: null,
		IsFetching: true,
		Error: null)
	{
		IsStreaming = true,
		ProbedCount = 2
	};

	/// <summary>
	/// Builds a reconciliation result from the supplied rows, preserving their order.
	/// </summary>
	/// <param name="models">The reconciled rows, in render order.</param>
	/// <returns>The reconciliation result.</returns>
	private static ReconciliationResult Reconciliation(params ReconciledModel[] models) => new(models);

	/// <summary>
	/// Builds a <see cref="ReconciledModelState.Discovered"/> (unpinned) row.
	/// </summary>
	/// <param name="name">The model's bare name and upstream id.</param>
	/// <param name="exposedName">The client-facing exposed name; defaults to <paramref name="name"/>.</param>
	/// <param name="capabilities">The resolved capabilities, or <see langword="null"/> when probing was skipped.</param>
	/// <param name="contextLength">The reported context window, or <see langword="null"/>.</param>
	/// <param name="isExposed">Whether the runtime catalog exposes this row (false only for Explicit-mode discoveries).</param>
	/// <param name="metadata">The backend-reported descriptive metadata, or <see langword="null"/> for none.</param>
	/// <returns>The discovered row.</returns>
	private static ReconciledModel DiscoveredModel(
		string                 name          = "llama3",
		string?                exposedName   = null,
		ModelCapabilities?     capabilities  = null,
		long?                  contextLength = 8192,
		bool                   isExposed     = true,
		ProviderModelMetadata? metadata      = null)
	{
		return new ReconciledModel(
			Name: name,
			ExposedName: exposedName ?? name,
			BackendName: "primary",
			UpstreamModel: name,
			Capabilities: capabilities,
			ContextLength: contextLength,
			State: ReconciledModelState.Discovered,
			IsExposed: isExposed,
			Metadata: metadata);
	}

	/// <summary>
	/// Builds an <see cref="ReconciledModelState.Available"/> pin. With no discovered facets it never drifts; supply
	/// <paramref name="discoveredCapabilities"/> / <paramref name="discoveredContextLength"/> to trigger drift.
	/// </summary>
	/// <param name="name">The model's bare name and upstream id.</param>
	/// <param name="capabilities">The pin's configured capabilities; defaults to completion-only.</param>
	/// <param name="contextLength">The pin's configured context window.</param>
	/// <param name="explicitContextOverride">Whether the context came from an explicit override (required for context drift).</param>
	/// <param name="discoveredCapabilities">The backend's currently reported capabilities, or <see langword="null"/>.</param>
	/// <param name="discoveredContextLength">The backend's currently reported context window, or <see langword="null"/>.</param>
	/// <param name="metadata">The backend-reported descriptive metadata, or <see langword="null"/> for none.</param>
	/// <returns>The available pin row.</returns>
	private static ReconciledModel AvailablePin(
		string                 name                    = "gpt-4",
		ModelCapabilities?     capabilities            = null,
		long?                  contextLength           = 8192,
		bool                   explicitContextOverride = false,
		ModelCapabilities?     discoveredCapabilities  = null,
		long?                  discoveredContextLength = null,
		ProviderModelMetadata? metadata                = null)
	{
		return new ReconciledModel(
			Name: name,
			ExposedName: name,
			BackendName: "primary",
			UpstreamModel: name,
			Capabilities: capabilities ?? Caps(completion: true, tools: false, vision: false, embeddings: false),
			ContextLength: contextLength,
			State: ReconciledModelState.Available,
			ExplicitContextOverride: explicitContextOverride,
			DiscoveredCapabilities: discoveredCapabilities,
			DiscoveredContextLength: discoveredContextLength,
			Metadata: metadata);
	}

	/// <summary>
	/// Builds an <see cref="ReconciledModelState.Unavailable"/> pin: a pinned model the backend no longer reports.
	/// </summary>
	/// <param name="name">The model's bare name and upstream id.</param>
	/// <returns>The unavailable pin row.</returns>
	private static ReconciledModel UnavailablePin(string name = "retired-model")
	{
		return new ReconciledModel(
			Name: name,
			ExposedName: name,
			BackendName: "primary",
			UpstreamModel: name,
			Capabilities: Caps(completion: true, tools: false, vision: false, embeddings: false),
			ContextLength: 4096,
			State: ReconciledModelState.Unavailable);
	}

	/// <summary>
	/// Builds an <see cref="ReconciledModelState.Available"/> pin that has drifted in its context window: an explicit
	/// override of 4,096 against a backend-reported window of 8,192, so <see cref="ReconciledModel.IsDrifted"/> is set.
	/// </summary>
	/// <param name="name">The model's bare name and upstream id.</param>
	/// <returns>The drifted pin row.</returns>
	private static ReconciledModel DriftedPin(string name = "drifted")
	{
		return AvailablePin(
			name: name,
			contextLength: 4096,
			explicitContextOverride: true,
			discoveredCapabilities: Caps(completion: true, tools: false, vision: false, embeddings: false),
			discoveredContextLength: 8192);
	}

	/// <summary>
	/// Builds a <see cref="ModelCapabilities"/> from the four functional flags, using an arbitrary provenance source
	/// that none of the rendered branches read.
	/// </summary>
	/// <param name="completion">Whether completion is supported.</param>
	/// <param name="tools">Whether tool calling is supported.</param>
	/// <param name="vision">Whether vision input is supported.</param>
	/// <param name="embeddings">Whether embeddings are supported.</param>
	/// <returns>The configured capabilities.</returns>
	private static ModelCapabilities Caps(
		bool completion,
		bool tools,
		bool vision,
		bool embeddings)
	{
		return new ModelCapabilities(
			completion,
			tools,
			vision,
			embeddings,
			CapabilitySource.ProviderMetadata);
	}
}
