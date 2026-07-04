// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OllamaProxy.Providers.OpenAiProtocol.Contracts;

/// <summary>
/// Request body for the OpenAI <c>POST /v1/chat/completions</c> endpoint, sent upstream after an inbound
/// Ollama chat request is translated.
/// </summary>
/// <remarks>
/// This shared record carries only fields that are part of the OpenAI specification, so it is safe to
/// send to any compliant backend. Non-standard sampling extensions (<c>top_k</c>, <c>min_p</c>) are
/// deliberately absent: a provider whose backend honors them stamps them on per request through its
/// sampling-extension seam, so the strict OpenAI dialect never carries a field an official endpoint
/// would reject. The type itself is therefore the filter: there is no list of forbidden fields to keep
/// in sync.
/// </remarks>
/// <param name="Model">The upstream model identifier resolved from the requested model name.</param>
/// <param name="Messages">The translated conversation history.</param>
/// <param name="Stream">Whether to request a streamed (SSE) response.</param>
/// <param name="StreamOptions">
/// Streaming options. Set <c>include_usage</c> so the final streamed chunk carries token usage,
/// which the proxy maps onto Ollama's token-accounting fields.
/// </param>
/// <param name="Tools">Translated tool/function definitions, if any.</param>
/// <param name="ResponseFormat">Structured-output directive derived from the Ollama <c>format</c> field.</param>
/// <param name="Temperature">Sampling temperature.</param>
/// <param name="TopP">Nucleus sampling probability mass.</param>
/// <param name="Seed">Deterministic sampling seed.</param>
/// <param name="MaxCompletionTokens">
/// Upper bound on the number of tokens generated for the completion, including visible output and
/// reasoning tokens. This is the OpenAI spec's current field; it replaces the legacy <c>max_tokens</c>,
/// which the specification marks deprecated and explicitly notes is not compatible with reasoning
/// (o-series) models, the reason the proxy emits the modern field instead.
/// </param>
/// <param name="Stop">Stop sequences.</param>
/// <param name="FrequencyPenalty">Frequency penalty.</param>
/// <param name="PresencePenalty">Presence penalty.</param>
/// <param name="Logprobs">When <see langword="true"/>, requests token log-probabilities in the response.</param>
/// <param name="TopLogprobs">
/// Number of most likely tokens to return at each position. OpenAI requires <see cref="Logprobs"/> to be
/// <see langword="true"/> when this is set.
/// </param>
sealed record OpenAiChatRequest(
	[property: JsonPropertyName("model")]                 string                           Model,
	[property: JsonPropertyName("messages")]              IReadOnlyList<OpenAiChatMessage> Messages,
	[property: JsonPropertyName("stream")]                bool                             Stream,
	[property: JsonPropertyName("stream_options")]        OpenAiStreamOptions?             StreamOptions       = null,
	[property: JsonPropertyName("tools")]                 IReadOnlyList<OpenAiTool>?       Tools               = null,
	[property: JsonPropertyName("response_format")]       JsonNode?                        ResponseFormat      = null,
	[property: JsonPropertyName("temperature")]           double?                          Temperature         = null,
	[property: JsonPropertyName("top_p")]                 double?                          TopP                = null,
	[property: JsonPropertyName("seed")]                  int?                             Seed                = null,
	[property: JsonPropertyName("max_completion_tokens")] int?                             MaxCompletionTokens = null,
	[property: JsonPropertyName("stop")]                  IReadOnlyList<string>?           Stop                = null,
	[property: JsonPropertyName("frequency_penalty")]     double?                          FrequencyPenalty    = null,
	[property: JsonPropertyName("presence_penalty")]      double?                          PresencePenalty     = null,
	[property: JsonPropertyName("logprobs")]              bool?                            Logprobs            = null,
	[property: JsonPropertyName("top_logprobs")]          int?                             TopLogprobs         = null);

/// <summary>
/// Streaming options for an OpenAI chat completion request.
/// </summary>
/// <param name="IncludeUsage">
/// When <see langword="true"/>, the terminal streamed chunk includes a <c>usage</c> object with
/// token counts. Required so the proxy can populate Ollama's <c>prompt_eval_count</c>/<c>eval_count</c>.
/// </param>
sealed record OpenAiStreamOptions([property: JsonPropertyName("include_usage")] bool IncludeUsage);

