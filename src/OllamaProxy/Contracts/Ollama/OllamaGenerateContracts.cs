// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OllamaProxy.Contracts.Ollama;

/// <summary>
/// Request body for the Ollama <c>POST /api/generate</c> endpoint. This single-prompt completion
/// API is translated by wrapping <see cref="Prompt"/> (and optional <see cref="System"/>) into chat
/// messages and reusing the chat translation pipeline.
/// </summary>
/// <param name="Model">The model name as known to the client.</param>
/// <param name="Prompt">The user prompt to complete. May be empty for a model-warmup request.</param>
/// <param name="Suffix">Optional text appended after the completion (fill-in-the-middle).</param>
/// <param name="System">Optional system prompt, mapped to a leading system message.</param>
/// <param name="Images">Optional base64-encoded images for multimodal input.</param>
/// <param name="Format">Optional structured-output directive (<c>"json"</c> or a JSON schema object).</param>
/// <param name="Options">Optional generation parameters.</param>
/// <param name="Stream">Whether the response is streamed. Defaults to <see langword="true"/> when omitted.</param>
/// <param name="Think">
/// Optional reasoning directive forwarded to the chat pipeline. Ollama accepts either a boolean
/// (<c>true</c>/<c>false</c>) or a level string (<c>"low"</c>, <c>"medium"</c>, <c>"high"</c>); it is modeled
/// as a raw <see cref="JsonNode"/> because both shapes are valid. See <see cref="OllamaChatRequest.Think"/>
/// for how the proxy resolves it to a neutral reasoning effort.
/// </param>
/// <param name="Raw">
/// When <see langword="true"/>, the prompt is passed through without templating. Accepted for
/// compatibility; the proxy always forwards the prompt verbatim to the chat pipeline.
/// </param>
/// <param name="KeepAlive">Optional model keep-alive hint. Accepted for compatibility and ignored upstream.</param>
/// <param name="Logprobs">Optional flag requesting token log-probabilities in the response.</param>
/// <param name="TopLogprobs">
/// Optional number of most likely tokens to return at each position when <see cref="Logprobs"/> is enabled.
/// </param>
sealed record OllamaGenerateRequest(
	[property: JsonPropertyName("model")]        string                 Model,
	[property: JsonPropertyName("prompt")]       string?                Prompt      = null,
	[property: JsonPropertyName("suffix")]       string?                Suffix      = null,
	[property: JsonPropertyName("system")]       string?                System      = null,
	[property: JsonPropertyName("images")]       IReadOnlyList<string>? Images      = null,
	[property: JsonPropertyName("format")]       JsonNode?              Format      = null,
	[property: JsonPropertyName("options")]      OllamaOptions?         Options     = null,
	[property: JsonPropertyName("stream")]       bool?                  Stream      = null,
	[property: JsonPropertyName("think")]        JsonNode?              Think       = null,
	[property: JsonPropertyName("raw")]          bool?                  Raw         = null,
	[property: JsonPropertyName("keep_alive")]   JsonNode?              KeepAlive   = null,
	[property: JsonPropertyName("logprobs")]     bool?                  Logprobs    = null,
	[property: JsonPropertyName("top_logprobs")] int?                   TopLogprobs = null);

/// <summary>
/// A single object in the newline-delimited JSON stream returned by <c>POST /api/generate</c>. The
/// generated text arrives incrementally via <see cref="Response"/>; the terminal chunk sets
/// <see cref="Done"/> and the timing/token accounting fields.
/// </summary>
/// <param name="Model">The model name echoed back to the client.</param>
/// <param name="CreatedAt">The ISO-8601 timestamp the chunk was produced.</param>
/// <param name="Response">The incremental generated text for this chunk.</param>
/// <param name="Done">Whether this is the terminal chunk of the stream.</param>
/// <param name="DoneReason">On the terminal chunk, the reason generation stopped.</param>
/// <param name="TotalDuration">Total request duration in nanoseconds (terminal chunk only).</param>
/// <param name="LoadDuration">Model load duration in nanoseconds (terminal chunk only).</param>
/// <param name="PromptEvalCount">Number of prompt tokens evaluated (terminal chunk only).</param>
/// <param name="PromptEvalDuration">Prompt evaluation duration in nanoseconds (terminal chunk only).</param>
/// <param name="EvalCount">Number of generated tokens (terminal chunk only).</param>
/// <param name="EvalDuration">Generation duration in nanoseconds (terminal chunk only).</param>
/// <param name="Thinking">
/// The model's reasoning (chain-of-thought) text when it streams a separate reasoning channel;
/// <see langword="null"/> (and omitted) otherwise. Mirrors <see cref="OllamaChatMessage.Thinking"/>.
/// </param>
/// <param name="Logprobs">
/// Token log-probabilities for the generated tokens when log-probabilities were requested;
/// <see langword="null"/> (and omitted) otherwise.
/// </param>
sealed record OllamaGenerateResponse(
	[property: JsonPropertyName("model")]                string    Model,
	[property: JsonPropertyName("created_at")]           string    CreatedAt,
	[property: JsonPropertyName("response")]             string    Response,
	[property: JsonPropertyName("done")]                 bool      Done,
	[property: JsonPropertyName("done_reason")]          string?   DoneReason         = null,
	[property: JsonPropertyName("total_duration")]       long?     TotalDuration      = null,
	[property: JsonPropertyName("load_duration")]        long?     LoadDuration       = null,
	[property: JsonPropertyName("prompt_eval_count")]    int?      PromptEvalCount    = null,
	[property: JsonPropertyName("prompt_eval_duration")] long?     PromptEvalDuration = null,
	[property: JsonPropertyName("eval_count")]           int?      EvalCount          = null,
	[property: JsonPropertyName("eval_duration")]        long?     EvalDuration       = null,
	[property: JsonPropertyName("thinking")]             string?   Thinking           = null,
	[property: JsonPropertyName("logprobs")]             JsonNode? Logprobs           = null);
