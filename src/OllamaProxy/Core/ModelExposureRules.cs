// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Configuration;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Core;

/// <summary>
/// The shared rules that turn a backend's raw model information into the attributes a client sees: the
/// client-facing name, the effective context window, and a registry entry's resolved capabilities. These
/// rules are the single source of truth for both startup catalog assembly (<see cref="ModelCatalogBuilder"/>)
/// and admin-time reconciliation (the <c>OllamaProxy.Admin.Reconciliation</c> engine), so a model is named,
/// sized, and described identically whether it is exposed at startup or previewed in the admin surface.
/// </summary>
/// <remarks>
/// Keeping these rules here, rather than duplicated in each caller, guarantees the admin preview cannot
/// drift from runtime behavior: a discovered model the operator pins is named and sized exactly as the
/// catalog would have exposed it on the next start.
/// </remarks>
static class ModelExposureRules
{
	/// <summary>
	/// Applies an optional backend prefix to a model's bare name, producing the client-facing name. When no
	/// prefix is configured the bare name is returned unchanged, so single-backend deployments keep short
	/// names; otherwise the name becomes <c>prefix/model</c>. The same rule names both auto-exposed models
	/// (whose bare name is the discovered upstream identifier) and explicit registry entries (whose bare name
	/// is the configured <see cref="ModelRegistrationOptions.Name"/>), so a model is exposed identically
	/// whether it is discovered or pinned. The prefix changes only the client-facing name; the identifier the
	/// proxy requests upstream is never prefixed.
	/// </summary>
	/// <param name="modelPrefix">The configured backend prefix, or <see langword="null"/> when none is set.</param>
	/// <param name="bareModelName">The bare model name to expose: a discovered upstream id or a registry entry's name.</param>
	/// <returns>The client-facing model name.</returns>
	public static string ApplyClientFacingPrefix(string? modelPrefix, string bareModelName) =>
		string.IsNullOrWhiteSpace(modelPrefix) ? bareModelName : $"{modelPrefix}/{bareModelName}";

	/// <summary>
	/// Resolves a model's effective context window from the three sources that can supply one, in strict
	/// precedence: an explicit per-model override wins first, the value the backend reported during discovery
	/// second, and the operator-configured backend default last. The backend default is therefore only a gap
	/// filler for backends that advertise no window. It never overrides or narrows a value the backend reports.
	/// </summary>
	/// <param name="explicitOverride">
	/// The explicit per-model <see cref="ModelRegistrationOptions.ContextLength"/> override, when set; this is
	/// the sanctioned way to deliberately constrain a single model below what its backend reports.
	/// </param>
	/// <param name="reported">The context length the backend reported during discovery, when any.</param>
	/// <param name="backendDefault">
	/// The operator-configured <see cref="BackendOptions.ContextLength"/> fallback, when any.
	/// </param>
	/// <returns>The effective context window in tokens, or <see langword="null"/> when none is known.</returns>
	/// <remarks>
	/// This is the single precedence rule shared by every caller (startup discovery's probe gate, the runtime
	/// catalog's auto-exposed and pinned paths, and the admin reconciliation preview) so a model is sized
	/// identically wherever it is resolved. Returns <see langword="null"/> when no source supplies a window
	/// rather than throwing, so each caller can choose its own policy: discovery skips the model and warns, a
	/// registry entry treats the missing window as a fatal configuration error, and the admin surface flags it
	/// as needing a context length before commit.
	/// </remarks>
	public static long? ResolveEffectiveContextWindow(long? explicitOverride, long? reported, long? backendDefault) =>
		explicitOverride ?? reported ?? backendDefault;

	/// <summary>
	/// Resolves a registry entry's pinned capabilities over a completion-capable baseline. An unset completion
	/// flag defaults to <see langword="true"/> (the proxy's baseline modality), while the additive tools,
	/// vision, and embeddings flags default to <see langword="false"/>. Any pinned flag marks the source
	/// <see cref="CapabilitySource.Configured"/>; otherwise <see cref="CapabilitySource.Default"/> applies,
	/// because an explicit registry entry intentionally bypasses live detection.
	/// </summary>
	/// <param name="registration">The registry entry whose capabilities are resolved.</param>
	/// <returns>The resolved capabilities for the pinned model.</returns>
	public static ModelCapabilities ResolveRegisteredCapabilities(ModelRegistrationOptions registration)
	{
		bool anyOverride =
			registration.SupportsCompletion.HasValue ||
			registration.SupportsTools.HasValue ||
			registration.SupportsVision.HasValue ||
			registration.SupportsEmbeddings.HasValue;

		return new ModelCapabilities(
			SupportsCompletion: registration.SupportsCompletion ?? true,
			SupportsTools: registration.SupportsTools ?? false,
			SupportsVision: registration.SupportsVision ?? false,
			SupportsEmbeddings: registration.SupportsEmbeddings ?? false,
			anyOverride ? CapabilitySource.Configured : CapabilitySource.Default);
	}
}
