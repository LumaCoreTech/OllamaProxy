// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Contracts.Ollama;
using OllamaProxy.Core;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Endpoints;

/// <summary>
/// Projects a <see cref="RegisteredModel"/> onto the Ollama metadata shapes used by <c>/api/tags</c>
/// and <c>/api/show</c>. Upstream OpenAI-compatible backends do not report the filesystem-oriented
/// fields Ollama clients expect (size, digest, quantization, family), so those are synthesized to
/// stable, plausible placeholders; the one field that materially affects client behavior, the
/// capability list, is derived faithfully from the model's resolved <see cref="ModelCapabilities"/>.
/// Lives in the endpoints layer because its only consumers are the Ollama metadata endpoints
/// (<see cref="ModelEndpoints"/>); placing it here keeps every Ollama-DTO-producing helper in one
/// place rather than scattered across the core.
/// </summary>
static class ModelProjection
{
	/// <summary>The Ollama capability token that enables tool calling in clients such as Copilot.</summary>
	public const string ToolsCapability = "tools";

	/// <summary>The Ollama capability token advertising chat/text completion support.</summary>
	public const string CompletionCapability = "completion";

	/// <summary>The Ollama capability token advertising image input support.</summary>
	public const string VisionCapability = "vision";

	/// <summary>The Ollama capability token advertising embedding generation support.</summary>
	public const string EmbeddingCapability = "embedding";

	/// <summary>A synthesized digest placeholder; upstreams do not expose a content hash.</summary>
	private const string SynthesizedDigest = "0000000000000000000000000000000000000000000000000000000000000000";

	/// <summary>
	/// The synthesized GGUF architecture reported under <c>general.architecture</c>. OpenAI-compatible
	/// backends expose no GGUF metadata, so a stable placeholder stands in. Architecture-namespaced keys
	/// in <c>model_info</c> (e.g. <c>openai.context_length</c>) derive their prefix from this value,
	/// mirroring the llama.cpp convention where such keys are namespaced by the architecture name.
	/// </summary>
	private const string SynthesizedArchitecture = "openai";

	/// <summary>
	/// Builds a <c>/api/tags</c> list entry for the model, stamping it with the supplied timestamp so
	/// every entry in a single listing reports a consistent modification time.
	/// </summary>
	/// <param name="model">The registered model to project.</param>
	/// <param name="modifiedAt">The ISO-8601 timestamp to report as the modification time.</param>
	/// <returns>The Ollama tags entry for the model.</returns>
	/// <exception cref="ArgumentNullException">
	/// <paramref name="model"/> is <see langword="null"/>.
	/// </exception>
	public static OllamaModelEntry ToModelEntry(RegisteredModel model, string modifiedAt)
	{
		ArgumentNullException.ThrowIfNull(model);

		return new OllamaModelEntry(
			model.Name,
			model.Name,
			modifiedAt,
			Size: 0,
			SynthesizedDigest,
			CreateDetails());
	}

	/// <summary>
	/// Builds the <c>/api/show</c> response for the model, deriving the decisive
	/// <see cref="OllamaShowResponse.Capabilities"/> list from the model's resolved capabilities and
	/// exposing the capability provenance under <c>model_info</c> for operator diagnostics.
	/// </summary>
	/// <param name="model">The registered model to describe.</param>
	/// <returns>The Ollama show response for the model.</returns>
	/// <exception cref="ArgumentNullException">
	/// <paramref name="model"/> is <see langword="null"/>.
	/// </exception>
	public static OllamaShowResponse ToShowResponse(RegisteredModel model)
	{
		ArgumentNullException.ThrowIfNull(model);

		ModelCapabilities capabilities = model.Capabilities;

		Dictionary<string, object> modelInfo = new(StringComparer.Ordinal)
		{
			// A client reads the context length under "{general.architecture}.context_length" (llama.cpp
			// convention), so the prefix must match general.architecture; both derive from one constant.
			["general.architecture"] = SynthesizedArchitecture,
			[$"{SynthesizedArchitecture}.context_length"] = model.ContextLength,
			["ollamaproxy.backend"] = model.BackendName,
			["ollamaproxy.upstream_model"] = model.UpstreamModel,
			["ollamaproxy.capability_source"] = capabilities.Source.ToString()
		};

		return new OllamaShowResponse(
			CreateDetails(),
			modelInfo,
			BuildCapabilityList(capabilities));
	}

	/// <summary>
	/// Translates the capability flags into the Ollama capability token list. <c>completion</c> is
	/// always present for a chat model; <c>tools</c>, <c>vision</c>, and <c>embedding</c> are added
	/// only when their respective flags are set, since their presence changes client behavior.
	/// </summary>
	/// <param name="capabilities">The resolved capabilities to translate.</param>
	/// <returns>The Ollama capability token list.</returns>
	private static List<string> BuildCapabilityList(ModelCapabilities capabilities)
	{
		List<string> tokens = [];

		if (capabilities.SupportsCompletion) tokens.Add(CompletionCapability);

		if (capabilities.SupportsTools) tokens.Add(ToolsCapability);

		if (capabilities.SupportsVision) tokens.Add(VisionCapability);

		if (capabilities.SupportsEmbeddings) tokens.Add(EmbeddingCapability);

		return tokens;
	}

	/// <summary>
	/// Creates the synthesized <see cref="OllamaModelDetails"/> shared by both projections. The values
	/// are neutral placeholders because OpenAI-compatible backends do not report model file metadata.
	/// </summary>
	/// <returns>The synthesized model details.</returns>
	private static OllamaModelDetails CreateDetails() => new(
		ParentModel: string.Empty,
		Format: "gguf",
		Family: "openai",
		Families: ["openai"],
		ParameterSize: string.Empty,
		QuantizationLevel: string.Empty);
}
