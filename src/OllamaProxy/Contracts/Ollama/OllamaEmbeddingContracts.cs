// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OllamaProxy.Contracts.Ollama;

/// <summary>
/// Request body for the Ollama <c>POST /api/embed</c> endpoint (the current embeddings API). The
/// <see cref="Input"/> field accepts either a single string or an array of strings, so it is modeled
/// as a raw <see cref="JsonNode"/> and normalized during translation.
/// </summary>
/// <param name="Model">The embedding model name as known to the client.</param>
/// <param name="Input">A single string or an array of strings to embed.</param>
/// <param name="Truncate">
/// Whether inputs exceeding the model context window are truncated (<see langword="true"/>) or cause the backend to
/// return an error (<see langword="false"/>). Accepted for compatibility and currently not forwarded upstream.
/// </param>
/// <param name="Dimensions">
/// Optional number of dimensions for the generated embedding vectors. Accepted for compatibility and currently not
/// forwarded upstream.
/// </param>
/// <param name="KeepAlive">Optional model keep-alive hint. Accepted for compatibility and ignored upstream.</param>
/// <param name="Options">Optional generation parameters. Accepted for compatibility.</param>
sealed record OllamaEmbedRequest(
	[property: JsonPropertyName("model")]      string         Model,
	[property: JsonPropertyName("input")]      JsonNode?      Input      = null,
	[property: JsonPropertyName("truncate")]   bool?          Truncate   = null,
	[property: JsonPropertyName("dimensions")] int?           Dimensions = null,
	[property: JsonPropertyName("keep_alive")] JsonNode?      KeepAlive  = null,
	[property: JsonPropertyName("options")]    OllamaOptions? Options    = null);

/// <summary>
/// Response body for the Ollama <c>POST /api/embed</c> endpoint.
/// </summary>
/// <param name="Model">The model name echoed back to the client.</param>
/// <param name="Embeddings">One embedding vector per input item, in input order.</param>
/// <param name="TotalDuration">Total request duration in nanoseconds.</param>
/// <param name="LoadDuration">Model load duration in nanoseconds.</param>
/// <param name="PromptEvalCount">Number of prompt tokens evaluated.</param>
sealed record OllamaEmbedResponse(
	[property: JsonPropertyName("model")]             string                              Model,
	[property: JsonPropertyName("embeddings")]        IReadOnlyList<IReadOnlyList<float>> Embeddings,
	[property: JsonPropertyName("total_duration")]    long?                               TotalDuration   = null,
	[property: JsonPropertyName("load_duration")]     long?                               LoadDuration    = null,
	[property: JsonPropertyName("prompt_eval_count")] int?                                PromptEvalCount = null);

/// <summary>
/// Request body for the legacy Ollama <c>POST /api/embeddings</c> endpoint, retained for clients that
/// have not migrated to <c>/api/embed</c>. It embeds exactly one prompt.
/// </summary>
/// <param name="Model">The embedding model name as known to the client.</param>
/// <param name="Prompt">The single text to embed.</param>
/// <param name="Options">Optional generation parameters. Accepted for compatibility.</param>
/// <param name="KeepAlive">Optional model keep-alive hint. Accepted for compatibility and ignored upstream.</param>
sealed record OllamaLegacyEmbeddingsRequest(
	[property: JsonPropertyName("model")]      string         Model,
	[property: JsonPropertyName("prompt")]     string         Prompt,
	[property: JsonPropertyName("options")]    OllamaOptions? Options   = null,
	[property: JsonPropertyName("keep_alive")] JsonNode?      KeepAlive = null);

/// <summary>
/// Response body for the legacy Ollama <c>POST /api/embeddings</c> endpoint, carrying a single vector.
/// </summary>
/// <param name="Embedding">The embedding vector for the single input prompt.</param>
sealed record OllamaLegacyEmbeddingsResponse([property: JsonPropertyName("embedding")] IReadOnlyList<float> Embedding);
