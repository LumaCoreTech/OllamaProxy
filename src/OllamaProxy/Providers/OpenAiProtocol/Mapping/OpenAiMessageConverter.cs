// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using OllamaProxy.Contracts.Ollama;
using OllamaProxy.Providers.OpenAiProtocol.Contracts;

namespace OllamaProxy.Providers.OpenAiProtocol.Mapping;

/// <summary>
/// Shared, stateless conversions from OpenAI message shapes back to the Ollama format. These helpers
/// are used by both the non-streaming response mapper and the streaming translator so that content
/// extraction, tool-call argument parsing, and finish-reason normalization behave identically in both
/// paths.
/// </summary>
static class OpenAiMessageConverter
{
	/// <summary>
	/// Extracts plain text from an OpenAI <c>content</c> value, which may be a bare string, an array
	/// of typed content parts (text parts are concatenated, non-text parts ignored), or absent.
	/// </summary>
	/// <param name="content">The raw OpenAI content node, if any.</param>
	/// <returns>The concatenated textual content; an empty string when there is none.</returns>
	public static string ExtractText(JsonNode? content)
	{
		switch (content)
		{
			case null:
				return string.Empty;

			case JsonValue value when value.TryGetValue(out string? text):
				return text ?? string.Empty;

			case JsonArray parts:
				StringBuilder builder = new();
				foreach (JsonNode? part in parts)
				{
					if (part is JsonObject obj &&
					    obj.TryGetPropertyValue("type", out JsonNode? type) &&
					    type?.GetValue<string>() == "text" &&
					    obj.TryGetPropertyValue("text", out JsonNode? partText))
						builder.Append(partText?.GetValue<string>());
				}

				return builder.ToString();

			default:
				return string.Empty;
		}
	}

	/// <summary>
	/// Extracts the reasoning (chain-of-thought) text from an OpenAI message, preferring the de-facto
	/// standard <see cref="OpenAiChatMessage.ReasoningContent"/> field and falling back to OpenRouter's
	/// <see cref="OpenAiChatMessage.Reasoning"/> spelling. Returns <see langword="null"/> when the
	/// message carries no reasoning, so callers can omit the field rather than emit an empty one.
	/// </summary>
	/// <param name="message">The OpenAI message to read, if any.</param>
	/// <returns>The reasoning text, or <see langword="null"/> when none is present.</returns>
	public static string? ExtractReasoning(OpenAiChatMessage? message)
	{
		string? reasoning = message?.ReasoningContent ?? message?.Reasoning;
		return string.IsNullOrEmpty(reasoning) ? null : reasoning;
	}

	/// <summary>
	/// Translates an OpenAI choice <c>logprobs</c> object into Ollama's shape. OpenAI nests the per-token
	/// entries under a <c>content</c> array, whereas Ollama exposes that array directly; this unwraps the
	/// <c>content</c> member (the per-element shape, <c>token</c>, <c>logprob</c>, <c>bytes</c>,
	/// <c>top_logprobs</c>, is identical between the two). The array is deep-cloned so it is detached from
	/// the source document and can be re-parented onto the outgoing Ollama response. Returns
	/// <see langword="null"/> when no log-probabilities are present, so callers omit the field.
	/// </summary>
	/// <param name="logprobs">The raw OpenAI choice <c>logprobs</c> node, if any.</param>
	/// <returns>The detached Ollama log-probabilities array, or <see langword="null"/> when none are present.</returns>
	public static JsonNode? ExtractLogprobs(JsonNode? logprobs)
	{
		if (logprobs is JsonObject obj &&
		    obj.TryGetPropertyValue("content", out JsonNode? content) &&
		    content is JsonArray array)
			return array.DeepClone();

		return null;
	}

	/// <summary>
	/// Converts OpenAI tool calls (whose arguments are a JSON-formatted string) into Ollama tool calls
	/// (whose arguments are a structured object), carrying the call id through so the client can correlate
	/// the result it later returns. Argument strings that fail to parse are wrapped as a raw string node so
	/// no information is lost. Returns <see langword="null"/> when there are none.
	/// </summary>
	/// <param name="toolCalls">The OpenAI tool calls to convert, if any.</param>
	/// <returns>The converted Ollama tool calls, or <see langword="null"/>.</returns>
	public static IReadOnlyList<OllamaToolCall>? ConvertToolCalls(IReadOnlyList<OpenAiToolCall>? toolCalls)
	{
		if (toolCalls is not { Count: > 0 }) return null;

		List<OllamaToolCall> converted = new(toolCalls.Count);
		foreach (OpenAiToolCall call in toolCalls)
		{
			string name = call.Function?.Name ?? string.Empty;
			// OpenAI tool calls carry no description; only name and arguments are echoed back. The call id
			// is carried through so the client can correlate the result it later returns with this call.
			converted.Add(
				new OllamaToolCall(
					new OllamaToolCallFunction(
						name,
						Description: null,
						Arguments: ParseArgumentsOrEmpty(call.Function?.Arguments)),
					Id: string.IsNullOrEmpty(call.Id) ? null : call.Id));
		}

		return converted;
	}

	/// <summary>
	/// Normalizes an OpenAI <c>finish_reason</c> into an Ollama <c>done_reason</c>. OpenAI reports
	/// <c>tool_calls</c> when the model stops to call a tool; Ollama represents that terminal state as
	/// the ordinary <c>stop</c> reason. Other values pass through unchanged.
	/// </summary>
	/// <param name="finishReason">The OpenAI finish reason, if reported.</param>
	/// <returns>The Ollama done reason.</returns>
	public static string MapFinishReason(string? finishReason) => finishReason switch
	{
		null         => "stop",
		"tool_calls" => "stop",
		var _        => finishReason
	};

	/// <summary>
	/// Parses a tool-call argument string into a JSON node, falling back to a string node when the
	/// payload is not valid JSON (for example a partially streamed fragment that was flushed as-is).
	/// Shared by the non-streaming converter and the streaming accumulator so both treat malformed
	/// arguments identically.
	/// </summary>
	/// <param name="arguments">The raw argument string, if any.</param>
	/// <returns>The parsed arguments node; an empty object when no arguments were supplied.</returns>
	public static JsonNode ParseArgumentsOrEmpty(string? arguments)
	{
		if (string.IsNullOrWhiteSpace(arguments)) return new JsonObject();

		try
		{
			return JsonNode.Parse(arguments) ?? new JsonObject();
		}
		catch (JsonException)
		{
			return JsonValue.Create(arguments);
		}
	}
}
