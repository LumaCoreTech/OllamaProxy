// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Providers.Abstractions;

/// <summary>
/// The provenance of a resolved <see cref="ModelCapabilities"/> value. Recorded for diagnostics so
/// operators can see <em>why</em> a capability was reported, which is especially useful when only the
/// conservative default could be applied.
/// </summary>
public enum CapabilitySource
{
	/// <summary>
	/// The capabilities were taken verbatim from explicit registry configuration.
	/// </summary>
	Configured,

	/// <summary>
	/// The capabilities were derived from metadata returned by the backend's model listing.
	/// </summary>
	ProviderMetadata,

	/// <summary>
	/// The capabilities were confirmed by an active probe against the backend.
	/// </summary>
	Probed,

	/// <summary>
	/// No signal was available; conservative defaults were applied.
	/// </summary>
	Default
}

/// <summary>
/// The capabilities whose active probe stayed <em>inconclusive</em>: a probe that neither confirmed nor
/// denied the capability because it timed out or kept failing transiently across its retries, so the flag
/// kept its conservative default rather than a measured answer. This is a diagnostic overlay carried
/// alongside the functional flags purely so the admin surface can tell a <em>measured</em> result apart from
/// an "unknown because the probe could not complete"; it never reaches the <c>/api/show</c> contract, which
/// only reads the booleans.
/// <para>
/// Completion is included but means something different from the three optional capabilities. Its probe is
/// fail-open: an inconclusive completion probe keeps <c>SupportsCompletion</c> <see langword="true"/> (it never
/// hides a working chat model), so the flag marks a capability that is still <em>supported</em> but
/// <em>unconfirmed</em>: "kept completion-capable, just not verified". The three optional capabilities under-
/// report instead: an inconclusive probe leaves their flag at the conservative <see langword="false"/>, so
/// their flag marks an "off but unconfirmed" capability. The admin surface renders the two cases distinctly
/// (a supported-but-unconfirmed chip versus an off-but-unconfirmed one), but both exist for the same reason:
/// so a failed probe is never silently presented as a measured fact.
/// </para>
/// </summary>
[Flags]
public enum InconclusiveCapabilities
{
	/// <summary>
	/// Every probed capability resolved conclusively (or was never probed).
	/// </summary>
	None = 0,

	/// <summary>
	/// The completion-support probe stayed inconclusive; <c>SupportsCompletion</c> is the fail-open default
	/// (<see langword="true"/>), so the model is kept completion-capable but the capability is unconfirmed.
	/// </summary>
	Completion = 1 << 0,

	/// <summary>
	/// The tool-support probe stayed inconclusive; <c>SupportsTools</c> is the conservative default.
	/// </summary>
	Tools = 1 << 1,

	/// <summary>
	/// The vision-support probe stayed inconclusive; <c>SupportsVision</c> is the conservative default.
	/// </summary>
	Vision = 1 << 2,

	/// <summary>
	/// The embedding-support probe stayed inconclusive; <c>SupportsEmbeddings</c> is the conservative default.
	/// </summary>
	Embeddings = 1 << 3
}

/// <summary>
/// The set of capabilities a model exposes, as surfaced to Ollama clients through <c>/api/show</c>.
/// These flags drive client behavior.
/// </summary>
/// <param name="SupportsCompletion">Whether the model can perform chat/text completion.</param>
/// <param name="SupportsTools">Whether the model can be advertised tool/function definitions and emit tool calls.</param>
/// <param name="SupportsVision">Whether the model accepts image input.</param>
/// <param name="SupportsEmbeddings">Whether the model can produce embedding vectors.</param>
/// <param name="Source">The provenance of these flags, retained for startup diagnostics.</param>
/// <param name="Inconclusive">
/// The optional capabilities whose probe stayed inconclusive, so their flag reflects the conservative default
/// rather than a measured result. Defaults to <see cref="InconclusiveCapabilities.None"/>. Diagnostic only: it
/// is excluded from the <c>/api/show</c> capability list and from drift comparison, which read only the
/// functional flags; it exists so the admin surface can show "probe could not confirm" instead of a misleading
/// "unsupported".
/// </param>
public sealed record ModelCapabilities(
	bool                     SupportsCompletion,
	bool                     SupportsTools,
	bool                     SupportsVision,
	bool                     SupportsEmbeddings,
	CapabilitySource         Source,
	InconclusiveCapabilities Inconclusive = InconclusiveCapabilities.None)
{
	/// <summary>
	/// Gets a conservative capability set for a chat model whose features could not be determined:
	/// completion only, with <see cref="Source"/> set to <see cref="CapabilitySource.Default"/>.
	/// </summary>
	public static ModelCapabilities CompletionOnly { get; } =
		new(
			SupportsCompletion: true,
			SupportsTools: false,
			SupportsVision: false,
			SupportsEmbeddings: false,
			CapabilitySource.Default);
}
