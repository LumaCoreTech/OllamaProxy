// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OllamaProxy.Diagnostics;

/// <summary>
/// Serializes a trace entry's body for human readability. A body that is a JSON object or array is inlined
/// verbatim as nested JSON instead of an escaped one-line string. A Server-Sent Events (SSE) transcript is
/// unwrapped into a JSON array of its events, each event inlined as nested JSON when it is itself a JSON
/// object or array. Any other body (plain text, a bare scalar, or a fragment truncated mid-token) is
/// written as an ordinary JSON string, exactly as captured.
/// </summary>
/// <remarks>
/// The converter keys on the captured text's <em>shape</em>, not on the producing endpoint. A whole-body
/// JSON value is inlined; a <em>data-only</em> SSE transcript (every non-blank line is a <c>data:</c> field
/// or a <c>:</c> comment, and at least one event payload is itself JSON) is unwrapped into an array of its
/// event payloads. Both transforms are write-time presentation choices only: the in-memory trace keeps the
/// raw captured string. Inlining a JSON value round-trips exactly through <see cref="Read"/>; the SSE unwrap
/// is a <strong>one-way</strong> readable projection. <see cref="Read"/> returns the array's JSON text, not
/// the original wire transcript, which is safe because traces are written for human inspection and never
/// read back by the proxy. A richer SSE stream that also carries <c>event:</c>, <c>id:</c>, or <c>retry:</c>
/// fields is left verbatim so none of the captured frames are reinterpreted. A <see langword="null"/> body
/// is handled by the serializer itself (the converter does not opt into null handling), so the read and
/// write paths only ever see a non-null body.
/// </remarks>
sealed class TraceBodyJsonConverter : JsonConverter<string>
{
	/// <summary>
	/// Reads a body value, accepting both shapes the converter can write: a JSON string is returned as-is,
	/// and an inlined JSON object or array is returned as its raw JSON text. An SSE transcript that the
	/// writer unwrapped into an event array therefore reads back as that array's JSON text, not as the
	/// original wire transcript; the unwrap is a one-way readable projection.
	/// </summary>
	/// <param name="reader">The reader positioned at the body token.</param>
	/// <param name="typeToConvert">The target type, always <see cref="string"/>.</param>
	/// <param name="options">The active serializer options.</param>
	/// <returns>The body text: the string itself, or the raw JSON of an inlined object or array.</returns>
	public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		if (reader.TokenType == JsonTokenType.String) return reader.GetString()!;

