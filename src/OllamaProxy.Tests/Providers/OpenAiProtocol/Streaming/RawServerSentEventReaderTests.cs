// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text;

using OllamaProxy.Providers.OpenAiProtocol.Streaming;

namespace OllamaProxy.Tests.Providers.OpenAiProtocol.Streaming;

/// <summary>
/// Tests for <see cref="RawServerSentEventReader"/>, which extracts the verbatim JSON payload of each
/// OpenAI SSE <c>data:</c> frame without deserializing it. The story covers normal multi-frame
/// extraction, termination at the <c>[DONE]</c> sentinel, and skipping of blank and non-data lines —
/// the behavior the passthrough endpoints rely on to relay provider responses losslessly.
/// </summary>
[Trait("Category", "Unit")]
public sealed class RawServerSentEventReaderTests
{
	private static async Task<List<string>> ReadAllAsync(string sse)
	{
		using MemoryStream stream = new(Encoding.UTF8.GetBytes(sse));

		List<string> payloads = [];
		await foreach (string payload in RawServerSentEventReader.ReadDataPayloadsAsync(stream, CancellationToken.None))
		{
			payloads.Add(payload);
		}

		return payloads;
	}

	/// <summary>
	/// Verifies that each <c>data:</c> frame's JSON is yielded verbatim and in order, stopping at the
	/// <c>[DONE]</c> sentinel without yielding it.
	/// </summary>
	[Fact]
	public async Task ReadDataPayloadsAsync_WithDataFramesAndSentinel_YieldsPayloadsInOrder()
	{
		// Arrange
		string sse = string.Join(
			"\n",
			"""data: {"id":"1","choices":[{"delta":{"content":"a"}}]}""",
			"""data: {"id":"2","choices":[{"delta":{"content":"b"}}]}""",
			"data: [DONE]",
			"");

		// Act
		List<string> payloads = await ReadAllAsync(sse);

		// Assert
		Assert.Equal(2, payloads.Count);
		Assert.Equal("""{"id":"1","choices":[{"delta":{"content":"a"}}]}""", payloads[0]);
		Assert.Equal("""{"id":"2","choices":[{"delta":{"content":"b"}}]}""", payloads[1]);
	}

	/// <summary>
	/// Verifies that blank lines and non-data fields (such as SSE comments) are skipped rather than
	/// yielded.
	/// </summary>
	[Fact]
	public async Task ReadDataPayloadsAsync_WithBlankAndNonDataLines_SkipsThem()
	{
		// Arrange
		string sse = string.Join(
			"\n",
			": this is a comment",
			"",
			"""data: {"id":"1"}""",
			"event: ping",
			"""data: {"id":"2"}""",
			"");

		// Act
		List<string> payloads = await ReadAllAsync(sse);

		// Assert
		Assert.Equal(["""{"id":"1"}""", """{"id":"2"}"""], payloads);
	}

	/// <summary>
	/// Verifies that a stream ending without a <c>[DONE]</c> sentinel still yields every data frame and
	/// completes cleanly at end of stream.
	/// </summary>
	[Fact]
	public async Task ReadDataPayloadsAsync_WithoutSentinel_YieldsAllFramesThenCompletes()
	{
		// Arrange
		string sse = string.Join(
			"\n",
			"""data: {"id":"1"}""",
			"""data: {"id":"2"}""",
			"");

		// Act
		List<string> payloads = await ReadAllAsync(sse);

		// Assert
		Assert.Equal(2, payloads.Count);
	}
}
