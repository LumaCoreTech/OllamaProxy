// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json.Nodes;

using OllamaProxy.Contracts.Ollama;
using OllamaProxy.Providers.OpenAiProtocol.Contracts;

namespace OllamaProxy.Providers.OpenAiProtocol.Mapping;

/// <summary>
/// Translates an inbound <see cref="OllamaChatRequest"/> into the OpenAI
/// <see cref="OpenAiChatRequest"/> wire format. The mapper is pure and stateless: it copies the
/// resolved upstream model, projects each message (carrying multimodal images, assistant tool calls,
/// and tool results across the differing shapes), maps the Ollama <c>options</c> onto the standard
/// OpenAI sampling fields, forwards tool definitions, and converts the <c>format</c> directive into a
/// <c>response_format</c> object. It maps only specification fields; non-standard sampling extensions
/// (<c>top_k</c>, <c>min_p</c>) are intentionally not mapped here and are stamped on per provider by
/// the sampling-extension seam, so the shared output stays safe for a strict OpenAI backend.
/// </summary>
static class OpenAiRequestMapper
{
	/// <summary>The Ollama sentinel for <c>num_predict</c> meaning "generate until context is full".</summary>
	private const int UnlimitedTokens = -1;

	/// <summary>
	/// Builds the OpenAI chat request for the supplied inbound request, upstream model, and streaming
	/// mode. When streaming, usage reporting is requested so token accounting reaches the final chunk.
	/// </summary>
	/// <param name="request">The inbound Ollama chat request to translate.</param>
	/// <param name="upstreamModel">The resolved upstream model identifier to target.</param>
	/// <param name="stream">Whether a streamed response is requested.</param>
	/// <returns>The translated OpenAI chat request.</returns>
	public static OpenAiChatRequest MapRequest(OllamaChatRequest request, string upstreamModel, bool stream)
	{
		ArgumentNullException.ThrowIfNull(request);
		ArgumentException.ThrowIfNullOrWhiteSpace(upstreamModel);

		OllamaOptions options = request.Options ?? new OllamaOptions();

		return new OpenAiChatRequest(
			Model: upstreamModel,
			Messages: request.Messages.Select(MapMessage).ToArray(),
			Stream: stream,
			StreamOptions: stream ? new OpenAiStreamOptions(IncludeUsage: true) : null,
			Tools: MapTools(request.Tools),
			ResponseFormat: MapResponseFormat(request.Format),
			Temperature: options.Temperature,
			TopP: options.TopP,
			Seed: options.Seed,
			MaxCompletionTokens: MapMaxCompletionTokens(options.NumPredict),
			Stop: options.Stop,
			FrequencyPenalty: options.FrequencyPenalty,
			PresencePenalty: options.PresencePenalty,
			Logprobs: request.Logprobs,
			TopLogprobs: request.TopLogprobs);
	}

	/// <summary>
	/// Projects a single Ollama message onto an OpenAI message, choosing the content shape (plain
	/// string or multimodal part array), converting structured tool-call arguments to the
	/// JSON-string form OpenAI expects, and carrying tool-result correlation fields.
	/// </summary>
	/// <param name="message">The Ollama message to translate.</param>
	/// <returns>The translated OpenAI message.</returns>
	private static OpenAiChatMessage MapMessage(OllamaChatMessage message)
	{
		IReadOnlyList<OpenAiToolCall>? toolCalls = message.ToolCalls is { Count: > 0 }
			                                           ? message.ToolCalls.Select(MapToolCall).ToArray()
			                                           : null;

		// OpenAI correlates a tool result with its originating call by id. Prefer the client-supplied
		// tool_call_id; fall back to the tool name when no id was provided, the best-effort correlation
		// value most backends accept, and all a name-only Ollama client can offer.
		bool isToolResult = string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase);
		string? toolCallId = isToolResult
			                     ? string.IsNullOrEmpty(message.ToolCallId) ? message.ToolName : message.ToolCallId
			                     : null;

