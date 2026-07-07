// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Diagnostics;

namespace OllamaProxy.Configuration;

/// <summary>
/// The provider-neutral reasoning effort a chat request asks the model to spend on its internal
/// deliberation. The proxy resolves an effort from the inbound request (the Ollama <c>think</c> field)
/// or a backend default, then each OpenAI-compatible provider maps it onto its own wire dialect.
/// The vocabulary mirrors OpenAI's published set plus the additional <see cref="Max"/> level that some
/// models and providers accept (for example Anthropic's Claude models, and Venice). The values are ordered
/// weakest-to-strongest, so a request- or backend-default-sourced effort the provider's API does not
/// recognize is <em>clamped</em> down to that provider's highest accepted token before it is sent. That
/// clamp is a <em>token-validity</em> guard only: it prevents emitting a level the API rejects as unknown,
/// but it cannot know whether a specific model accepts the level. A strict backend may still reject it. A
/// model's pinned effort is the only authoritative guarantee and is therefore sent <em>verbatim</em>, never
/// clamped. A <see langword="null"/> effort means "unspecified": the proxy sends no reasoning directive at
/// all.
/// </summary>
public enum ReasoningEffort
{
	/// <summary>
	/// Reasoning is explicitly turned off; the model should not deliberate before answering.
	/// </summary>
	None,

	/// <summary>
	/// The smallest non-zero amount of reasoning.
	/// </summary>
	Minimal,

	/// <summary>
	/// A low reasoning budget.
	/// </summary>
	Low,

	/// <summary>
	/// A balanced reasoning budget. Also the level mapped from a bare Ollama <c>think: true</c>.
	/// </summary>
	Medium,

	/// <summary>
	/// A high reasoning budget.
	/// </summary>
	High,

	/// <summary>
	/// An extended reasoning budget above <see cref="High"/> (OpenAI <c>xhigh</c>).
	/// </summary>
	XHigh,

	/// <summary>
	/// The maximum reasoning budget, above <see cref="XHigh"/>. Accepted by some models and providers (for
	/// example Anthropic's Claude models); a provider or model without a <c>max</c> level clamps
	/// it to its nearest supported one.
	/// </summary>
	Max
}

/// <summary>
/// Extension helpers translating a <see cref="ReasoningEffort"/> either onto the canonical OpenAI
/// <c>reasoning_effort</c> wire token shared by most OpenAI-compatible backends, or onto the
/// human-readable label the admin UI shows in its reasoning-effort selectors.
/// </summary>
static class ReasoningEffortExtensions
{
	/// <summary>
	/// Returns the canonical OpenAI <c>reasoning_effort</c> wire token for the supplied effort (for
	/// example <see cref="ReasoningEffort.XHigh"/> becomes <c>xhigh</c>).
	/// </summary>
	/// <param name="effort">The neutral effort to translate.</param>
	/// <returns>The lowercase wire token recognized by OpenAI-compatible backends.</returns>
	/// <exception cref="UnreachableException">
	/// <paramref name="effort"/> is not a defined <see cref="ReasoningEffort"/> value.
	/// </exception>
	public static string ToWireValue(this ReasoningEffort effort) => effort switch
	{
		ReasoningEffort.None    => "none",
		ReasoningEffort.Minimal => "minimal",
		ReasoningEffort.Low     => "low",
		ReasoningEffort.Medium  => "medium",
		ReasoningEffort.High    => "high",
		ReasoningEffort.XHigh   => "xhigh",
		ReasoningEffort.Max     => "max",
		// All defined enum values are handled above; an undefined value cannot occur through binding or parsing.
		var _ => throw new UnreachableException($"Unhandled reasoning effort '{effort}'.")
	};

	/// <summary>
	/// Returns the human-readable label for the supplied effort, as shown in the admin UI's
	/// reasoning-effort selectors (for example <see cref="ReasoningEffort.XHigh"/> becomes
	/// <c>Extra high</c>). Distinct from <see cref="ToWireValue"/>, which yields the lowercase provider
	/// wire token; this is the display copy the operator reads.
	/// </summary>
	/// <param name="effort">The neutral effort to translate.</param>
	/// <returns>The display label for the effort level.</returns>
	/// <exception cref="UnreachableException">
	/// <paramref name="effort"/> is not a defined <see cref="ReasoningEffort"/> value.
	/// </exception>
	public static string ToDisplayLabel(this ReasoningEffort effort) => effort switch
	{
		ReasoningEffort.None    => "None",
		ReasoningEffort.Minimal => "Minimal",
		ReasoningEffort.Low     => "Low",
		ReasoningEffort.Medium  => "Medium",
		ReasoningEffort.High    => "High",
		ReasoningEffort.XHigh   => "Extra high",
		ReasoningEffort.Max     => "Max",
		// All defined enum values are handled above; an undefined value cannot occur through binding or parsing.
		var _ => throw new UnreachableException($"Unhandled reasoning effort '{effort}'.")
	};
}
