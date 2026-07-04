// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Providers.OpenAiProtocol;

/// <summary>
/// Derives a model's capabilities directly from the modality and parameter metadata an OpenAI-compatible
/// backend reports about it. Some gateways (notably OpenRouter, and Venice once its structured spec is
/// translated) annotate each model with its accepted input modalities, its produced output modalities,
/// and the request parameters it honors; when present, those signals are authoritative. Vision is
/// inferred from an <c>image</c> input modality, tool support from a <c>tools</c> entry in the supported
/// parameters, and completion from a <c>text</c> output modality. A generation-only model that produces
/// solely <c>image</c> is reported as not supporting completion. A provider calls this during discovery
/// projection so a metadata-rich model carries authoritative capabilities and bypasses probing; a
/// metadata-poor model yields <see langword="null"/> here, leaving its capabilities to be resolved by the
/// provider's active probing fallback.
/// </summary>
static class ModelMetadataCapabilities
{
	private const string ImageModality  = "image";
	private const string TextModality   = "text";
	private const string ToolsParameter = "tools";

	/// <summary>
	/// Derives capabilities from the reported modality and parameter metadata.
	/// </summary>
	/// <param name="inputModalities">The accepted input modalities, or <see langword="null"/> when not reported.</param>
	/// <param name="outputModalities">The produced output modalities, or <see langword="null"/> when not reported.</param>
	/// <param name="supportedParameters">The honored request parameters, or <see langword="null"/> when not reported.</param>
	/// <returns>
	/// The derived capabilities tagged <see cref="CapabilitySource.ProviderMetadata"/> when any metadata
	/// signal was available; otherwise <see langword="null"/> so the caller can fall back to probing.
	/// </returns>
	public static ModelCapabilities? FromMetadata(
		IReadOnlyList<string>? inputModalities,
		IReadOnlyList<string>? outputModalities,
		IReadOnlyList<string>? supportedParameters)
	{
		bool hasInputModalityMetadata = inputModalities is { Count: > 0 };
		bool hasOutputModalityMetadata = outputModalities is { Count: > 0 };
		bool hasParameterMetadata = supportedParameters is { Count: > 0 };

		// With no metadata at all, this signal cannot conclude anything; defer to probing.
		if (!hasInputModalityMetadata && !hasOutputModalityMetadata && !hasParameterMetadata) return null;

		bool supportsVision = hasInputModalityMetadata && ContainsIgnoreCase(inputModalities!, ImageModality);
		bool supportsTools = hasParameterMetadata && ContainsIgnoreCase(supportedParameters!, ToolsParameter);

		// Output modalities are authoritative for completion: when reported, the model can chat only if
		// it produces text. A generation-only model (e.g. solely image output) is honestly marked as not
		// supporting completion. When no output modalities are reported, completion is assumed
		// conservatively, the same baseline a metadata-less model would receive from probing.
		bool supportsCompletion =
			!hasOutputModalityMetadata || ContainsIgnoreCase(outputModalities!, TextModality);

		return new ModelCapabilities(
			SupportsCompletion: supportsCompletion,
			SupportsTools: supportsTools,
			SupportsVision: supportsVision,
			SupportsEmbeddings: false,
			CapabilitySource.ProviderMetadata);
	}

	/// <summary>
	/// Determines whether a value collection contains the target entry, comparing case-insensitively.
	/// </summary>
	/// <param name="values">The collection to search.</param>
	/// <param name="target">The entry to look for.</param>
	/// <returns>
	/// <see langword="true"/> when a case-insensitive match exists; otherwise <see langword="false"/>.
	/// </returns>
	private static bool ContainsIgnoreCase(IReadOnlyList<string> values, string target)
	{
		foreach (string value in values)
		{
			if (string.Equals(value, target, StringComparison.OrdinalIgnoreCase))
				return true;
		}

		return false;
	}
}
