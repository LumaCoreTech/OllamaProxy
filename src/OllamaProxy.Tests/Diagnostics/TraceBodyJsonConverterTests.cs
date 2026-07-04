// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json;
using System.Text.Json.Nodes;

using OllamaProxy.Diagnostics;

namespace OllamaProxy.Tests.Diagnostics;

// Body rendering: the shapes a captured body can take, and how each is written and read back.
//
// The converter exists to make a persisted trace readable without rewriting what was captured. It keys on
// the body's shape, never on the endpoint, so these tests pin every half of that contract:
//
//   1. Write — structured: a JSON object or array is inlined as nested JSON so it reads as part of the
//      surrounding document instead of an escaped one-line string (WhenBodyIsStructuredJson). The writer's
//      indentation flows through to the inlined value (WhenIndentationEnabled).
//
//   2. Write — SSE: a data-only transcript is unwrapped into a JSON array of its events, each event inlined
//      when it is itself JSON (WhenBodyIsDataOnlySse). A stream that is not cleanly unwrappable — richer
//      event:/id:/retry: frames or a transcript with no JSON payload — stays verbatim
//      (WhenSseIsNotUnwrappable).
//
//   3. Write — everything else: plain text, a bare scalar, an empty body, or a fragment truncated mid-token
//      stays a verbatim string, so nothing is silently retyped (WhenBodyIsNotStructuredJson).
//
//   4. Read & round-trip: a string token comes back unchanged (WhenTokenIsString) and an inlined object or
//      array comes back as its compact JSON text (WhenTokenIsInlinedStructure); a structured body round-trips
//      exactly (Roundtrip_StructuredBody), while an unwrapped SSE transcript reads back as its event array,
//      not the wire transcript — a one-way projection (Roundtrip_SseBody).
[Trait("Category", "Unit")]
public sealed class TraceBodyJsonConverterTests
{
	/// <summary>Cached compact write/read options (CA1869): the converter is the only registered behavior.</summary>
	private static readonly JsonSerializerOptions CompactOptions = new()
	{
		Converters = { new TraceBodyJsonConverter() }
	};

	/// <summary>Cached indented options that mirror the trace sink's readable output.</summary>
	private static readonly JsonSerializerOptions IndentedOptions = new()
	{
		WriteIndented = true,
		Converters = { new TraceBodyJsonConverter() }
	};

	/// <summary>
	/// Serializes a body string through the converter, mirroring how the trace sink writes it.
	/// </summary>
	/// <param name="body">The body text to serialize.</param>
	/// <param name="indented">Whether the writer indents, matching the sink's readable output.</param>
	/// <returns>The serialized JSON: an inlined object/array for a structured body, otherwise a string.</returns>
	private static string SerializeBody(string body, bool indented = false) =>
		JsonSerializer.Serialize(body, indented ? IndentedOptions : CompactOptions);

	/// <summary>
	/// Deserializes a body value through the converter, accepting both a JSON string and an inlined
	/// object or array.
	/// </summary>
	/// <param name="json">The JSON to read a body from.</param>
	/// <returns>The body text the converter reconstructed.</returns>
	private static string? DeserializeBody(string json) => JsonSerializer.Deserialize<string>(json, CompactOptions);

	#region Write()

	// --- 1. Structured bodies are inlined ---

	/// <summary>
	/// Supplies JSON object and array bodies that must be inlined, paired with the JSON kind each must keep.
	/// </summary>
	public static TheoryData<string, string, JsonValueKind> StructuredBodies => new()
	{
		// A typical request object inlines as an object, not an escaped string.
		{ "object", """{"model":"llama3","stream":true}""", JsonValueKind.Object },
		// A nested object inlines whole — depth does not change the decision.
		{ "nested object", """{"a":{"b":1}}""", JsonValueKind.Object },
		// An empty object is still structured and is inlined verbatim.
		{ "empty object", "{}", JsonValueKind.Object },
		// A top-level array inlines as an array.
		{ "array", "[1,2,3]", JsonValueKind.Array },
		// An empty array is structured too.
		{ "empty array", "[]", JsonValueKind.Array }
	};

