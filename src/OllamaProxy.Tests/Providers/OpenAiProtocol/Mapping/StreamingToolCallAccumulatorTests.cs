// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Contracts.Ollama;
using OllamaProxy.Providers.OpenAiProtocol.Contracts;
using OllamaProxy.Providers.OpenAiProtocol.Mapping;

namespace OllamaProxy.Tests.Providers.OpenAiProtocol.Mapping;

/// <summary>
/// Tests for <see cref="StreamingToolCallAccumulator"/>, which reassembles streamed OpenAI tool-call
/// fragments into complete Ollama tool calls. The story moves from the empty stream, through the
/// canonical case (a name delta followed by argument fragments for one index), to multiple concurrent
/// calls and the index-defaulting and parsing behaviors.
/// </summary>
[Trait("Category", "Unit")]
public sealed class StreamingToolCallAccumulatorTests
{
	private static OpenAiToolCall Delta(
		int?    index,
		string? name,
		string? arguments,
		string? id = null) => new(
		Id: id,
		Type: "function",
		Function: new OpenAiToolCallFunction(name, arguments),
		Index: index);

	/// <summary>
	/// Verifies that a freshly constructed accumulator reports no tool calls and builds to
	/// <see langword="null"/>.
	/// </summary>
	[Fact]
	public void Build_WhenNoFragmentsAccumulated_ReturnsNull()
	{
		// Arrange
		StreamingToolCallAccumulator sut = new();

		// Assert
		Assert.False(sut.HasToolCalls);
		Assert.Null(sut.Build());
	}

	/// <summary>
	/// Verifies that an empty or absent delta list is ignored, leaving the accumulator empty.
	/// </summary>
	[Fact]
	public void Accumulate_WhenDeltaListEmpty_RecordsNothing()
	{
		// Arrange
		StreamingToolCallAccumulator sut = new();

		// Act
		sut.Accumulate(null);
		sut.Accumulate([]);

		// Assert
		Assert.False(sut.HasToolCalls);
	}

	/// <summary>
	/// Verifies that a name delta followed by several argument fragments for the same index is
	/// reassembled into one tool call whose concatenated argument string is parsed into a structured
	/// object.
	/// </summary>
	[Fact]
	public void Build_WhenFragmentsSpanDeltas_ReassemblesSingleCall()
	{
		// Arrange: the first delta names the call; later deltas stream the JSON arguments in pieces.
		StreamingToolCallAccumulator sut = new();
		sut.Accumulate([Delta(0, "get_weather", null)]);
		sut.Accumulate([Delta(0, null, """{"ci""")]);
		sut.Accumulate([Delta(0, null, """ty":"Berlin"}""")]);

		// Act
		IReadOnlyList<OllamaToolCall>? built = sut.Build();

		// Assert
		Assert.True(sut.HasToolCalls);
		OllamaToolCall call = Assert.Single(built!);
		Assert.Equal("get_weather", call.Function.Name);
		Assert.Equal("Berlin", call.Function.Arguments?["city"]?.GetValue<string>());
	}

	/// <summary>
	/// Verifies that fragments for distinct indices produce distinct tool calls, ordered by index.
	/// </summary>
	[Fact]
	public void Build_WhenMultipleIndices_ProducesOneOrderedCallPerIndex()
	{
		// Arrange: deltas for index 1 arrive before index 0 to prove the output is index-ordered.
		StreamingToolCallAccumulator sut = new();
		sut.Accumulate([Delta(1, "second", "{}")]);
		sut.Accumulate([Delta(0, "first", "{}")]);

		// Act
		IReadOnlyList<OllamaToolCall>? built = sut.Build();

		// Assert
		Assert.Equal(2, built!.Count);
		Assert.Equal("first", built[0].Function.Name);
		Assert.Equal("second", built[1].Function.Name);
	}

	/// <summary>
	/// Verifies that a delta without an explicit index is attributed to the first (index 0) call.
	/// </summary>
	[Fact]
	public void Build_WhenIndexOmitted_AttributesFragmentsToFirstCall()
	{
		// Arrange
		StreamingToolCallAccumulator sut = new();
		sut.Accumulate([Delta(null, "fn", "{}")]);

		// Act
		IReadOnlyList<OllamaToolCall>? built = sut.Build();

		// Assert
		OllamaToolCall call = Assert.Single(built!);
		Assert.Equal("fn", call.Function.Name);
	}

	/// <summary>
	/// Verifies that the call id, which OpenAI delivers on the first delta of an index alongside the name,
	/// is carried onto the assembled tool call even though later argument-only fragments omit it.
	/// </summary>
	[Fact]
	public void Build_WhenFirstDeltaCarriesId_EmitsIdOnAssembledCall()
	{
		// Arrange: id and name arrive together; the argument fragment that follows carries no id.
		StreamingToolCallAccumulator sut = new();
		sut.Accumulate([Delta(0, "get_weather", null, id: "call_abc123")]);
		sut.Accumulate([Delta(0, null, "{}")]);

		// Act
		IReadOnlyList<OllamaToolCall>? built = sut.Build();

		// Assert
		OllamaToolCall call = Assert.Single(built!);
		Assert.Equal("call_abc123", call.Id);
		Assert.Equal("get_weather", call.Function.Name);
	}
}
