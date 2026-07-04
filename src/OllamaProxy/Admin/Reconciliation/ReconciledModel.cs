// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Configuration;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Admin.Reconciliation;

/// <summary>
/// A single row in a <see cref="ReconciliationResult"/>: one model, its reconciled <see cref="State"/>, and
/// the display-ready capability and context information the admin surface needs to render it and offer a
/// pin/unpin action. Rows are produced by <see cref="ModelReconciler.Reconcile"/> in stable source order;
/// any sorting or grouping is left to the presentation layer.
/// </summary>
/// <remarks>
/// For an <see cref="ReconciledModelState.Available"/> pin the row also carries the backend's currently-reported
/// values (<see cref="DiscoveredCapabilities"/>, <see cref="DiscoveredContextLength"/>) alongside the pin's own
/// configured values. The surface can then highlight <em>drift</em>: a pin whose capabilities or context window
/// no longer match what the backend reports (see <see cref="IsDrifted"/>).
/// </remarks>
/// <param name="Name">
/// The bare model name: the identity the registry stores and the key pin/unpin edits match on. For a pinned
/// model this is the registry entry's <see cref="OllamaProxy.Configuration.ModelRegistrationOptions.Name"/>
/// verbatim; for a <see cref="ReconciledModelState.Discovered"/> model it is the upstream id (the bare form of
/// the candidate's client-facing name). It carries no backend prefix, so pinning a discovered model stores the
/// bare name and the runtime re-applies the prefix at exposure without double-prefixing. Use
/// <see cref="ExposedName"/> for display.
/// </param>
/// <param name="ExposedName">
/// The client-facing model name the catalog exposes: <see cref="Name"/> with the backend's
/// <see cref="OllamaProxy.Configuration.BackendOptions.ModelPrefix"/> applied (when one is set), matching how
/// the runtime catalog names the same model. Equal to <see cref="Name"/> for an unprefixed backend. This is the
/// value the admin surface displays; it is never used as an identity or match key.
/// </param>
/// <param name="BackendName">The logical backend the model belongs to.</param>
/// <param name="UpstreamModel">
/// The upstream model identifier the backend knows. This is the key the reconciliation matches on: a pin is
/// <see cref="ReconciledModelState.Available"/> exactly when a snapshot model shares this id.
/// </param>
/// <param name="Capabilities">
/// The capabilities to display. Always non-<see langword="null"/> for a pinned model, resolved from its
/// configured flags (completion defaults to <see langword="true"/> and the rest to <see langword="false"/>).
/// For a <see cref="ReconciledModelState.Discovered"/> model it carries whatever the discovery resolved.
/// Metadata-rich providers (e.g. OpenRouter, Venice) yield listed capabilities; metadata-poor ones (e.g. strict
/// OpenAI, vLLM) are actively probed during the fetch, so this is normally populated. It is
/// <see langword="null"/> only when discovery deliberately skipped probing: a model with no resolvable context
/// window fetched under a context-skipping probe policy, leaving its capabilities undetermined until a later
/// probe.
/// </param>
/// <param name="ContextLength">
/// The context window in tokens to display, or <see langword="null"/> when none could be resolved. For a
/// <see cref="ReconciledModelState.Discovered"/> row this is the backend's <em>raw</em> reported window with no
/// default applied, so the table never overstates what the backend offers. For a pin it is the entry's explicit
/// override when set; otherwise the backend's reported window, falling back to the backend default. The reported
/// value wins and the default only fills a gap, so a pin is never silently narrowed below what the backend
/// reports. A pin with no resolvable window is surfaced as <see langword="null"/> rather than rejected, so the
/// admin surface can flag it as needing a context length before commit.
/// </param>
/// <param name="State">The reconciled availability of the model relative to the latest snapshot.</param>
/// <param name="ExplicitContextOverride">
/// Whether this pin's <see cref="ContextLength"/> came from an explicit registry override rather than being
/// inherited from the matched candidate or the backend default. Always <see langword="false"/> for
/// <see cref="ReconciledModelState.Discovered"/> rows; for a pin it is <see langword="true"/> when the registry
/// entry carried a non-<see langword="null"/> <see cref="ModelRegistrationOptions.ContextLength"/>.
/// </param>
/// <param name="DiscoveredCapabilities">
/// The capabilities the backend currently reports for this model, carried only for an
/// <see cref="ReconciledModelState.Available"/> pin so its configured <see cref="Capabilities"/> can be
/// compared against them for drift (see <see cref="HasCapabilityDrift"/>); <see langword="null"/> for every
/// other state (and also for an available pin whose matching snapshot candidate had its probing skipped).
/// </param>
/// <param name="DiscoveredContextLength">
/// The <em>raw</em> context window the backend currently reports for this model, carried only for an
/// <see cref="ReconciledModelState.Available"/> pin so its configured <see cref="ContextLength"/> can be
/// compared against it for drift; <see langword="null"/> for every other state (and also when the backend
/// reports no window). This is the backend's reported value with no default applied, so drift reflects a real
/// difference between the pin's explicit override and what the backend actually offers.
/// </param>
/// <param name="Metadata">
/// The descriptive metadata the backend currently reports for this model (display name, description, tokenizer,
/// quantization, pricing, …), carried from the matched snapshot candidate for an
/// <see cref="ReconciledModelState.Available"/> pin and a <see cref="ReconciledModelState.Discovered"/> row
/// alike. It is <see langword="null"/> when the backend reported none, or when the model is
/// <see cref="ReconciledModelState.Unavailable"/> (no matching candidate). Purely informational: it never
/// affects reconciliation, pinning, or drift. The admin surface shows it so the operator sees the richest honest
/// picture each backend offers before deciding what to pin.
/// </param>
/// <param name="IsExposed">
/// Whether the runtime catalog actually exposes this model to clients under the backend's effective mode. The
/// admin surface uses it to avoid advertising an <see cref="ExposedName"/> the proxy never serves. It is
/// <see langword="false"/> only for a <see cref="ReconciledModelState.Discovered"/> row under
/// <see cref="OperatingMode.Explicit"/>: that mode exposes the registry alone, so an unpinned discovered model is
/// listed for the operator to promote but is not auto-exposed (mirroring the runtime catalog builder, which skips
/// Explicit backends' unpinned candidates). Every pinned row (<see cref="ReconciledModelState.Available"/> or
/// <see cref="ReconciledModelState.Unavailable"/>), and every discovered row under a mode that auto-exposes
/// (Hybrid, PlugAndPlay), is <see langword="true"/>. When <see langword="false"/>, the <see cref="ExposedName"/>
/// is still computed as the would-be client-facing name (so pinning previews it) but is not the catalog's truth.
/// </param>
public sealed record ReconciledModel(
	string                 Name,
	string                 ExposedName,
	string                 BackendName,
	string                 UpstreamModel,
	ModelCapabilities?     Capabilities,
	long?                  ContextLength,
	ReconciledModelState   State,
	bool                   ExplicitContextOverride = false,
	ModelCapabilities?     DiscoveredCapabilities  = null,
	long?                  DiscoveredContextLength = null,
	ProviderModelMetadata? Metadata                = null,
	bool                   IsExposed               = true)
{
	/// <summary>
	/// Gets a value indicating whether this model originates from an existing registry pin. It is
	/// <see langword="true"/> for both <see cref="ReconciledModelState.Available"/> and
	/// <see cref="ReconciledModelState.Unavailable"/> (both come from a pin) and <see langword="false"/> for
	/// <see cref="ReconciledModelState.Discovered"/>.
	/// </summary>
	public bool IsPinned => State is ReconciledModelState.Available or ReconciledModelState.Unavailable;

	/// <summary>
	/// Gets a value indicating whether this available pin's configured capabilities differ from the ones the
	/// backend currently reports. Only the functional flags are compared (completion, tools, vision,
	/// embeddings); <see cref="ModelCapabilities.Source"/> is ignored because it records <em>provenance</em>,
	/// not the capability, and the two sides' provenance differs by construction. It is
	/// <see langword="false"/> unless the row is <see cref="ReconciledModelState.Available"/> and both the
	/// pinned and discovered capabilities are known.
	/// </summary>
	public bool HasCapabilityDrift => State is ReconciledModelState.Available &&
	                                  Capabilities is { } pinned &&
	                                  DiscoveredCapabilities is { } discovered &&
	                                  CapabilitiesDiffer(pinned, discovered);

	/// <summary>
	/// Gets a value indicating whether this available pin's configured context window differs from the one the
	/// backend currently reports. Drift is detected only when the pin carries an <em>explicit</em> context
	/// override (<see cref="ExplicitContextOverride"/>) that differs from the backend's value. A pin without an
	/// explicit override inherits the discovered context dynamically, so no drift can occur. It is
	/// <see langword="false"/> unless the row is <see cref="ReconciledModelState.Available"/>, the pin has an
	/// explicit override, and both windows are known.
	/// </summary>
	public bool HasContextDrift => State is ReconciledModelState.Available &&
	                               ExplicitContextOverride &&
	                               ContextLength is { } pinnedContext &&
	                               DiscoveredContextLength is { } discoveredContext &&
	                               pinnedContext != discoveredContext;

	/// <summary>
	/// Gets a value indicating whether this available pin has drifted from the backend in either its
	/// capabilities (<see cref="HasCapabilityDrift"/>) or its context window (<see cref="HasContextDrift"/>).
	/// This is the signal the admin surface highlights so the operator can realign or remove a stale pin.
	/// </summary>
	public bool IsDrifted => HasCapabilityDrift || HasContextDrift;

	/// <summary>
	/// Compares two capability sets by their functional flags only (completion, tools, vision, embeddings),
	/// ignoring <see cref="ModelCapabilities.Source"/> for the reason given on <see cref="HasCapabilityDrift"/>.
	/// </summary>
	/// <param name="pinned">The pin's configured capabilities.</param>
	/// <param name="discovered">The capabilities the backend currently reports.</param>
	/// <returns>
	/// <see langword="true"/> if any of completion, tools, vision, or embeddings support differs;
	/// otherwise <see langword="false"/>.
	/// </returns>
	private static bool CapabilitiesDiffer(ModelCapabilities pinned, ModelCapabilities discovered) =>
		pinned.SupportsCompletion != discovered.SupportsCompletion ||
		pinned.SupportsTools != discovered.SupportsTools ||
		pinned.SupportsVision != discovered.SupportsVision ||
		pinned.SupportsEmbeddings != discovered.SupportsEmbeddings;
}
