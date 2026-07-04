// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json.Nodes;

using OllamaProxy.Configuration;

namespace OllamaProxy.Providers.OpenAiProtocol.Mapping;

/// <summary>
/// Resolves the neutral <see cref="ReasoningEffort"/> for a chat request from the inbound Ollama
/// <c>think</c> directive and the backend's configured default. Ollama accepts <c>think</c> as either a
/// boolean or a level string, so both shapes are parsed; an explicit per-request value always overrides
/// the backend default, and when neither is present the result is <see langword="null"/> so no
/// reasoning directive is sent upstream.
/// </summary>
static class ReasoningEffortParser
{
	/// <summary>
	/// Resolves the effective reasoning effort along with its provenance, preferring an explicit inbound
	/// <c>think</c> directive over the backend default and falling back to <see langword="null"/>
	/// ("unspecified") when neither is supplied or the directive is not a recognized shape. The returned
	/// <see cref="ReasoningResolution.Source"/> records which input won, so the decision can be traced.
	/// </summary>
	/// <param name="think">The raw Ollama <c>think</c> node, if supplied.</param>
	/// <param name="backendDefault">The backend's configured default reasoning effort, if any.</param>
	/// <returns>The resolved effort together with its provenance.</returns>
	public static ReasoningResolution Resolve(JsonNode? think, ReasoningEffort? backendDefault)
	{
		ReasoningEffort? requested = Parse(think);

		// A per-request directive wins over the backend default; otherwise the default (possibly null) applies.
		if (requested is { } fromRequest)
		{
			return new ReasoningResolution(fromRequest, ReasoningEffortSource.Request, backendDefault);
		}

		return backendDefault is { } fromBackend
			       ? new ReasoningResolution(fromBackend, ReasoningEffortSource.BackendDefault, backendDefault)
			       : new ReasoningResolution(null, ReasoningEffortSource.Unspecified, backendDefault);
	}

	/// <summary>
	/// Maps a raw Ollama <c>think</c> node onto a neutral effort: a boolean <c>true</c> becomes
	/// <see cref="ReasoningEffort.Medium"/> and <c>false</c> becomes <see cref="ReasoningEffort.None"/>,
	/// while a level string is matched case-insensitively against the known levels. Any other value
	/// (a missing node or an unrecognized string) yields <see langword="null"/> so it is ignored
	/// rather than guessed.
	/// </summary>
	/// <param name="think">The raw Ollama <c>think</c> node, if supplied.</param>
	/// <returns>The parsed effort, or <see langword="null"/> when the node carries no usable directive.</returns>
	private static ReasoningEffort? Parse(JsonNode? think)
	{
		if (think is not JsonValue value) return null;

		// Ollama's bare boolean form: true enables a balanced reasoning budget, false turns it off.
		if (value.TryGetValue(out bool flag)) return flag ? ReasoningEffort.Medium : ReasoningEffort.None;

		// The richer string form carries an explicit level, mapped case-insensitively.
		if (value.TryGetValue(out string? level) && level is not null)
		{
			return level.Trim().ToLowerInvariant() switch
			{
				"none"    => ReasoningEffort.None,
				"minimal" => ReasoningEffort.Minimal,
				"low"     => ReasoningEffort.Low,
				"medium"  => ReasoningEffort.Medium,
				"high"    => ReasoningEffort.High,
				"xhigh"   => ReasoningEffort.XHigh,
				"max"     => ReasoningEffort.Max,
				// An unrecognized level is ignored rather than guessed, so a typo never silently changes behavior.
				var _ => null
			};
		}

		return null;
	}
}
