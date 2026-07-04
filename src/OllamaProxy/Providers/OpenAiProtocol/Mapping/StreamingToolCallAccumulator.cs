// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text;
using System.Text.Json.Nodes;

using OllamaProxy.Contracts.Ollama;
using OllamaProxy.Providers.OpenAiProtocol.Contracts;

namespace OllamaProxy.Providers.OpenAiProtocol.Mapping;

/// <summary>
/// Reassembles streamed OpenAI tool calls into complete Ollama tool calls. OpenAI delivers a tool
/// call across several stream deltas: the first delta carries the call <c>index</c>, <c>id</c>, and
/// function name, and subsequent deltas append fragments of the JSON argument string for the same
/// index. This accumulator buffers those fragments per index and, once the stream completes, parses
/// each finished argument string into the structured object shape Ollama expects. The type is
/// single-consumer and not thread-safe; one instance backs one in-flight stream.
/// </summary>
sealed class StreamingToolCallAccumulator
{
	// Keyed by the OpenAI tool-call index so fragments arriving across deltas land in the right slot;
	// sorted so the assembled calls preserve their original order.
	private readonly SortedDictionary<int, PartialToolCall> mCalls = [];

	/// <summary>
	/// Gets a value indicating whether any tool-call fragments have been observed on the stream.
	/// </summary>
	public bool HasToolCalls => mCalls.Count > 0;

	/// <summary>
	/// Incorporates the tool-call fragments from a single streamed delta, creating a buffer for a
	/// newly seen index and appending name/argument fragments to existing buffers.
	/// </summary>
	/// <param name="toolCalls">The tool-call deltas from the current chunk, if any.</param>
	public void Accumulate(IReadOnlyList<OpenAiToolCall>? toolCalls)
	{
		if (toolCalls is not { Count: > 0 }) return;

		foreach (OpenAiToolCall delta in toolCalls)
		{
			// A delta without an explicit index still belongs to the first (index 0) call.
			int index = delta.Index ?? 0;

			if (!mCalls.TryGetValue(index, out PartialToolCall? partial))
			{
				partial = new PartialToolCall();
				mCalls[index] = partial;
			}

			if (!string.IsNullOrEmpty(delta.Function?.Name)) partial.Name = delta.Function.Name;

			// The call id arrives on the first delta for an index (alongside the name); later argument-only
			// fragments omit it, so only overwrite when a non-empty id is actually present.
			if (!string.IsNullOrEmpty(delta.Id)) partial.Id = delta.Id;

			if (!string.IsNullOrEmpty(delta.Function?.Arguments)) partial.Arguments.Append(delta.Function.Arguments);
		}
	}

	/// <summary>
	/// Materializes the buffered fragments into complete Ollama tool calls, parsing each accumulated
	/// argument string into a structured node. Returns <see langword="null"/> when no tool calls were
	/// seen, so the terminal message omits the <c>tool_calls</c> field entirely.
	/// </summary>
	/// <returns>The assembled tool calls, or <see langword="null"/> when there were none.</returns>
	public IReadOnlyList<OllamaToolCall>? Build()
	{
		if (mCalls.Count == 0) return null;

		List<OllamaToolCall> result = new(mCalls.Count);
		foreach (PartialToolCall partial in mCalls.Values)
		{
			JsonNode arguments = OpenAiMessageConverter.ParseArgumentsOrEmpty(partial.Arguments.ToString());
			// Streamed OpenAI tool calls carry no description; only name and arguments are accumulated. The
			// call id is carried through so the client can correlate the result it later returns.
			result.Add(
				new OllamaToolCall(
					new OllamaToolCallFunction(
						partial.Name ?? string.Empty,
						Description: null,
						Arguments: arguments),
					Id: partial.Id));
		}

		return result;
	}

	/// <summary>
	/// Mutable per-index buffer holding a tool call's name and the concatenated fragments of its
	/// argument string while the stream is still in flight.
	/// </summary>
	private sealed class PartialToolCall
	{
		/// <summary>
		/// Gets the buffer accumulating the JSON argument-string fragments.
		/// </summary>
		public StringBuilder Arguments { get; } = new();

		/// <summary>
		/// Gets or sets the function name once a delta has supplied it.
		/// </summary>
		public string? Name { get; set; }

		/// <summary>
		/// Gets or sets the call id once a delta has supplied it.
		/// </summary>
		public string? Id { get; set; }
	}
}
