// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OllamaProxy.Providers.OpenAiProtocol.Contracts;

/// <summary>
/// Request body for the OpenAI <c>POST /v1/embeddings</c> endpoint. The <see cref="Input"/> field
/// accepts either a single string or an array of strings, so it is modeled as a raw
/// <see cref="JsonNode"/> and populated during translation from the Ollama embeddings contracts.
/// </summary>
/// <param name="Model">The upstream embedding model identifier.</param>
/// <param name="Input">A single string or an array of strings to embed.</param>
/// <param name="EncodingFormat">
/// The requested vector encoding. Defaults to <c>float</c> so the response carries plain numeric
/// arrays rather than base64-encoded payloads.
/// </param>
sealed record OpenAiEmbeddingRequest(
	[property: JsonPropertyName("model")]           string    Model,
	[property: JsonPropertyName("input")]           JsonNode? Input,
	[property: JsonPropertyName("encoding_format")] string    EncodingFormat = "float");

/// <summary>
/// Response body for the OpenAI <c>POST /v1/embeddings</c> endpoint.
/// </summary>
/// <param name="Data">
/// One embedding entry per input item, each carrying the vector and its index. May be absent (the
/// property is <see langword="null"/>) when a non-conforming backend omits the array on a 2xx response.
/// </param>
/// <param name="Model">The model identifier echoed back by the backend.</param>
/// <param name="Usage">Token accounting for the request, when reported.</param>
sealed record OpenAiEmbeddingResponse(
	[property: JsonPropertyName("data")]  IReadOnlyList<OpenAiEmbeddingData>? Data,
	[property: JsonPropertyName("model")] string?                             Model = null,
	[property: JsonPropertyName("usage")] OpenAiEmbeddingUsage?               Usage = null);

/// <summary>
/// A single embedding entry from <c>POST /v1/embeddings</c>, pairing a vector with its position in
/// the original input sequence.
/// </summary>
/// <param name="Embedding">The embedding vector for the corresponding input item.</param>
/// <param name="Index">The zero-based position of the item within the request input.</param>
sealed record OpenAiEmbeddingData(
	[property: JsonPropertyName("embedding")] IReadOnlyList<float> Embedding,
	[property: JsonPropertyName("index")]     int                  Index);

/// <summary>
/// Token accounting reported by the OpenAI embeddings endpoint, used to populate the Ollama
/// <c>prompt_eval_count</c> field.
/// </summary>
/// <param name="PromptTokens">The number of tokens consumed by the embedded input.</param>
/// <param name="TotalTokens">The total number of tokens billed for the request.</param>
sealed record OpenAiEmbeddingUsage(
	[property: JsonPropertyName("prompt_tokens")] int? PromptTokens = null,
	[property: JsonPropertyName("total_tokens")]  int? TotalTokens  = null);
