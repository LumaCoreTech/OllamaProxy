// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OllamaProxy.Contracts.Ollama;

/// <summary>
/// Request body for the Ollama <c>POST /api/chat</c> endpoint. This is the inbound contract the
/// proxy exposes to Ollama-aware clients (e.g. GitHub Copilot Chat, Continue.dev).
/// </summary>
/// <param name="Model">
/// The model name as known to the client. The proxy resolves this to a backend and an upstream
/// model name via the model registry.
/// </param>
/// <param name="Messages">The ordered conversation history.</param>
/// <param name="Tools">
/// Optional tool/function definitions the model may call. Presence of this array is what enables
/// tool calling in clients such as Copilot.
/// </param>
/// <param name="Format">
/// Optional structured-output directive. Either the literal string <c>"json"</c> or a JSON schema
/// object. Modeled as a raw <see cref="JsonNode"/> because both shapes are valid.
/// </param>
/// <param name="Options">Optional generation parameters (temperature, sampling, token limits).</param>
/// <param name="Stream">
/// Whether the response is streamed as newline-delimited JSON. Defaults to <see langword="true"/>
/// in the Ollama API when omitted; the proxy applies the same default during binding.
/// </param>
/// <param name="Think">
/// Optional reasoning directive. Ollama accepts either a boolean (<c>true</c> enables thinking,
/// <c>false</c> disables it) or a level string (<c>"low"</c>, <c>"medium"</c>, <c>"high"</c>).
/// Modeled as a raw <see cref="JsonNode"/> because both shapes are valid; the proxy resolves it to a
/// neutral reasoning effort and lets each provider map it onto its own wire dialect. When omitted, the
/// backend's configured default applies, and if that too is unset no reasoning directive is sent.
/// </param>
/// <param name="KeepAlive">
/// Optional model keep-alive hint. Accepted for compatibility and ignored by upstream
/// OpenAI-compatible backends, which manage their own model lifecycle.
/// </param>
/// <param name="Logprobs">
/// Optional flag to include token log-probabilities in the response.
/// </param>
/// <param name="TopLogprobs">
/// Optional flag to include top token log-probabilities in the response.
/// </param>
sealed record OllamaChatRequest(
	[property: JsonPropertyName("model")]        string                           Model,
	[property: JsonPropertyName("messages")]     IReadOnlyList<OllamaChatMessage> Messages,
	[property: JsonPropertyName("tools")]        IReadOnlyList<OllamaTool>?       Tools       = null,
	[property: JsonPropertyName("format")]       JsonNode?                        Format      = null,
	[property: JsonPropertyName("options")]      OllamaOptions?                   Options     = null,
	[property: JsonPropertyName("stream")]       bool?                            Stream      = null,
	[property: JsonPropertyName("think")]        JsonNode?                        Think       = null,
	[property: JsonPropertyName("keep_alive")]   JsonNode?                        KeepAlive   = null,
	[property: JsonPropertyName("logprobs")]     bool?                            Logprobs    = null,
	[property: JsonPropertyName("top_logprobs")] int?                             TopLogprobs = null);

/// <summary>
/// A single message in an Ollama conversation.
/// </summary>
/// <param name="Role">The message author role: <c>system</c>, <c>user</c>, <c>assistant</c>, or <c>tool</c>.</param>
/// <param name="Content">The textual content. May be empty for assistant messages that only carry tool calls.</param>
/// <param name="Images">
/// Optional list of base64-encoded images for multimodal input. Maps to OpenAI image content parts.
/// </param>
/// <param name="ToolCalls">Optional tool calls emitted by the assistant.</param>
/// <param name="ToolName">
/// For <c>tool</c>-role messages, the name of the tool whose result this message carries.
/// </param>
/// <param name="ToolCallId">
/// For <c>tool</c>-role messages, the id of the assistant tool call this result answers. Carried so the
/// proxy can correlate a result with the exact call it belongs to, which the tool name alone cannot do
/// when several calls to the same tool are outstanding (parallel tool calls). When absent, the proxy
/// falls back to correlating by <see cref="ToolName"/>. <see langword="null"/> (and omitted) when the
/// client does not supply one.
/// </param>
/// <param name="Thinking">
/// The assistant's reasoning (chain-of-thought) text, surfaced under Ollama's native <c>thinking</c>
/// field. Populated for models that stream a separate reasoning channel; <see langword="null"/> (and
/// therefore omitted from the response) when the model does not reason.
/// </param>
sealed record OllamaChatMessage(
	[property: JsonPropertyName("role")]         string                         Role,
	[property: JsonPropertyName("content")]      string                         Content,
	[property: JsonPropertyName("images")]       IReadOnlyList<string>?         Images     = null,
	[property: JsonPropertyName("tool_calls")]   IReadOnlyList<OllamaToolCall>? ToolCalls  = null,
	[property: JsonPropertyName("tool_name")]    string?                        ToolName   = null,
	[property: JsonPropertyName("tool_call_id")] string?                        ToolCallId = null,
	[property: JsonPropertyName("thinking")]     string?                        Thinking   = null);

