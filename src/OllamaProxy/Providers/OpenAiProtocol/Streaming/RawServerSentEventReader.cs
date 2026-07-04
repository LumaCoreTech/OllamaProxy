// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Runtime.CompilerServices;

namespace OllamaProxy.Providers.OpenAiProtocol.Streaming;

/// <summary>
/// Reads the raw <c>data:</c> payloads from an OpenAI Server-Sent-Events stream without
/// deserializing them. Unlike <see cref="ServerSentEventReader"/>, which parses each frame into a
/// typed chunk, this reader yields the verbatim JSON text of every event so a passthrough endpoint can
/// forward provider responses losslessly, preserving any fields the proxy's typed contracts do not
/// model (for example <c>logprobs</c> or provider-specific extensions). The terminating
/// <c>data: [DONE]</c> sentinel and blank/non-data lines are consumed and not yielded.
/// </summary>
static class RawServerSentEventReader
{
	/// <summary>The SSE field prefix carrying a data payload.</summary>
	private const string DataPrefix = "data:";

	/// <summary>The sentinel payload that signals the end of the OpenAI stream.</summary>
	private const string DoneSentinel = "[DONE]";

	/// <summary>
	/// Reads each data payload from an SSE stream until the terminating sentinel or the end of the
	/// stream is reached, yielding the raw JSON text of every event in stream order.
	/// </summary>
	/// <param name="stream">The raw upstream response stream positioned at the start of the SSE body.</param>
	/// <param name="cancellationToken">A token observed while reading lines from the stream.</param>
	/// <returns>An asynchronous sequence of raw JSON payload strings.</returns>
	public static async IAsyncEnumerable<string> ReadDataPayloadsAsync(
		Stream                                     stream,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(stream);

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

			yield return payload;
		}
	}
}
