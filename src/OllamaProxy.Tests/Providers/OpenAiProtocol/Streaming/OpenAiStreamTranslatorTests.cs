// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json.Nodes;

using OllamaProxy.Contracts.Ollama;
using OllamaProxy.Providers.OpenAiProtocol.Contracts;
using OllamaProxy.Providers.OpenAiProtocol.Streaming;

namespace OllamaProxy.Tests.Providers.OpenAiProtocol.Streaming;

/// <summary>
/// Tests for <see cref="OpenAiStreamTranslator"/>, which projects a parsed OpenAI chunk stream onto
/// the Ollama newline-delimited response stream. The story moves from the canonical content stream
/// (incremental text chunks plus one terminal done chunk), through reasoning and usage/finish-reason
/// handling on the terminal chunk, the empty stream, tool-call buffering, the per-delta
/// log-probability concatenation, and finally the constructor-style argument guards.
/// </summary>
[Trait("Category", "Unit")]
public sealed class OpenAiStreamTranslatorTests
{
	private static OpenAiChatCompletionChunk ContentChunk(
		string? text,
		string? finishReason = null,
		string? role         = null) => new(
		Id: "id",
		Model: "gpt-4o",
		Created: 0,
		Choices: [new OpenAiChatChunkChoice(0, new OpenAiChatMessage(role ?? string.Empty, text), finishReason)]);

	private static OpenAiChatCompletionChunk UsageChunk(OpenAiUsage usage) => new(
		Id: "id",
		Model: "gpt-4o",
		Created: 0,
		Choices: [],
		Usage: usage);

	private static OpenAiChatCompletionChunk NullChoicesUsageChunk(OpenAiUsage usage) => new(
		Id: "id",
		Model: "gpt-4o",
		Created: 0,
		Choices: null,
		Usage: usage);

	private static OpenAiChatCompletionChunk ReasoningChunk(string reasoning, string? role = null) => new(
		Id: "id",
		Model: "gpt-4o",
		Created: 0,
		Choices:
		[
			new OpenAiChatChunkChoice(
				0,
				new OpenAiChatMessage(role ?? string.Empty, null, ReasoningContent: reasoning),
				null)
		]);

	private static OpenAiChatCompletionChunk LogprobChunk(string? text, params string[] tokens) => new(
		Id: "id",
		Model: "gpt-4o",
		Created: 0,
		Choices:
		[
			new OpenAiChatChunkChoice(
				0,
				new OpenAiChatMessage("assistant", text),
				FinishReason: null,
				// OpenAI nests the per-token entries under a content array on each delta's logprobs.
				Logprobs: new JsonObject
				{
					["content"] = new JsonArray(
						tokens.Select(t => (JsonNode)new JsonObject { ["token"] = t }).ToArray())
				})
		]);

	private static async IAsyncEnumerable<OpenAiChatCompletionChunk> Source(params OpenAiChatCompletionChunk[] chunks)
	{
		foreach (OpenAiChatCompletionChunk chunk in chunks)
		{
			yield return chunk;
			await Task.Yield();
		}
	}

	private static async Task<List<OllamaChatResponse>> CollectAsync(IAsyncEnumerable<OllamaChatResponse> stream)
	{
		List<OllamaChatResponse> results = [];
		await foreach (OllamaChatResponse response in stream) results.Add(response);

		return results;
	}

	/// <summary>
	/// Verifies that each content delta becomes a non-terminal Ollama chunk and that the stream ends
	/// with exactly one terminal chunk carrying the measured duration and the default done reason.
	/// </summary>
	[Fact]
	public async Task TranslateAsync_WhenContentStreamed_EmitsIncrementalChunksThenTerminal()
	{
		// Arrange
		IAsyncEnumerable<OpenAiChatCompletionChunk> source = Source(
			ContentChunk("Hel", role: "assistant"),
			ContentChunk("lo"));

		// Act
		List<OllamaChatResponse> results = await CollectAsync(
			                                   OpenAiStreamTranslator.TranslateAsync(
				                                   source,
				                                   "m",
				                                   () => "ts",
				                                   () => 99L,
				                                   CancellationToken.None));

		// Assert: two content chunks (not done) plus a terminal chunk.
		Assert.Equal(3, results.Count);
		Assert.Equal("Hel", results[0].Message.Content);
		Assert.False(results[0].Done);
		Assert.Equal("lo", results[1].Message.Content);
		Assert.True(results[2].Done);
		Assert.Equal(99L, results[2].TotalDuration);
		Assert.Equal("stop", results[2].DoneReason);
	}