		// The writer inlined a structured body (a JSON value or an unwrapped SSE array); read the whole
		// subtree back and hand its raw text to the model.
		using JsonDocument document = JsonDocument.ParseValue(ref reader);
		return document.RootElement.GetRawText();
	}

	/// <summary>
	/// Writes a body value for readability: a JSON object or array is inlined as nested JSON, a data-only SSE
	/// transcript is unwrapped into an array of its events, and anything else (text, a richer SSE stream, a
	/// scalar, or a truncated fragment) stays a verbatim string.
	/// </summary>
	/// <param name="writer">
	/// The writer to emit the body into; it already carries the document's indentation and encoder, so an
	/// inlined body shares them automatically.
	/// </param>
	/// <param name="value">The captured body text, already redacted and truncated upstream.</param>
	/// <param name="options">The active serializer options. Unused: the writer alone drives the output.</param>
	public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
	{
		if (TryParseStructured(value, out JsonDocument? document))
		{
			// The document is owned by this call; dispose it once its element has been copied to the writer.
			using (document)
			{
				document.RootElement.WriteTo(writer);
			}
			return;
		}

		// A whole-body JSON value did not match; try the SSE shape before falling back to a verbatim string.
		if (TryWriteSseEvents(writer, value)) return;

		writer.WriteStringValue(value);
	}

	/// <summary>
	/// Attempts to parse <paramref name="value"/> as a structured JSON value (an object or array) that is
	/// safe to inline. A bare scalar (number, boolean, string, <see langword="null"/>) is rejected so a text
	/// body that merely looks like a literal is not reinterpreted into a different JSON type.
	/// </summary>
	/// <param name="value">The captured body text to probe.</param>
	/// <param name="document">
	/// The parsed document rooted at an object or array when the parse succeeds; otherwise
	/// <see langword="null"/>. The caller owns the returned document and must dispose it.
	/// </param>
	/// <returns>
	/// <see langword="true"/> when the body is a JSON object or array; otherwise <see langword="false"/>.
	/// </returns>
	private static bool TryParseStructured(string value, [NotNullWhen(true)] out JsonDocument? document)
	{
		document = null;

		// An empty body can never be structured JSON; skip the parse and keep it as a (empty) string.
		if (value.Length == 0) return false;

		// The read-only DOM is used deliberately over the mutable JsonNode: it never throws on duplicate
		// keys (it keeps them verbatim), and tracing must never break the request it traces. A non-object,
		// non-array root (a bare scalar) is rejected so a text body that merely looks like a literal is not
		// silently retyped.
		JsonDocument parsed;
		try
		{
			parsed = JsonDocument.Parse(value);
		}
		catch (JsonException)
		{
			// Not JSON (plain text, an SSE transcript, or a body truncated mid-token): keep it as a string.
			return false;
		}

		if (parsed.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
		{
			document = parsed;
			return true;
		}

		// A bare scalar parsed successfully but must stay a string; release the document we will not return.
		parsed.Dispose();
		return false;
	}

	/// <summary>
	/// Attempts to unwrap a data-only Server-Sent Events transcript into a JSON array of its event payloads,
	/// writing each payload as nested JSON when it is itself a JSON object or array and as a string
	/// otherwise (e.g. the <c>[DONE]</c> terminator). The transcript qualifies only when every non-blank
	/// line is a <c>data:</c> field or a <c>:</c> comment and at least one event payload is JSON, so plain
	/// text and a richer SSE stream carrying <c>event:</c> / <c>id:</c> / <c>retry:</c> frames are left
	/// untouched.
	/// </summary>
	/// <param name="writer">The writer to emit the event array into.</param>
	/// <param name="value">The captured body text to probe and, on success, unwrap.</param>
	/// <returns>
	/// <see langword="true"/> when the body was a qualifying SSE transcript and has been written as an
	/// array; otherwise <see langword="false"/>, leaving <paramref name="writer"/> untouched.
	/// </returns>
	private static bool TryWriteSseEvents(Utf8JsonWriter writer, string value)
	{
		if (!TrySplitSseEvents(value, out List<string>? events)) return false;

		writer.WriteStartArray();
		foreach (string payload in events)
		{
			// Re-parse per event (a cold diagnostic path, so clarity beats avoiding the second parse): the
			// whole-body rule decides each element: a JSON object/array inlines, [DONE] stays a string.
			if (TryParseStructured(payload, out JsonDocument? document))
			{
				using (document)
				{
					document.RootElement.WriteTo(writer);
				}
			}
			else
			{
				writer.WriteStringValue(payload);
			}
		}

		writer.WriteEndArray();
		return true;
	}

	/// <summary>
	/// Attempts to split <paramref name="value"/> into the payloads of a data-only Server-Sent Events
	/// transcript. Every non-blank line must be a <c>data:</c> field or a <c>:</c> comment, consecutive
	/// <c>data:</c> lines within one event are joined with a newline (per the SSE specification), and the
	/// transcript qualifies only when at least one resulting payload parses as a JSON object or array.
	/// </summary>
	/// <param name="value">The captured body text to probe.</param>
	/// <param name="events">
	/// The ordered event payloads when the split succeeds; otherwise <see langword="null"/>.
	/// </param>
	/// <returns>
	/// <see langword="true"/> when <paramref name="value"/> is a data-only SSE transcript with at least one
	/// JSON payload; otherwise <see langword="false"/>.
	/// </returns>
	private static bool TrySplitSseEvents(string value, [NotNullWhen(true)] out List<string>? events)
	{
		events = null;

		// A transcript must carry an SSE data field to qualify; this short-circuits ordinary prose (the
		// common BackendResponse / BackendReasoning case) before allocating the line split.
		if (!value.Contains("data:", StringComparison.Ordinal)) return false;

		List<string> payloads = [];
		StringBuilder current = new();
		bool currentHasData = false;
		bool anyJson = false;

		// Lines are split on '\n'; a trailing '\r' is trimmed so both '\n' and '\r\n' transcripts parse.
		foreach (string rawLine in value.Split('\n'))
		{
			string line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;

			if (line.Length == 0)
			{
				// A blank line dispatches the current event (SSE spec); flush it if it carried any data.
				if (currentHasData) FlushEvent();
				continue;
			}

			if (line.StartsWith(':'))
			{
				// A comment line is valid SSE but carries no payload; ignore it without disqualifying.
				continue;
			}

			if (line.StartsWith("data:", StringComparison.Ordinal))
			{
				// Strip "data:" and, per the SSE spec, a single optional leading space ("data: x" -> "x").
				string fieldValue = line["data:".Length..];
				if (fieldValue.StartsWith(' ')) fieldValue = fieldValue[1..];

				if (currentHasData) current.Append('\n');
				current.Append(fieldValue);
				currentHasData = true;
				continue;
			}

			// A line that is neither blank, comment, nor data means this is not a data-only transcript.
			return false;
		}

		// Dispatch a final event that was not followed by a trailing blank line.
		if (currentHasData) FlushEvent();

		// With no JSON payload there is nothing worth unwrapping; leave the body verbatim.
		if (!anyJson) return false;

		events = payloads;
		return true;

		void FlushEvent()
		{
			string payload = current.ToString();
			current.Clear();
			currentHasData = false;
			payloads.Add(payload);

			// Probe (and immediately release) the payload to learn whether the transcript holds any JSON.
			if (TryParseStructured(payload, out JsonDocument? document))
			{
				document.Dispose();
				anyJson = true;
			}
		}
	}
}
