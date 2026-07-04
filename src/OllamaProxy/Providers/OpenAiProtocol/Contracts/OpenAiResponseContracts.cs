// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OllamaProxy.Providers.OpenAiProtocol.Contracts;

/// <summary>
/// A non-streaming OpenAI <c>POST /v1/chat/completions</c> response.
/// </summary>
/// <param name="Id">The completion id.</param>
/// <param name="Model">The model that produced the completion.</param>
/// <param name="Created">The Unix timestamp (seconds) the completion was created.</param>
/// <param name="Choices">
/// The completion choices. The proxy uses the first choice. May be absent (the property is
/// <see langword="null"/>) when a non-conforming backend omits the array on a 2xx response.
/// </param>
/// <param name="Usage">Token usage accounting, when reported.</param>
sealed record OpenAiChatCompletion(
	[property: JsonPropertyName("id")]      string?                          Id,
	[property: JsonPropertyName("model")]   string?                          Model,
	[property: JsonPropertyName("created")] long?                            Created,
	[property: JsonPropertyName("choices")] IReadOnlyList<OpenAiChatChoice>? Choices,
	[property: JsonPropertyName("usage")]   OpenAiUsage?                     Usage = null);

/// <summary>
/// A single choice in a non-streaming chat completion.
/// </summary>
/// <param name="Index">The choice index.</param>
/// <param name="Message">The complete assistant message.</param>
/// <param name="FinishReason">The reason generation stopped (e.g. <c>stop</c>, <c>length</c>, <c>tool_calls</c>).</param>
/// <param name="Logprobs">
/// The token log-probabilities for this choice when requested, as the raw OpenAI object nesting the
/// per-token entries under a <c>content</c> array. Translated to Ollama's shape by
/// <see cref="Mapping.OpenAiMessageConverter.ExtractLogprobs"/>.
/// </param>
sealed record OpenAiChatChoice(
	[property: JsonPropertyName("index")]         int               Index,
	[property: JsonPropertyName("message")]       OpenAiChatMessage Message,
	[property: JsonPropertyName("finish_reason")] string?           FinishReason,
	[property: JsonPropertyName("logprobs")]      JsonNode?         Logprobs = null);

/// <summary>
/// A single chunk of a streamed OpenAI chat completion (one SSE <c>data:</c> event).
/// </summary>
/// <param name="Id">The completion id, stable across the stream.</param>
/// <param name="Model">The model that produced the completion.</param>
/// <param name="Created">The Unix timestamp (seconds) the chunk was created.</param>
/// <param name="Choices">
/// The choice deltas. May be empty (or absent entirely, the property is <see langword="null"/>) on
/// the terminal usage-only chunk (when <c>include_usage</c> is set), or when a non-conforming backend
/// omits the array.
/// </param>
/// <param name="Usage">Token usage, present only on the terminal chunk when usage is requested.</param>
sealed record OpenAiChatCompletionChunk(
	[property: JsonPropertyName("id")]      string?                               Id,
	[property: JsonPropertyName("model")]   string?                               Model,
	[property: JsonPropertyName("created")] long?                                 Created,
	[property: JsonPropertyName("choices")] IReadOnlyList<OpenAiChatChunkChoice>? Choices,
	[property: JsonPropertyName("usage")]   OpenAiUsage?                          Usage = null);

/// <summary>
/// A single choice delta in a streamed chat completion chunk.
/// </summary>
/// <param name="Index">The choice index.</param>
/// <param name="Delta">The incremental message content/tool-call fragment for this chunk.</param>
/// <param name="FinishReason">Set on the choice's final delta to indicate why generation stopped.</param>
/// <param name="Logprobs">
/// The token log-probabilities for the tokens in this delta when requested, as the raw OpenAI object
/// nesting the per-token entries under a <c>content</c> array. Translated to Ollama's shape by
/// <see cref="Mapping.OpenAiMessageConverter.ExtractLogprobs"/>.
/// </param>
sealed record OpenAiChatChunkChoice(
	[property: JsonPropertyName("index")]         int                Index,
	[property: JsonPropertyName("delta")]         OpenAiChatMessage? Delta,
	[property: JsonPropertyName("finish_reason")] string?            FinishReason,
	[property: JsonPropertyName("logprobs")]      JsonNode?          Logprobs = null);

/// <summary>
/// Token usage accounting returned by OpenAI completions and embeddings.
/// </summary>
/// <param name="PromptTokens">Number of input/prompt tokens. Maps to Ollama <c>prompt_eval_count</c>.</param>
/// <param name="CompletionTokens">Number of generated tokens. Maps to Ollama <c>eval_count</c>.</param>
/// <param name="TotalTokens">The sum of prompt and completion tokens.</param>
sealed record OpenAiUsage(
	[property: JsonPropertyName("prompt_tokens")]     int? PromptTokens,
	[property: JsonPropertyName("completion_tokens")] int? CompletionTokens,
	[property: JsonPropertyName("total_tokens")]      int? TotalTokens);

/// <summary>
/// An OpenAI error envelope (<c>{ "error": { ... } }</c>) returned on non-success responses.
/// </summary>
/// <param name="Error">The error detail.</param>
sealed record OpenAiErrorEnvelope([property: JsonPropertyName("error")] OpenAiError? Error);

/// <summary>
/// The detail object of an OpenAI error response.
/// </summary>
/// <param name="Message">A human-readable error message.</param>
/// <param name="Type">The error type/category.</param>
/// <param name="Code">An optional machine-readable error code.</param>
sealed record OpenAiError(
	[property: JsonPropertyName("message")] string? Message,
	[property: JsonPropertyName("type")]    string? Type,
	[property: JsonPropertyName("code")]    string? Code);
