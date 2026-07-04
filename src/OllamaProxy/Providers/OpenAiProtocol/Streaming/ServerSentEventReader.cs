// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Runtime.CompilerServices;
using System.Text.Json;

using OllamaProxy.Providers.OpenAiProtocol.Contracts;

namespace OllamaProxy.Providers.OpenAiProtocol.Streaming;

/// <summary>
/// Parses an OpenAI Server-Sent-Events chat-completion stream into typed
/// <see cref="OpenAiChatCompletionChunk"/> values. OpenAI frames each chunk as a <c>data: {json}</c>
/// line; the stream terminates with the sentinel <c>data: [DONE]</c>. Blank lines and any non-data
/// fields (such as comments) are skipped. The reader streams lazily and honors cancellation so a
/// client disconnect promptly stops consuming the upstream response.
/// </summary>
static class ServerSentEventReader
{
	/// <summary>The SSE field prefix carrying a data payload.</summary>
	private const string DataPrefix = "data:";

	/// <summary>The sentinel payload that signals the end of the OpenAI stream.</summary>
	private const string DoneSentinel = "[DONE]";

	/// <summary>
	/// Reads and deserializes chat-completion chunks from an SSE stream until the terminating sentinel
	/// or the end of the stream is reached.
	/// </summary>
	/// <param name="stream">The raw upstream response stream positioned at the start of the SSE body.</param>
	/// <param name="serializerOptions">The JSON options used to deserialize each data payload.</param>
	/// <param name="cancellationToken">A token observed while reading lines from the stream.</param>
	/// <returns>An asynchronous sequence of parsed chunks in stream order.</returns>
	public static async IAsyncEnumerable<OpenAiChatCompletionChunk> ReadChunksAsync(
		Stream                                     stream,
		JsonSerializerOptions                      serializerOptions,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(stream);
		ArgumentNullException.ThrowIfNull(serializerOptions);

		using StreamReader reader = new(stream);

		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();

			string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

			// A null line marks the end of the stream; the SSE body ended without a [DONE] sentinel.
			if (line is null) yield break;

			// Blank lines delimit events; non-data fields are not used by the chat-completion stream.
			if (line.Length == 0 || !line.StartsWith(DataPrefix, StringComparison.Ordinal)) continue;

			string payload = line[DataPrefix.Length..].Trim();

			if (payload.Length == 0) continue;

			if (string.Equals(payload, DoneSentinel, StringComparison.Ordinal)) yield break;

			OpenAiChatCompletionChunk? chunk = DeserializeChunk(payload, serializerOptions);

			if (chunk is not null) yield return chunk;
		}
	}

	/// <summary>
	/// Deserializes a single SSE data payload into a chunk, tolerating malformed fragments by skipping
	/// them rather than aborting the whole stream.
	/// </summary>
	/// <param name="payload">The JSON text of one <c>data:</c> line.</param>
	/// <param name="serializerOptions">The JSON options used for deserialization.</param>
	/// <returns>The parsed chunk, or <see langword="null"/> when the payload could not be parsed.</returns>
	private static OpenAiChatCompletionChunk? DeserializeChunk(string payload, JsonSerializerOptions serializerOptions)
	{
		try
		{
			return JsonSerializer.Deserialize<OpenAiChatCompletionChunk>(payload, serializerOptions);
		}
		catch (JsonException)
		{
			return null;
		}
	}
}