		return new OpenAiChatMessage(
			Role: message.Role,
			Content: MapContent(message),
			ToolCalls: toolCalls,
			ToolCallId: toolCallId,
			Name: isToolResult ? message.ToolName : null);
	}

	/// <summary>
	/// Builds the OpenAI <c>content</c> value: a plain string when there are no images, or an array of
	/// typed content parts (one text part followed by one <c>image_url</c> part per base64 image) for
	/// multimodal input. Returns <see langword="null"/> for an empty assistant message that only
	/// carries tool calls.
	/// </summary>
	/// <param name="message">The Ollama message whose content is being translated.</param>
	/// <returns>The OpenAI content node, or <see langword="null"/> when there is nothing to send.</returns>
	private static JsonNode? MapContent(OllamaChatMessage message)
	{
		if (message.Images is not { Count: > 0 })
		{
			// An empty string on a tool-call-only assistant message is dropped to null so OpenAI does
			// not treat it as an empty textual answer.
			if (string.IsNullOrEmpty(message.Content) && message.ToolCalls is { Count: > 0 }) return null;

			return message.Content;
		}

		JsonArray parts =
		[
			new JsonObject
			{
				["type"] = "text",
				["text"] = message.Content
			}
		];

		foreach (string image in message.Images)
		{
			// A bare base64 payload is wrapped as a data URL; an already-formed URL is passed through.
			string url = image.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || IsAbsoluteUrl(image)
				             ? image
				             : $"data:image/png;base64,{image}";

			parts.Add(
				new JsonObject
				{
					["type"] = "image_url",
					["image_url"] = new JsonObject
					{
						["url"] = url
					}
				});
		}

		return parts;
	}

	/// <summary>
	/// Converts an Ollama tool call (structured argument object) into an OpenAI tool call (arguments
	/// as a JSON-formatted string), serializing the arguments node to its compact JSON text and
	/// echoing the call id so the backend can correlate the subsequent tool result.
	/// </summary>
	/// <param name="toolCall">The Ollama tool call to translate.</param>
	/// <returns>The translated OpenAI tool call.</returns>
	private static OpenAiToolCall MapToolCall(OllamaToolCall toolCall)
	{
		string arguments = toolCall.Function.Arguments?.ToJsonString() ?? "{}";

		return new OpenAiToolCall(
			// Echo the client-supplied call id so the backend can correlate the later tool result;
			// null when the client did not assign one, which OpenAI tolerates.
			Id: string.IsNullOrEmpty(toolCall.Id) ? null : toolCall.Id,
			Type: "function",
			Function: new OpenAiToolCallFunction(toolCall.Function.Name, arguments));
	}

	/// <summary>
	/// Translates the advertised tool definitions, returning <see langword="null"/> when none are
	/// present so the <c>tools</c> field is omitted entirely.
	/// </summary>
	/// <param name="tools">The Ollama tool definitions, if any.</param>
	/// <returns>The translated OpenAI tool definitions, or <see langword="null"/>.</returns>
	private static OpenAiTool[]? MapTools(IReadOnlyList<OllamaTool>? tools)
	{
		if (tools is not { Count: > 0 }) return null;

		return tools
			.Select(tool => new OpenAiTool(
				tool.Type,
				new OpenAiToolFunction(tool.Function.Name, tool.Function.Description, tool.Function.Parameters)))
			.ToArray();
	}

	/// <summary>
	/// Translates the Ollama <c>format</c> directive into an OpenAI <c>response_format</c>: the literal
	/// <c>"json"</c> becomes <c>json_object</c> mode, and a JSON-schema object becomes a
	/// <c>json_schema</c> directive. Any other value is ignored.
	/// </summary>
	/// <param name="format">The raw Ollama format node, if supplied.</param>
	/// <returns>The OpenAI response-format node, or <see langword="null"/> when not applicable.</returns>
	private static JsonObject? MapResponseFormat(JsonNode? format)
	{
		switch (format)
		{
			case JsonValue value when value.TryGetValue(out string? text):
				return string.Equals(text, "json", StringComparison.OrdinalIgnoreCase)
					       ? new JsonObject { ["type"] = "json_object" }
					       : null;

			case JsonObject schema:
				return new JsonObject
				{
					["type"] = "json_schema",
					["json_schema"] = new JsonObject
					{
						["name"] = "response",
						["strict"] = true,
						["schema"] = schema.DeepClone()
					}
				};

			default:
				return null;
		}
	}

	/// <summary>
	/// Maps the Ollama <c>num_predict</c> token cap onto OpenAI <c>max_completion_tokens</c>, treating
	/// the <c>-1</c> sentinel (and any non-positive value) as "no limit" by omitting the field. The
	/// modern <c>max_completion_tokens</c> field is used rather than the deprecated <c>max_tokens</c>,
	/// which the OpenAI specification notes is not compatible with reasoning (o-series) models.
	/// </summary>
	/// <param name="numPredict">The Ollama token cap, if supplied.</param>
	/// <returns>The OpenAI <c>max_completion_tokens</c> value, or <see langword="null"/> for no limit.</returns>
	private static int? MapMaxCompletionTokens(int? numPredict) =>
		numPredict is null or UnlimitedTokens or <= 0 ? null : numPredict;

	/// <summary>
	/// Determines whether the supplied image reference is already an absolute URL, in which case it is
	/// forwarded unchanged rather than wrapped as a base64 data URL.
	/// </summary>
	/// <param name="value">The image reference to test.</param>
	/// <returns><see langword="true"/> when the value is an absolute URI; otherwise <see langword="false"/>.</returns>
	private static bool IsAbsoluteUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
	                                                   (uri.Scheme == Uri.UriSchemeHttp ||
	                                                    uri.Scheme == Uri.UriSchemeHttps);
}
