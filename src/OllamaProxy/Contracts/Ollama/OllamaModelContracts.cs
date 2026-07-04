// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json.Serialization;

namespace OllamaProxy.Contracts.Ollama;

/// <summary>
/// Response body for the Ollama <c>GET /api/tags</c> endpoint, listing the locally available models.
/// Clients (e.g. Copilot's model picker) call this to discover which models can be selected.
/// </summary>
/// <param name="Models">The available models. May be empty but is never <see langword="null"/>.</param>
sealed record OllamaTagsResponse([property: JsonPropertyName("models")] IReadOnlyList<OllamaModelEntry> Models);

/// <summary>
/// A single entry in the <c>GET /api/tags</c> model list.
/// </summary>
/// <param name="Name">The model name as exposed to clients (e.g. <c>gpt-4o</c>).</param>
/// <param name="Model">The model identifier, conventionally identical to <paramref name="Name"/>.</param>
/// <param name="ModifiedAt">An ISO-8601 timestamp. Synthesized because upstreams rarely provide one.</param>
/// <param name="Size">The model size in bytes. Synthesized; upstreams do not report this.</param>
/// <param name="Digest">A content digest placeholder. Synthesized for client compatibility.</param>
/// <param name="Details">Structured model metadata.</param>
sealed record OllamaModelEntry(
	[property: JsonPropertyName("name")]        string             Name,
	[property: JsonPropertyName("model")]       string             Model,
	[property: JsonPropertyName("modified_at")] string             ModifiedAt,
	[property: JsonPropertyName("size")]        long               Size,
	[property: JsonPropertyName("digest")]      string             Digest,
	[property: JsonPropertyName("details")]     OllamaModelDetails Details);

/// <summary>
/// Structured model metadata shared by <c>/api/tags</c> and <c>/api/show</c>.
/// </summary>
/// <param name="ParentModel">The parent model, if any. Usually empty.</param>
/// <param name="Format">The model file format (e.g. <c>gguf</c>). Synthesized.</param>
/// <param name="Family">The primary model family (e.g. <c>llama</c>). Synthesized.</param>
/// <param name="Families">All model families the model belongs to.</param>
/// <param name="ParameterSize">A human-readable parameter count (e.g. <c>7B</c>). Synthesized.</param>
/// <param name="QuantizationLevel">The quantization level (e.g. <c>Q4_0</c>). Synthesized.</param>
sealed record OllamaModelDetails(
	[property: JsonPropertyName("parent_model")]       string                ParentModel,
	[property: JsonPropertyName("format")]             string                Format,
	[property: JsonPropertyName("family")]             string                Family,
	[property: JsonPropertyName("families")]           IReadOnlyList<string> Families,
	[property: JsonPropertyName("parameter_size")]     string                ParameterSize,
	[property: JsonPropertyName("quantization_level")] string                QuantizationLevel);

/// <summary>
/// Request body for the Ollama <c>POST /api/show</c> endpoint.
/// </summary>
/// <param name="Model">The model name to describe.</param>
sealed record OllamaShowRequest([property: JsonPropertyName("model")] string Model);

/// <summary>
/// Response body for the Ollama <c>POST /api/show</c> endpoint. The <see cref="Capabilities"/> array
/// is the decisive field for tool-aware clients: when it contains <c>tools</c>, clients such as
/// GitHub Copilot enable tool/function calling for the model.
/// </summary>
/// <param name="Details">Structured model metadata.</param>
/// <param name="ModelInfo">
/// A free-form bag of architecture/context details (e.g. context length). Clients may read context
/// length from here to size their requests.
/// </param>
/// <param name="Capabilities">
/// The model's capabilities (e.g. <c>completion</c>, <c>tools</c>, <c>vision</c>, <c>insert</c>).
/// </param>
/// <param name="ModelFile">A synthesized modelfile string for compatibility.</param>
/// <param name="Parameters">A synthesized parameters string for compatibility.</param>
/// <param name="Template">A synthesized prompt template string for compatibility.</param>
sealed record OllamaShowResponse(
	[property: JsonPropertyName("details")]      OllamaModelDetails                  Details,
	[property: JsonPropertyName("model_info")]   IReadOnlyDictionary<string, object> ModelInfo,
	[property: JsonPropertyName("capabilities")] IReadOnlyList<string>               Capabilities,
	[property: JsonPropertyName("modelfile")]    string?                             ModelFile  = null,
	[property: JsonPropertyName("parameters")]   string?                             Parameters = null,
	[property: JsonPropertyName("template")]     string?                             Template   = null);

/// <summary>
/// Response body for the Ollama <c>GET /api/version</c> endpoint.
/// </summary>
/// <param name="Version">The reported Ollama-compatible version string.</param>
sealed record OllamaVersionResponse([property: JsonPropertyName("version")] string Version);

/// <summary>
/// Response body for the Ollama <c>GET /api/ps</c> endpoint, which lists the models currently held in
/// memory. The proxy holds no models itself (backends manage their own lifecycle), so it reports an
/// empty list, which clients tolerate as "nothing currently loaded."
/// </summary>
/// <param name="Models">The running models. Always empty for the proxy but never <see langword="null"/>.</param>
sealed record OllamaPsResponse([property: JsonPropertyName("models")] IReadOnlyList<OllamaPsModel> Models);

/// <summary>
/// A single entry in the <c>GET /api/ps</c> running-model list. Retained for contract completeness;
/// the proxy never populates this because it does not load models locally.
/// </summary>
/// <param name="Name">The running model name.</param>
/// <param name="Model">The model identifier, conventionally identical to <paramref name="Name"/>.</param>
/// <param name="Size">The total model size in bytes.</param>
/// <param name="Digest">A content digest placeholder.</param>
/// <param name="Details">Structured model metadata.</param>
/// <param name="ExpiresAt">An ISO-8601 timestamp at which the model would be unloaded.</param>
/// <param name="SizeVram">The portion of the model resident in VRAM, in bytes.</param>
sealed record OllamaPsModel(
	[property: JsonPropertyName("name")]       string             Name,
	[property: JsonPropertyName("model")]      string             Model,
	[property: JsonPropertyName("size")]       long               Size,
	[property: JsonPropertyName("digest")]     string             Digest,
	[property: JsonPropertyName("details")]    OllamaModelDetails Details,
	[property: JsonPropertyName("expires_at")] string             ExpiresAt,
	[property: JsonPropertyName("size_vram")]  long               SizeVram);
