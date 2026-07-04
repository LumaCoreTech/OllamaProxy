// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json.Serialization;

namespace OllamaProxy.Contracts.Ollama;

/// <summary>
/// Generation parameters supplied under the Ollama <c>options</c> object. Most fields map onto the
/// standard OpenAI sampling parameters; <see cref="TopK"/> and <see cref="MinP"/> have no standard
/// OpenAI equivalent and are therefore accepted here but forwarded only by providers whose backend
/// honors them (vLLM, OpenRouter, Venice). The generic OpenAI provider omits them so a strict backend
/// never receives an unknown sampling field. <see cref="NumCtx"/> is validated locally rather than
/// forwarded. Options not modeled here are dropped during translation because upstream backends do not
/// understand the remaining Ollama-specific tuning knobs.
/// </summary>
/// <param name="Temperature">Sampling temperature. Maps to OpenAI <c>temperature</c>.</param>
/// <param name="TopP">Nucleus sampling probability mass. Maps to OpenAI <c>top_p</c>.</param>
/// <param name="TopK">
/// Limits sampling to the <c>K</c> most probable tokens. It has no standard OpenAI equivalent, so it is
/// stamped on as <c>top_k</c> only by providers whose backend honors it (vLLM, OpenRouter, Venice); the
/// generic OpenAI provider drops it rather than send a strict backend an unknown field.
/// </param>
/// <param name="MinP">
/// Minimum token probability, relative to the most likely token, for a token to be considered. It has
/// no standard OpenAI equivalent, so it is stamped on as <c>min_p</c> only by providers whose backend
/// honors it (vLLM, OpenRouter, Venice); the generic OpenAI provider drops it rather than send a strict
/// backend an unknown field.
/// </param>
/// <param name="Seed">Deterministic sampling seed. Maps to OpenAI <c>seed</c>.</param>
/// <param name="NumPredict">
/// Maximum number of tokens to generate. Maps to OpenAI <c>max_completion_tokens</c>. The Ollama sentinel
/// <c>-1</c> (generate until context is filled) is treated as "no limit" and omitted upstream.
/// </param>
/// <param name="Stop">
/// Stop sequences that halt generation. Maps to OpenAI <c>stop</c>.
/// </param>
/// <param name="FrequencyPenalty">Penalizes token frequency. Maps to OpenAI <c>frequency_penalty</c>.</param>
/// <param name="PresencePenalty">Penalizes token presence. Maps to OpenAI <c>presence_penalty</c>.</param>
/// <param name="NumCtx">
/// The context window size (in tokens) the client requests for this call. It is not forwarded upstream
/// because OpenAI-compatible backends size the context themselves. It is still checked against the model's
/// resolved context window so a request that asks for more than the backend can serve is rejected explicitly
/// rather than failing opaquely downstream.
/// </param>
sealed record OllamaOptions(
	[property: JsonPropertyName("temperature")]       double?                Temperature      = null,
	[property: JsonPropertyName("top_p")]             double?                TopP             = null,
	[property: JsonPropertyName("top_k")]             int?                   TopK             = null,
	[property: JsonPropertyName("min_p")]             double?                MinP             = null,
	[property: JsonPropertyName("seed")]              int?                   Seed             = null,
	[property: JsonPropertyName("num_predict")]       int?                   NumPredict       = null,
	[property: JsonPropertyName("stop")]              IReadOnlyList<string>? Stop             = null,
	[property: JsonPropertyName("frequency_penalty")] double?                FrequencyPenalty = null,
	[property: JsonPropertyName("presence_penalty")]  double?                PresencePenalty  = null,
	[property: JsonPropertyName("num_ctx")]           int?                   NumCtx           = null);
