// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

using OllamaProxy.Contracts.Ollama;
using OllamaProxy.Providers.OpenAiProtocol.Contracts;
using OllamaProxy.Providers.OpenAiProtocol.Mapping;

namespace OllamaProxy.Providers.OpenAiProtocol.Streaming;

/// <summary>
/// Translates a parsed OpenAI chat-completion chunk stream into the Ollama newline-delimited JSON
/// response stream. Each upstream content delta becomes a non-terminal Ollama chunk carrying the
/// incremental text; tool-call fragments are buffered by a <see cref="StreamingToolCallAccumulator"/>
/// and surfaced on the terminal chunk, which also carries <c>done</c>, the mapped done reason, the
/// measured total duration, and the token accounting from the upstream usage object. Cancellation
/// flows through from the source sequence so a client disconnect stops the translation.
/// </summary>
static class OpenAiStreamTranslator
{
	/// <summary>
	/// Projects the upstream chunk sequence onto the Ollama response sequence, emitting incremental
	/// content chunks followed by exactly one terminal <c>done</c> chunk.
	/// </summary>
	/// <param name="chunks">The parsed upstream chunk sequence.</param>
	/// <param name="model">The client-facing model name to echo on every emitted chunk.</param>
	/// <param name="timestampProvider">
	/// Supplies the ISO-8601 <c>created_at</c> stamp for each emitted chunk; injected so timing is
	/// deterministic under test.
	/// </param>
	/// <param name="elapsedNanosecondsProvider">
	/// Supplies the elapsed wall-clock duration in nanoseconds for the terminal chunk's
	/// <c>total_duration</c>; injected for deterministic testing.
	/// </param>
	/// <param name="cancellationToken">A token observed while consuming the source sequence.</param>
	/// <returns>An asynchronous sequence of Ollama response chunks ending with a terminal chunk.</returns>
	public static async IAsyncEnumerable<OllamaChatResponse> TranslateAsync(
		IAsyncEnumerable<OpenAiChatCompletionChunk> chunks,
		string                                      model,
		Func<string>                                timestampProvider,
		Func<long>                                  elapsedNanosecondsProvider,
		[EnumeratorCancellation] CancellationToken  cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(chunks);
		ArgumentNullException.ThrowIfNull(timestampProvider);
		ArgumentNullException.ThrowIfNull(elapsedNanosecondsProvider);

		StreamingToolCallAccumulator toolCalls = new();
		OpenAiUsage? usage = null;
		string? finishReason = null;
		string role = "assistant";

		// Log-probabilities arrive per delta (one slice per content chunk). They are concatenated in arrival
		// order into a single array so the terminal chunk reports them in the same shape as a non-streamed
		// response; left null until the first slice appears so non-logprob streams omit the field.
		JsonArray? logprobs = null;

		await foreach (OpenAiChatCompletionChunk chunk in chunks
			               .WithCancellation(cancellationToken)
			               .ConfigureAwait(false))
		{
			// The terminal usage-only chunk carries no choices; capture its accounting and move on.
			if (chunk.Usage is not null) usage = chunk.Usage;

			OpenAiChatChunkChoice? choice = chunk.Choices is { Count: > 0 } choices ? choices[0] : null;
			OpenAiChatMessage? delta = choice?.Delta;

			if (choice?.FinishReason is not null) finishReason = choice.FinishReason;

			// Accumulate this delta's log-probabilities (if any) before the early-continue on a null delta, so
			// a logprob-only choice is not dropped.
			if (OpenAiMessageConverter.ExtractLogprobs(choice?.Logprobs) is JsonArray slice)
			{
				logprobs ??= [];
				foreach (JsonNode? entry in slice) logprobs.Add(entry?.DeepClone());
			}

			if (delta is null) continue;

			if (!string.IsNullOrEmpty(delta.Role)) role = delta.Role;

			toolCalls.Accumulate(delta.ToolCalls);

			string text = OpenAiMessageConverter.ExtractText(delta.Content);
			string? thinking = OpenAiMessageConverter.ExtractReasoning(delta);

			// Surface a reasoning delta as its own Ollama chunk carrying only the thinking text, mirroring
			// how native Ollama streams its separate reasoning channel ahead of the visible answer.
			if (thinking is not null)
			{
				yield return new OllamaChatResponse(
					Model: model,
					CreatedAt: timestampProvider(),
					Message: new OllamaChatMessage(role, string.Empty, Thinking: thinking),
					Done: false);
			}

			// Emit an incremental chunk only when this delta actually advanced the textual content;
			// tool-call fragments are withheld until the terminal chunk, mirroring Ollama's shape.
			if (text.Length > 0)
			{
				yield return new OllamaChatResponse(
					Model: model,
					CreatedAt: timestampProvider(),
					Message: new OllamaChatMessage(role, text),
					Done: false);
			}
		}

		yield return new OllamaChatResponse(
			Model: model,
			CreatedAt: timestampProvider(),
			Message: new OllamaChatMessage(role, string.Empty, ToolCalls: toolCalls.Build()),
			Done: true,
			DoneReason: OpenAiMessageConverter.MapFinishReason(finishReason),
			TotalDuration: elapsedNanosecondsProvider(),
			PromptEvalCount: usage?.PromptTokens,
			EvalCount: usage?.CompletionTokens,
			Logprobs: logprobs);
	}
}