	/// <summary>
	/// Verifies that <see cref="TraceBodyJsonConverter.Write"/> inlines a JSON object or array body as
	/// nested JSON, writing the structured value verbatim rather than as an escaped string.
	/// </summary>
	/// <param name="scenario">A human-readable label for the case.</param>
	/// <param name="body">The structured JSON body to serialize.</param>
	/// <param name="expectedKind">The JSON kind the inlined body must keep.</param>
	[Theory]
	[MemberData(nameof(StructuredBodies))]
	public void Write_WhenBodyIsStructuredJson_InlinesVerbatim(string scenario, string body, JsonValueKind expectedKind)
	{
		_ = scenario;

		// Act: compact serialization mirrors the in-memory body's canonical form.
		string result = SerializeBody(body);

		// Assert: the output is the structured value itself (its kind survived) and equals the body exactly.
		using JsonDocument document = JsonDocument.Parse(result);
		Assert.Equal(expectedKind, document.RootElement.ValueKind);
		Assert.Equal(body, result);
	}

	/// <summary>
	/// Verifies that <see cref="TraceBodyJsonConverter.Write"/> carries the writer's indentation into the
	/// inlined body, so a structured body reads as multi-line JSON while keeping its exact content.
	/// </summary>
	[Fact]
	public void Write_WhenIndentationEnabled_InlinesStructuredBodyAsMultiLineJson()
	{
		// Arrange: a structured body whose compact and indented renderings must differ only in whitespace.
		const string body = """{"model":"llama3","stream":true}""";

		// Act
		string compact = SerializeBody(body, indented: false);
		string indented = SerializeBody(body, indented: true);

		// Assert: indentation changed the text (it is now multi-line)...
		Assert.NotEqual(compact, indented);
		// ...the inlined value is still an object, not a stringified blob...
		using JsonDocument document = JsonDocument.Parse(indented);
		Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
		// ...and it is structurally identical to the original body.
		Assert.True(
			JsonNode.DeepEquals(JsonNode.Parse(indented), JsonNode.Parse(body)),
			"the indented body must be structurally identical to the source");
	}

	// --- 2. SSE transcripts are unwrapped into event arrays ---

	/// <summary>
	/// Supplies data-only SSE transcripts paired with the compact JSON array each must unwrap into, covering
	/// the terminator, CRLF endings, a final event without a trailing blank line, an ignored comment line,
	/// and the multi-line data concatenation rule.
	/// </summary>
	public static TheoryData<string, string, string> DataOnlySseTranscripts => new()
	{
		// Two JSON events then the [DONE] terminator: objects inline, [DONE] stays a string element.
		{
			"events with terminator",
			"data: {\"a\":1}\n\ndata: {\"b\":2}\n\ndata: [DONE]\n\n",
			"""[{"a":1},{"b":2},"[DONE]"]"""
		},
		// CRLF line endings parse the same as LF — the trailing '\r' is trimmed per line.
		{ "crlf endings", "data: {\"a\":1}\r\n\r\ndata: [DONE]\r\n\r\n", """[{"a":1},"[DONE]"]""" },
		// A final event with no trailing blank line is still flushed.
		{ "single event no trailing blank", "data: {\"a\":1}", """[{"a":1}]""" },
		// A leading ':' comment line is valid SSE and ignored without disqualifying the transcript.
		{ "leading comment ignored", ": keep-alive\ndata: {\"a\":1}\n\ndata: [DONE]\n\n", """[{"a":1},"[DONE]"]""" },
		// Two data lines in one event are joined with '\n' (SSE spec); the newline is structural whitespace.
		{ "multiline data joined", "data: {\"a\":\ndata: 1}\n\n", """[{"a":1}]""" }
	};