/// <summary>
/// A single message in an OpenAI chat request or response. The <see cref="Content"/> field is a raw
/// <see cref="JsonNode"/> because OpenAI accepts both a plain string and an array of typed content
/// parts (text and image parts for multimodal input).
/// </summary>
/// <param name="Role">The author role: <c>system</c>, <c>user</c>, <c>assistant</c>, or <c>tool</c>.</param>
/// <param name="Content">
/// A string or an array of content parts. May be <see langword="null"/> for tool-call-only
/// assistant messages.
/// </param>
/// <param name="ToolCalls">Tool calls emitted by the assistant, if any.</param>
/// <param name="ToolCallId">For <c>tool</c>-role messages, the id of the call this result answers.</param>
/// <param name="Name">Optional participant name. Used to carry a tool name where applicable.</param>
/// <param name="ReasoningContent">
/// The assistant's reasoning (chain-of-thought) text on response messages and stream deltas. This is
/// the de-facto standard field emitted by DeepSeek, vLLM, and llama.cpp. Always <see langword="null"/>
/// on outgoing request messages, so it is never sent upstream.
/// </param>
/// <param name="Reasoning">
/// OpenRouter's alternative spelling of <see cref="ReasoningContent"/>, carrying the same chain-of-thought
/// stream. Read as a fallback when <see cref="ReasoningContent"/> is absent.
/// </param>
/// <param name="ReasoningDetails">
/// The provider's opaque reasoning-details array (Venice and OpenRouter), carrying encrypted reasoning
/// blocks / thought signatures the model expects replayed verbatim on the follow-up request of a multi-turn
/// tool-call conversation. The proxy treats it as an uninterpreted <see cref="JsonNode"/>: it is read off a
/// response message to be cached and re-attached, never inspected. <see langword="null"/> (and, since null
/// values are omitted, absent from the wire) on every request message the typed mapper builds. Re-attachment
/// happens by raw-key stamping on the payload, not through this record.
/// </param>
sealed record OpenAiChatMessage(
	[property: JsonPropertyName("role")]              string                         Role,
	[property: JsonPropertyName("content")]           JsonNode?                      Content,
	[property: JsonPropertyName("tool_calls")]        IReadOnlyList<OpenAiToolCall>? ToolCalls        = null,
	[property: JsonPropertyName("tool_call_id")]      string?                        ToolCallId       = null,
	[property: JsonPropertyName("name")]              string?                        Name             = null,
	[property: JsonPropertyName("reasoning_content")] string?                        ReasoningContent = null,
	[property: JsonPropertyName("reasoning")]         string?                        Reasoning        = null,
	[property: JsonPropertyName("reasoning_details")] JsonNode?                      ReasoningDetails = null);

/// <summary>
/// A tool call emitted by the assistant. OpenAI encodes the call arguments as a JSON-formatted
/// string (in contrast to Ollama, which uses a structured object).
/// </summary>
/// <param name="Id">The call id, echoed back by the client when returning the tool result.</param>
/// <param name="Type">The tool type. Currently always <c>function</c>.</param>
/// <param name="Function">The function invocation details.</param>
/// <param name="Index">
/// The call index within a streamed delta. Present only in streaming chunks, where it is used to
/// reassemble argument fragments that arrive across multiple deltas.
/// </param>
sealed record OpenAiToolCall(
	[property: JsonPropertyName("id")]       string?                 Id,
	[property: JsonPropertyName("type")]     string?                 Type,
	[property: JsonPropertyName("function")] OpenAiToolCallFunction? Function,
	[property: JsonPropertyName("index")]    int?                    Index = null);

/// <summary>
/// The function portion of an OpenAI tool call.
/// </summary>
/// <param name="Name">The function name. May be <see langword="null"/> in argument-only stream deltas.</param>
/// <param name="Arguments">The call arguments as a JSON-formatted string (possibly a fragment while streaming).</param>
sealed record OpenAiToolCallFunction(
	[property: JsonPropertyName("name")]      string? Name,
	[property: JsonPropertyName("arguments")] string? Arguments);

/// <summary>
/// A tool/function definition advertised to the model.
/// </summary>
/// <param name="Type">The tool type. Currently, always <c>function</c>.</param>
/// <param name="Function">The function schema.</param>
sealed record OpenAiTool(
	[property: JsonPropertyName("type")]     string             Type,
	[property: JsonPropertyName("function")] OpenAiToolFunction Function);

/// <summary>
/// The schema of a callable function advertised to the model.
/// </summary>
/// <param name="Name">The function name.</param>
/// <param name="Description">A human-readable description guiding when to call the function.</param>
/// <param name="Parameters">The JSON-schema parameter definition.</param>
sealed record OpenAiToolFunction(
	[property: JsonPropertyName("name")]        string    Name,
	[property: JsonPropertyName("description")] string?   Description,
	[property: JsonPropertyName("parameters")]  JsonNode? Parameters);