	/// <summary>
	/// Verifies that a reasoning delta becomes its own non-terminal Ollama chunk carrying the text under
	/// the native <c>thinking</c> field, kept separate from the visible content chunks.
	/// </summary>
	[Fact]
	public async Task TranslateAsync_WhenReasoningStreamed_EmitsThinkingChunksSeparateFromContent()
	{
		// Arrange: a reasoning delta precedes the visible content delta, as a reasoning model streams it.
		IAsyncEnumerable<OpenAiChatCompletionChunk> source = Source(
			ReasoningChunk("Think", role: "assistant"),
			ContentChunk("Hello"));

		// Act
		List<OllamaChatResponse> results = await CollectAsync(
			                                   OpenAiStreamTranslator.TranslateAsync(
				                                   source,
				                                   "m",
				                                   () => "ts",
				                                   () => 0L,
				                                   CancellationToken.None));

		// Assert: a thinking chunk (no content) precedes the content chunk, then the terminal chunk.
		Assert.Equal(3, results.Count);
		Assert.Equal("Think", results[0].Message.Thinking);
		Assert.Equal(string.Empty, results[0].Message.Content);
		Assert.False(results[0].Done);
		Assert.Null(results[1].Message.Thinking);
		Assert.Equal("Hello", results[1].Message.Content);
		Assert.True(results[2].Done);
	}

	/// <summary>
	/// Verifies that the finish reason from the content chunk and the token usage from the terminal
	/// usage-only chunk are both surfaced on the terminal Ollama chunk.
	/// </summary>
	[Fact]
	public async Task TranslateAsync_WhenFinishReasonAndUsagePresent_SurfacesThemOnTerminalChunk()
	{
		// Arrange
		IAsyncEnumerable<OpenAiChatCompletionChunk> source = Source(
			ContentChunk("hi", finishReason: "length"),
			UsageChunk(new OpenAiUsage(5, 7, 12)));

		// Act
		List<OllamaChatResponse> results = await CollectAsync(
			                                   OpenAiStreamTranslator.TranslateAsync(
				                                   source,
				                                   "m",
				                                   () => "ts",
				                                   () => 0L,
				                                   CancellationToken.None));

		// Assert
		OllamaChatResponse terminal = results[^1];
		Assert.Equal("length", terminal.DoneReason);
		Assert.Equal(5, terminal.PromptEvalCount);
		Assert.Equal(7, terminal.EvalCount);
	}

	/// <summary>
	/// Verifies that a terminal usage chunk whose <c>choices</c> array is omitted entirely (deserialized
	/// as <see langword="null"/>, distinct from the empty-array terminal chunk above) is handled like any
	/// other choice-less chunk — its usage is still surfaced — rather than dereferencing the missing
	/// collection. A non-conforming backend that sends <c>{"usage":{…}}</c> with no <c>choices</c> key is
	/// the real-world shape this guards.
	/// </summary>
	[Fact]
	public async Task TranslateAsync_WhenTerminalChunkChoicesNull_SurfacesUsage()
	{
		// Arrange: a content chunk followed by a usage-only chunk that carries no choices key at all.
		IAsyncEnumerable<OpenAiChatCompletionChunk> source = Source(
			ContentChunk("hi", finishReason: "stop"),
			NullChoicesUsageChunk(new OpenAiUsage(5, 7, 12)));

		// Act
		List<OllamaChatResponse> results = await CollectAsync(
			                                   OpenAiStreamTranslator.TranslateAsync(
				                                   source,
				                                   "m",
				                                   () => "ts",
				                                   () => 0L,
				                                   CancellationToken.None));

		// Assert: the usage from the null-choices chunk still lands on the terminal Ollama chunk.
		OllamaChatResponse terminal = results[^1];
		Assert.Equal(5, terminal.PromptEvalCount);
		Assert.Equal(7, terminal.EvalCount);
	}

	/// <summary>
	/// Verifies that an empty upstream stream still yields a single terminal chunk so the client always
	/// observes a well-formed end of stream.
	/// </summary>
	[Fact]
	public async Task TranslateAsync_WhenStreamEmpty_EmitsOnlyTerminalChunk()
	{
		// Act
		List<OllamaChatResponse> results = await CollectAsync(
			                                   OpenAiStreamTranslator.TranslateAsync(
				                                   Source(),
				                                   "m",
				                                   () => "ts",
				                                   () => 0L,
				                                   CancellationToken.None));

		// Assert
		OllamaChatResponse terminal = Assert.Single(results);
		Assert.True(terminal.Done);
	}