	/// <summary>
	/// Verifies that <see cref="TraceBodyJsonConverter.Write"/> unwraps a data-only SSE transcript into a
	/// JSON array of its events, inlining each event that is itself a JSON object or array and keeping a
	/// non-JSON event (such as the <c>[DONE]</c> terminator) as a string element.
	/// </summary>
	/// <param name="scenario">A human-readable label for the case.</param>
	/// <param name="transcript">The SSE transcript to serialize.</param>
	/// <param name="expectedJson">The compact JSON array the transcript must unwrap into.</param>
	[Theory]
	[MemberData(nameof(DataOnlySseTranscripts))]
	public void Write_WhenBodyIsDataOnlySse_UnwrapsIntoEventArray(
		string scenario,
		string transcript,
		string expectedJson)
	{
		_ = scenario;

		// Act
		string result = SerializeBody(transcript);

		// Assert: the transcript became an array (not a string), exactly matching the expected event JSON.
		using JsonDocument document = JsonDocument.Parse(result);
		Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
		Assert.Equal(expectedJson, result);
	}

	/// <summary>
	/// Supplies SSE-like bodies that must NOT be unwrapped: a richer stream carrying a non-data field, and a
	/// data-only stream whose every payload is plain text. Both stay verbatim strings.
	/// </summary>
	public static TheoryData<string, string> NonUnwrappableSse => new()
	{
		// An 'event:' field means this is more than a data-only stream; leaving it verbatim avoids dropping
		// the frame metadata the converter does not model.
		{ "non-data field", "event: message\ndata: {\"a\":1}\n\n" },
		// Every payload is plain text with no JSON to inline, so there is nothing worth unwrapping.
		{ "no json payload", "data: hello\n\ndata: world\n\n" }
	};

	/// <summary>
	/// Verifies that <see cref="TraceBodyJsonConverter.Write"/> leaves an SSE-like body verbatim when it
	/// cannot be cleanly unwrapped — either because it carries non-<c>data:</c> fields or because no event
	/// payload is JSON — so no captured frame is reinterpreted or dropped.
	/// </summary>
	/// <param name="scenario">A human-readable label for the case.</param>
	/// <param name="body">The SSE-like body that must stay a verbatim string.</param>
	[Theory]
	[MemberData(nameof(NonUnwrappableSse))]
	public void Write_WhenSseIsNotUnwrappable_WritesAsString(string scenario, string body)
	{
		_ = scenario;

		// Act
		string result = SerializeBody(body);

		// Assert: the body is a JSON string equal to the original, character for character.
		using JsonDocument document = JsonDocument.Parse(result);
		Assert.Equal(JsonValueKind.String, document.RootElement.ValueKind);
		Assert.Equal(body, document.RootElement.GetString());
	}

	// --- 3. Everything else stays a verbatim string ---

	/// <summary>
	/// Supplies bodies that are not a JSON object or array and must therefore be written as plain strings.
	/// </summary>
	public static TheoryData<string, string> NonStructuredBodies => new()
	{
		// Plain prose is not JSON and must not be reshaped.
		{ "plain text", "Hello there" },
		// A bare number parses as JSON but is a scalar, so it stays a string rather than being retyped.
		{ "bare number", "42" },
		// A bare boolean is likewise a scalar.
		{ "bare boolean", "true" },
		// A JSON string literal is a scalar too — inlining it would strip the surrounding quotes.
		{ "quoted string literal", "\"hi\"" },
		// An empty body is short-circuited before any parse.
		{ "empty", "" },
		// A body truncated mid-token cannot parse and must survive as captured.
		{ "truncated json", "{\"a\":" }
	};

	/// <summary>
	/// Verifies that <see cref="TraceBodyJsonConverter.Write"/> writes a body that is not a JSON object or
	/// array as an ordinary string, leaving text, scalars, an empty body, and truncated fragments verbatim.
	/// </summary>
	/// <param name="scenario">A human-readable label for the case.</param>
	/// <param name="body">The non-structured body to serialize.</param>
	[Theory]
	[MemberData(nameof(NonStructuredBodies))]
	public void Write_WhenBodyIsNotStructuredJson_WritesAsString(string scenario, string body)
	{
		_ = scenario;

		// Act
		string result = SerializeBody(body);

		// Assert: the output is a JSON string whose value is the original body, character for character.
		using JsonDocument document = JsonDocument.Parse(result);
		Assert.Equal(JsonValueKind.String, document.RootElement.ValueKind);
		Assert.Equal(body, document.RootElement.GetString());
	}

