// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Contracts.Ollama;
using OllamaProxy.Providers.OpenAiProtocol.Contracts;

namespace OllamaProxy.Providers.OpenAiProtocol.Mapping;

/// <summary>
/// Translates a non-streaming OpenAI <see cref="OpenAiChatCompletion"/> into the single aggregated
/// <see cref="OllamaChatResponse"/> the proxy returns to the client. The first choice is used; its
/// content and tool calls are converted via <see cref="OpenAiMessageConverter"/>, the finish reason
/// is normalized, and token usage is projected onto the Ollama accounting fields. Timing fields that
/// OpenAI does not report are supplied by the caller, which measures the wall-clock duration.
/// </summary>
static class OpenAiResponseMapper
{
	/// <summary>
	/// Maps a completed OpenAI chat completion to an Ollama chat response.
	/// </summary>
	/// <param name="completion">The upstream completion to translate.</param>
	/// <param name="model">The client-facing model name to echo back.</param>
	/// <param name="createdAt">The ISO-8601 timestamp to stamp on the response.</param>
	/// <param name="totalDuration">The measured total request duration in nanoseconds.</param>
	/// <returns>The aggregated Ollama chat response.</returns>
	public static OllamaChatResponse MapCompletion(
		OpenAiChatCompletion completion,
		string               model,
		string               createdAt,
		long                 totalDuration)
	{
		ArgumentNullException.ThrowIfNull(completion);

		OpenAiChatChoice? choice = completion.Choices is { Count: > 0 } choices ? choices[0] : null;
		OpenAiChatMessage? message = choice?.Message;

		OllamaChatMessage ollamaMessage = new(
			Role: message?.Role ?? "assistant",
			Content: OpenAiMessageConverter.ExtractText(message?.Content),
			ToolCalls: OpenAiMessageConverter.ConvertToolCalls(message?.ToolCalls),
			Thinking: OpenAiMessageConverter.ExtractReasoning(message));

		return new OllamaChatResponse(
			Model: model,
			CreatedAt: createdAt,
			Message: ollamaMessage,
			Done: true,
			DoneReason: OpenAiMessageConverter.MapFinishReason(choice?.FinishReason),
			TotalDuration: totalDuration,
			PromptEvalCount: completion.Usage?.PromptTokens,
			EvalCount: completion.Usage?.CompletionTokens,
			Logprobs: OpenAiMessageConverter.ExtractLogprobs(choice?.Logprobs));
	}
}
