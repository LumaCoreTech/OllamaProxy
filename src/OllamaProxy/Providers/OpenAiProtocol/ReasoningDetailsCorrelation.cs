// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using OllamaProxy.Contracts.Ollama;

namespace OllamaProxy.Providers.OpenAiProtocol;

/// <summary>
/// Derives the stable correlation key under which a turn's <c>reasoning_details</c> blob is cached and
/// later retrieved. The key is computed from the assistant turn's tool calls (the one part of the message
/// a client must replay verbatim for tool calling to function) so it survives a round trip through a
/// client that drops every non-standard field. It is computed from the tool calls' <em>content</em>
/// (function name and arguments), never the backend-assigned <c>tool_call_id</c>, whose length and format
/// are backend-controlled (some backends mint very short ids) and therefore neither portable nor a reliable
/// source of entropy.
/// <para>
/// The key is additionally scoped to the originating backend's name, because the single process-wide cache
/// is shared across every backend: without that scope two backends that emit the same tool call (say
/// <c>get_weather({"city":"Berlin"})</c>) would hash to one key, and one backend could be handed the
/// opaque, vendor-specific blob the other produced, a contract it never emitted. Folding the backend name
/// into the hash isolates each backend's blobs while still letting them share one cache instance.
/// </para>
/// <para>
/// To stay stable across a client that deserializes and re-serializes the history, the arguments are
/// canonicalized before hashing: object keys are sorted (the dominant re-serialization difference), array
/// order is preserved (arrays are ordered), insignificant whitespace is dropped, and the per-call fragments
/// are themselves sorted so the order of parallel tool calls cannot change the key. Scalar values are
/// emitted in their canonical JSON form, which is stable for any client that preserves a value's JSON type.
/// This neutralizes the formatting differences a normal client introduces; it does not defend against a
/// client that semantically rewrites the arguments, which would no longer be the same tool call. A hash
/// collision (two genuinely different turns sharing one key) merely re-attaches a slightly wrong blob and
/// degrades gracefully, never throwing or corrupting the conversation. This correlation has not been
/// measured against a live Claude/Gemini backend; it is exercised by tests with mocked backends only.
/// </para>
/// </summary>
static class ReasoningDetailsCorrelation
{
	// Bumped if the canonical form ever changes, so stale keys from an older format can never alias new ones.
	private const string FormatVersion = "rd-v1";

	// Control characters as structural separators, chosen because they cannot appear unescaped inside a
	// canonical JSON scalar or a function name, so no value can forge a fragment boundary.
	private const char FieldSeparator = '\u0000';
	private const char CallSeparator  = '\u0001';

	/// <summary>
	/// Computes the correlation key for an assistant turn's tool calls, or returns <see langword="null"/>
	/// when the turn carries no tool calls (there is then no stable anchor to correlate on, and no
	/// reasoning-details blob to preserve).
	/// </summary>
	/// <param name="backendName">
	/// The originating backend's name, folded into the key so the shared cache isolates each backend's blobs.
	/// </param>
	/// <param name="toolCalls">The assistant turn's tool calls, in Ollama shape.</param>
	/// <returns>The hex-encoded correlation key, or <see langword="null"/> when there are no tool calls.</returns>
	public static string? TryComputeKey(string backendName, IReadOnlyList<OllamaToolCall>? toolCalls)
	{
		if (toolCalls is not { Count: > 0 }) return null;

		// Build one canonical fragment per call, then sort them so the order in which parallel calls appear
		// cannot change the key. Each fragment pairs the function name with its canonicalized arguments.
		List<string> fragments = new(toolCalls.Count);
		foreach (OllamaToolCall call in toolCalls)
		{
			string name = call.Function?.Name ?? string.Empty;
			StringBuilder argsBuilder = new();
			WriteCanonical(call.Function?.Arguments, argsBuilder);
			fragments.Add($"{name}{FieldSeparator}{argsBuilder}");
		}

		fragments.Sort(StringComparer.Ordinal);

		// Prefix the canonical form with the backend name as an escaped JSON string, so the operator-supplied
		// name cannot forge a separator and a blob from one backend can never alias another's key.
		string scope = JsonValue.Create(backendName ?? string.Empty).ToJsonString();
		string canonical =
			$"{FormatVersion}{CallSeparator}{scope}{CallSeparator}{string.Join(CallSeparator, fragments)}";
		byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
		return Convert.ToHexString(hash);
	}

	/// <summary>
	/// Writes a JSON node to <paramref name="builder"/> in a canonical form: object members are emitted in
	/// ordinal key order (so a reordered-but-equal object hashes identically), array elements keep their
	/// order, and scalars use their canonical JSON text. Recurses through nested objects and arrays.
	/// </summary>
	/// <param name="node">The node to canonicalize, or <see langword="null"/>.</param>
	/// <param name="builder">The buffer the canonical text is appended to.</param>
	private static void WriteCanonical(JsonNode? node, StringBuilder builder)
	{
		switch (node)
		{
			case null:
				builder.Append("null");
				break;

			case JsonObject obj:
				builder.Append('{');
				bool firstMember = true;
				foreach (KeyValuePair<string, JsonNode?> member in obj.OrderBy(m => m.Key, StringComparer.Ordinal))
				{
					if (!firstMember) builder.Append(',');
					firstMember = false;

					// The key as a canonical JSON string (quoted and escaped), then its canonicalized value.
					builder.Append(JsonValue.Create(member.Key).ToJsonString());
					builder.Append(':');
					WriteCanonical(member.Value, builder);
				}

				builder.Append('}');
				break;

			case JsonArray array:
				builder.Append('[');
				for (int i = 0; i < array.Count; i++)
				{
					if (i > 0) builder.Append(',');
					WriteCanonical(array[i], builder);
				}

				builder.Append(']');
				break;

			default:
				// A scalar (string, number, bool). Its own JSON serialization is already canonical and
				// preserves the value's JSON type, which is what a well-behaved client round-trips verbatim.
				builder.Append(node.ToJsonString());
				break;
		}
	}
}