	#endregion

	#region Read()

	/// <summary>
	/// Verifies that <see cref="TraceBodyJsonConverter.Read"/> returns a JSON string token unchanged, so a
	/// body that was written as a string reads back exactly as captured.
	/// </summary>
	[Fact]
	public void Read_WhenTokenIsString_ReturnsBodyVerbatim()
	{
		// Arrange: a plain string body as it would appear in a persisted trace.
		const string json = "\"Hello there\"";

		// Act
		string? result = DeserializeBody(json);

		// Assert
		Assert.Equal("Hello there", result);
	}

	/// <summary>
	/// Supplies inlined, compact object and array JSON that must read back as its own raw text.
	/// </summary>
	public static TheoryData<string, string> InlinedStructures => new()
	{
		// An inlined object reads back as its compact JSON text.
		{ "object", """{"a":1,"b":2}""" },
		// An inlined array reads back as its compact JSON text.
		{ "array", "[1,2,3]" }
	};

	/// <summary>
	/// Verifies that <see cref="TraceBodyJsonConverter.Read"/> returns an inlined object or array as its
	/// compact JSON text, reconstructing the structured body the writer had inlined.
	/// </summary>
	/// <param name="scenario">A human-readable label for the case.</param>
	/// <param name="json">The inlined, compact JSON to read back.</param>
	[Theory]
	[MemberData(nameof(InlinedStructures))]
	public void Read_WhenTokenIsInlinedStructure_ReturnsCompactJson(string scenario, string json)
	{
		_ = scenario;

		// Act
		string? result = DeserializeBody(json);

		// Assert: the raw JSON text comes back unchanged for a compact source.
		Assert.Equal(json, result);
	}

	#endregion

	// --- 4. End-to-end: write then read round-trips ---

	/// <summary>
	/// Verifies that a structured body survives a compact write-then-read cycle unchanged, tying the inline
	/// (<see cref="TraceBodyJsonConverter.Write"/>) and raw-text (<see cref="TraceBodyJsonConverter.Read"/>)
	/// halves of the converter into one exact round-trip.
	/// </summary>
	[Fact]
	public void Roundtrip_StructuredBody_PreservesContent()
	{
		// Arrange: a representative request body.
		const string body = """{"model":"llama3","messages":[{"role":"user","content":"hi"}]}""";

		// Act: write (inlines the object) then read (returns its compact text).
		string written = SerializeBody(body);
		string? readBack = DeserializeBody(written);

		// Assert: the body is identical to what was captured.
		Assert.Equal(body, readBack);
	}

	/// <summary>
	/// Verifies that an SSE transcript round-trips to its unwrapped event array rather than the original wire
	/// transcript: the unwrap is a deliberate one-way readable projection, so <see cref="TraceBodyJsonConverter.Read"/>
	/// returns the array's JSON text, not the <c>data:</c>-framed source.
	/// </summary>
	[Fact]
	public void Roundtrip_SseBody_ReadsBackAsEventArrayNotWireTranscript()
	{
		// Arrange: a data-only transcript whose wire framing differs from its unwrapped array form.
		const string transcript = "data: {\"a\":1}\n\ndata: [DONE]\n\n";

		// Act: write (unwraps into an event array) then read (returns that array's compact text).
		string written = SerializeBody(transcript);
		string? readBack = DeserializeBody(written);

		// Assert: the read-back value is the event array, not the original transcript (one-way projection).
		Assert.Equal("""[{"a":1},"[DONE]"]""", readBack);
		Assert.NotEqual(transcript, readBack);
	}
}
