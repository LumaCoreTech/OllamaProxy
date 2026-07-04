// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json.Nodes;

using OllamaProxy.Contracts.Ollama;
using OllamaProxy.Providers.OpenAiProtocol;

namespace OllamaProxy.Tests.Providers.OpenAiProtocol;

/// <summary>
/// Tests for <see cref="ReasoningDetailsCorrelation"/>, which derives the stable key a turn's
/// <c>reasoning_details</c> blob is cached and replayed under. The story covers the no-tool-calls null case,
/// determinism for identical calls, stability across the formatting differences a client introduces when it
/// deserializes and re-serializes the history (reordered object keys, whitespace, parallel-call order), the
/// id-independence that keeps a short backend-minted <c>tool_call_id</c> out of the key, the sensitivity
/// to a genuine change in function name, argument value, or argument array order, and the backend scoping
/// that keeps one backend's blob out of another's key when they share the process-wide cache.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ReasoningDetailsCorrelationTests
{
	// A default backend name for the tests whose subject is the tool-call content, not the scope. The
	// dedicated backend-scoping test supplies its own distinct names.
	private const string Backend = "backend-a";

	private static string? Key(IReadOnlyList<OllamaToolCall>? toolCalls, string backendName = Backend) =>
		ReasoningDetailsCorrelation.TryComputeKey(backendName, toolCalls);

	private static OllamaToolCall Call(string name, string argumentsJson, string? id = null) => new(
		new OllamaToolCallFunction(name, Description: null, JsonNode.Parse(argumentsJson)),
		id);

	/// <summary>
	/// Verifies that a turn carrying no tool calls yields <see langword="null"/>: there is no stable anchor
	/// to correlate on, and no blob to preserve.
	/// </summary>
	[Fact]
	public void TryComputeKey_WhenNoToolCalls_ReturnsNull()
	{
		Assert.Null(Key(null));
		Assert.Null(Key([]));
	}

	/// <summary>
	/// Verifies that identical tool calls produce identical keys (determinism).
	/// </summary>
	[Fact]
	public void TryComputeKey_ForIdenticalCalls_IsDeterministic()
	{
		// Arrange
		IReadOnlyList<OllamaToolCall> a = [Call("get_weather", """{"city":"Berlin","unit":"c"}""")];
		IReadOnlyList<OllamaToolCall> b = [Call("get_weather", """{"city":"Berlin","unit":"c"}""")];

		// Act / Assert
		Assert.Equal(
			Key(a),
			Key(b));
	}

	/// <summary>
	/// Verifies that reordering an arguments object's keys (the dominant re-serialization difference) does
	/// not change the key, so a client that round-trips the history through its own JSON model still
	/// correlates.
	/// </summary>
	[Fact]
	public void TryComputeKey_IsStableAcrossReorderedArgumentKeys()
	{
		// Arrange: same members, different key order.
		IReadOnlyList<OllamaToolCall> ordered = [Call("f", """{"a":1,"b":2}""")];
		IReadOnlyList<OllamaToolCall> reordered = [Call("f", """{"b":2,"a":1}""")];

		// Act / Assert
		Assert.Equal(
			Key(ordered),
			Key(reordered));
	}

	/// <summary>
	/// Verifies that insignificant whitespace in the arguments does not change the key.
	/// </summary>
	[Fact]
	public void TryComputeKey_IsStableAcrossInsignificantWhitespace()
	{
		// Arrange
		IReadOnlyList<OllamaToolCall> compact = [Call("f", """{"a":1,"b":[2,3]}""")];
		IReadOnlyList<OllamaToolCall> spaced = [Call("f", "{ \"a\" : 1 , \"b\" : [ 2 , 3 ] }")];

		// Act / Assert
		Assert.Equal(
			Key(compact),
			Key(spaced));
	}

	/// <summary>
	/// Verifies that the order of parallel tool calls within the turn does not change the key, since the
	/// per-call fragments are sorted before hashing.
	/// </summary>
	[Fact]
	public void TryComputeKey_IsStableAcrossParallelCallOrder()
	{
		// Arrange: two calls, swapped order.
		IReadOnlyList<OllamaToolCall> forward = [Call("a", "{}"), Call("b", "{}")];
		IReadOnlyList<OllamaToolCall> reversed = [Call("b", "{}"), Call("a", "{}")];

		// Act / Assert
		Assert.Equal(
			Key(forward),
			Key(reversed));
	}

	/// <summary>
	/// Verifies that the backend-assigned call id is not part of the key: two otherwise-identical calls with
	/// different ids correlate the same, so a short or volatile id never destabilizes the round-trip.
	/// </summary>
	[Fact]
	public void TryComputeKey_IgnoresToolCallId()
	{
		// Arrange
		IReadOnlyList<OllamaToolCall> withId = [Call("f", """{"x":1}""", id: "call_AAA")];
		IReadOnlyList<OllamaToolCall> withOtherId = [Call("f", """{"x":1}""", id: "1")];

		// Act / Assert
		Assert.Equal(
			Key(withId),
			Key(withOtherId));
	}

	/// <summary>
	/// Verifies that a different function name produces a different key.
	/// </summary>
	[Fact]
	public void TryComputeKey_DiffersOnFunctionName()
	{
		IReadOnlyList<OllamaToolCall> a = [Call("get_weather", "{}")];
		IReadOnlyList<OllamaToolCall> b = [Call("get_time", "{}")];

		Assert.NotEqual(
			Key(a),
			Key(b));
	}

	/// <summary>
	/// Verifies that a different argument value produces a different key.
	/// </summary>
	[Fact]
	public void TryComputeKey_DiffersOnArgumentValue()
	{
		IReadOnlyList<OllamaToolCall> a = [Call("f", """{"city":"Berlin"}""")];
		IReadOnlyList<OllamaToolCall> b = [Call("f", """{"city":"Paris"}""")];

		Assert.NotEqual(
			Key(a),
			Key(b));
	}

	/// <summary>
	/// Verifies that array element order is significant (arrays are ordered), so reordering an argument
	/// array changes the key.
	/// </summary>
	[Fact]
	public void TryComputeKey_DiffersOnArgumentArrayOrder()
	{
		IReadOnlyList<OllamaToolCall> a = [Call("f", """{"items":[1,2]}""")];
		IReadOnlyList<OllamaToolCall> b = [Call("f", """{"items":[2,1]}""")];

		Assert.NotEqual(
			Key(a),
			Key(b));
	}

	/// <summary>
	/// Verifies that a number argument and the string of that number produce different keys, since the
	/// canonical form preserves each value's JSON type.
	/// </summary>
	[Fact]
	public void TryComputeKey_DistinguishesScalarType()
	{
		IReadOnlyList<OllamaToolCall> asNumber = [Call("f", """{"n":1}""")];
		IReadOnlyList<OllamaToolCall> asString = [Call("f", """{"n":"1"}""")];

		Assert.NotEqual(
			Key(asNumber),
			Key(asString));
	}

	/// <summary>
	/// Verifies that the same tool calls hash to different keys under different backend names, so the
	/// process-wide cache shared across backends can never hand one backend the opaque blob another produced.
	/// </summary>
	[Fact]
	public void TryComputeKey_DiffersOnBackendName()
	{
		// Arrange: an identical tool call two backends could both make.
		IReadOnlyList<OllamaToolCall> calls = [Call("get_weather", """{"city":"Berlin"}""")];

		// Act / Assert
		Assert.NotEqual(
			Key(calls, "venice"),
			Key(calls, "openrouter"));
	}
}