/// <summary>
/// A tool call emitted by the assistant. Note that Ollama represents call arguments as a structured
/// JSON object, in contrast to OpenAI which encodes them as a JSON-formatted string.
/// </summary>
/// <param name="Function">The function invocation details.</param>
/// <param name="Id">
/// The provider-assigned call id used to correlate this call with the matching tool-result message the
/// client returns (under <see cref="OllamaChatMessage.ToolCallId"/>). Present in the Ollama wire schema;
/// <see langword="null"/> (and omitted) when the backend does not supply one.
/// </param>
sealed record OllamaToolCall(
	[property: JsonPropertyName("function")] OllamaToolCallFunction Function,
	[property: JsonPropertyName("id")]       string?                Id = null);

/// <summary>
/// The function portion of an Ollama tool call.
/// </summary>
/// <param name="Name">The name of the function to invoke.</param>
/// <param name="Description">
/// An optional human-readable description echoed alongside the call. Present in the Ollama wire schema but typically
/// absent for calls translated from OpenAI-compatible backends, which do not carry a description on tool calls.
/// </param>
/// <param name="Arguments">The call arguments as a structured JSON object.</param>
sealed record OllamaToolCallFunction(
	[property: JsonPropertyName("name")]        string    Name,
	[property: JsonPropertyName("description")] string?   Description,
	[property: JsonPropertyName("arguments")]   JsonNode? Arguments);

/// <summary>
/// A tool/function definition advertised to the model.
/// </summary>
/// <param name="Type">The tool type. Currently, always <c>function</c>.</param>
/// <param name="Function">The function schema.</param>
sealed record OllamaTool(
	[property: JsonPropertyName("type")]     string             Type,
	[property: JsonPropertyName("function")] OllamaToolFunction Function);

/// <summary>
/// The schema of a callable function advertised to the model.
/// </summary>
/// <param name="Name">The function name.</param>
/// <param name="Description">A human-readable description guiding when to call the function.</param>
/// <param name="Parameters">The JSON-schema parameter definition.</param>
sealed record OllamaToolFunction(
	[property: JsonPropertyName("name")]        string    Name,
	[property: JsonPropertyName("description")] string?   Description,
	[property: JsonPropertyName("parameters")]  JsonNode? Parameters);

/// <summary>
/// A single object in the newline-delimited JSON stream returned by <c>POST /api/chat</c>. Each
/// chunk carries an incremental <see cref="Message"/>; the terminal chunk additionally sets
/// <see cref="Done"/> and the timing/token accounting fields.
/// </summary>
/// <param name="Model">The model name echoed back to the client.</param>
/// <param name="CreatedAt">The ISO-8601 timestamp the chunk was produced.</param>
/// <param name="Message">The incremental assistant message for this chunk.</param>
/// <param name="Done">Whether this is the terminal chunk of the stream.</param>
/// <param name="DoneReason">
/// On the terminal chunk, the reason generation stopped (e.g. <c>stop</c>, <c>length</c>).
/// </param>
/// <param name="TotalDuration">Total request duration in nanoseconds (terminal chunk only).</param>
/// <param name="LoadDuration">Model load duration in nanoseconds (terminal chunk only).</param>
/// <param name="PromptEvalCount">Number of prompt tokens evaluated (terminal chunk only).</param>
/// <param name="PromptEvalDuration">Prompt evaluation duration in nanoseconds (terminal chunk only).</param>
/// <param name="EvalCount">Number of generated tokens (terminal chunk only).</param>
/// <param name="EvalDuration">Generation duration in nanoseconds (terminal chunk only).</param>
/// <param name="Logprobs">Token log-probabilities for the generated tokens when logprobs are enabled.</param>
sealed record OllamaChatResponse(
	[property: JsonPropertyName("model")]                string            Model,
	[property: JsonPropertyName("created_at")]           string            CreatedAt,
	[property: JsonPropertyName("message")]              OllamaChatMessage Message,
	[property: JsonPropertyName("done")]                 bool              Done,
	[property: JsonPropertyName("done_reason")]          string?           DoneReason         = null,
	[property: JsonPropertyName("total_duration")]       long?             TotalDuration      = null,
	[property: JsonPropertyName("load_duration")]        long?             LoadDuration       = null,
	[property: JsonPropertyName("prompt_eval_count")]    int?              PromptEvalCount    = null,
	[property: JsonPropertyName("prompt_eval_duration")] long?             PromptEvalDuration = null,
	[property: JsonPropertyName("eval_count")]           int?              EvalCount          = null,
	[property: JsonPropertyName("eval_duration")]        long?             EvalDuration       = null,
	[property: JsonPropertyName("logprobs")]             JsonNode?         Logprobs           = null);