	/// <summary>
	/// Verifies that streamed tool-call fragments are withheld from intermediate chunks and assembled
	/// onto the terminal chunk's message.
	/// </summary>
	[Fact]
	public async Task TranslateAsync_WhenToolCallStreamed_AssemblesCallOnTerminalChunk()
	{
		// Arrange: a tool-call delta carries no textual content, so no intermediate chunk is emitted.
		OpenAiChatCompletionChunk toolChunk = new(
			Id: "id",
			Model: "gpt-4o",
			Created: 0,
			Choices:
			[
				new OpenAiChatChunkChoice(
					0,
					new OpenAiChatMessage(
						"assistant",
						Content: null,
						ToolCalls:
						[
							new OpenAiToolCall(
								null,
								"function",
								new OpenAiToolCallFunction("search", """{"q":"x"}"""),
								Index: 0)
						]),
					FinishReason: "tool_calls")
			]);

		// Act
		List<OllamaChatResponse> results = await CollectAsync(
			                                   OpenAiStreamTranslator.TranslateAsync(
				                                   Source(toolChunk),
				                                   "m",
				                                   () => "ts",
				                                   () => 0L,
				                                   CancellationToken.None));

		// Assert
		OllamaChatResponse terminal = Assert.Single(results);
		OllamaToolCall call = Assert.Single(terminal.Message.ToolCalls!);
		Assert.Equal("search", call.Function.Name);
		Assert.Equal("x", call.Function.Arguments?["q"]?.GetValue<string>());
	}

	/// <summary>
	/// Verifies that per-delta log-probability slices are concatenated in arrival order onto the single
	/// terminal chunk, mirroring the shape a non-streamed response reports.
	/// </summary>
	[Fact]
	public async Task TranslateAsync_WhenLogprobsStreamed_ConcatenatesOntoTerminalChunk()
	{
		// Arrange: two content deltas, each carrying its own logprob slice.
		IAsyncEnumerable<OpenAiChatCompletionChunk> source = Source(
			LogprobChunk("Hel", "Hel"),
			LogprobChunk("lo", "lo"));

		// Act
		List<OllamaChatResponse> results = await CollectAsync(
			                                   OpenAiStreamTranslator.TranslateAsync(
				                                   source,
				                                   "m",
				                                   () => "ts",
				                                   () => 0L,
				                                   CancellationToken.None));

		// Assert: the terminal chunk reports both slices, concatenated in arrival order.
		OllamaChatResponse terminal = results[^1];
		var array = Assert.IsType<JsonArray>(terminal.Logprobs);
		Assert.Equal(2, array.Count);
		Assert.Equal("Hel", array[0]?["token"]?.GetValue<string>());
		Assert.Equal("lo", array[1]?["token"]?.GetValue<string>());
	}

	/// <summary>
	/// Verifies that the terminal chunk omits <c>logprobs</c> when no delta carried any, so a plain
	/// stream does not emit an empty log-probability field.
	/// </summary>
	[Fact]
	public async Task TranslateAsync_WhenNoLogprobsStreamed_OmitsLogprobsOnTerminalChunk()
	{
		// Arrange: an ordinary content stream with no logprob slices.
		IAsyncEnumerable<OpenAiChatCompletionChunk> source = Source(ContentChunk("hi"));

		// Act
		List<OllamaChatResponse> results = await CollectAsync(
			                                   OpenAiStreamTranslator.TranslateAsync(
				                                   source,
				                                   "m",
				                                   () => "ts",
				                                   () => 0L,
				                                   CancellationToken.None));

		// Assert
		Assert.Null(results[^1].Logprobs);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiStreamTranslator.TranslateAsync"/> rejects a <see langword="null"/>
	/// chunk source.
	/// </summary>
	[Fact]
	public async Task TranslateAsync_WhenChunksNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
			                CollectAsync(
				                OpenAiStreamTranslator.TranslateAsync(
					                null!,
					                "m",
					                () => "ts",
					                () => 0L,
					                CancellationToken.None)));
		Assert.Equal("chunks", exception.ParamName);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiStreamTranslator.TranslateAsync"/> rejects a <see langword="null"/>
	/// timestamp provider.
	/// </summary>
	[Fact]
	public async Task TranslateAsync_WhenTimestampProviderNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
			                CollectAsync(
				                OpenAiStreamTranslator.TranslateAsync(
					                Source(),
					                "m",
					                null!,
					                () => 0L,
					                CancellationToken.None)));
		Assert.Equal("timestampProvider", exception.ParamName);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiStreamTranslator.TranslateAsync"/> rejects a <see langword="null"/>
	/// elapsed-duration provider.
	/// </summary>
	[Fact]
	public async Task TranslateAsync_WhenElapsedProviderNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
			                CollectAsync(
				                OpenAiStreamTranslator.TranslateAsync(
					                Source(),
					                "m",
					                () => "ts",
					                null!,
					                CancellationToken.None)));
		Assert.Equal("elapsedNanosecondsProvider", exception.ParamName);
	}
}
