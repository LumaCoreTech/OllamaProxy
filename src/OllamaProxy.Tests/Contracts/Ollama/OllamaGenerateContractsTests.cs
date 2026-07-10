// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json;
using System.Text.Json.Nodes;

using OllamaProxy.Contracts.Ollama;

namespace OllamaProxy.Tests.Contracts.Ollama;

/// <summary>
/// Tests that pin the JSON contract of the Ollama <c>POST /api/generate</c> payloads
/// (<see cref="OllamaGenerateRequest"/> and <see cref="OllamaGenerateResponse"/>). The request is an
/// <em>inbound</em> shape the proxy must deserialize from real Ollama clients, so its tests drive
/// deserialization from canonical wire JSON; the response is an <em>outbound</em> shape the proxy serializes
/// through <see cref="OllamaJson.Options"/>, so its tests assert the emitted key set exactly — including that
/// null optional fields are omitted, which is what keeps the terminal-only timing/token fields off the
/// incremental stream chunks.
/// </summary>
[Trait("Category", "Unit")]
public sealed class OllamaGenerateContractsTests
{
	// The exact options the production endpoints serialize Ollama responses with: explicit [JsonPropertyName]
	// keys plus WhenWritingNull omission. Mirroring them here makes the emitted-key assertions authoritative.
	private static readonly JsonSerializerOptions Options = OllamaJson.Options;

	#region OllamaGenerateRequest

	/// <summary>
	/// Verifies that a full <c>/api/generate</c> request body deserializes into every mapped property, so the
	/// proxy reads each field a real Ollama client can send rather than silently dropping any of them.
	/// </summary>
	[Fact]
	public void Deserialize_GenerateRequest_BindsEveryField()
	{
		// Arrange
		const string json = """
		                    {
		                      "model": "gpt-4o",
		                      "prompt": "Hello",
		                      "suffix": " world",
		                      "system": "Be terse.",
		                      "images": ["aGVsbG8="],
		                      "format": "json",
		                      "options": { "temperature": 0.5 },
		                      "stream": false,
		                      "think": "high",
		                      "raw": true,
		                      "keep_alive": "5m",
		                      "logprobs": true,
		                      "top_logprobs": 3
		                    }
		                    """;

		// Act
		var request = JsonSerializer.Deserialize<OllamaGenerateRequest>(json, Options)!;

		// Assert
		Assert.Equal("gpt-4o", request.Model);
		Assert.Equal("Hello", request.Prompt);
		Assert.Equal(" world", request.Suffix);
		Assert.Equal("Be terse.", request.System);
		Assert.Equal(["aGVsbG8="], request.Images!);
		Assert.Equal("json", request.Format!.GetValue<string>());
		Assert.NotNull(request.Options);
		Assert.False(request.Stream);
		Assert.Equal("high", request.Think!.GetValue<string>());
		Assert.True(request.Raw);
		Assert.Equal("5m", request.KeepAlive!.GetValue<string>());
		Assert.True(request.Logprobs);
		Assert.Equal(3, request.TopLogprobs);
	}

	/// <summary>
	/// Verifies that a minimal request (only the required <c>model</c>) deserializes with every optional field
	/// left at its <see langword="null"/> default, confirming the proxy tolerates the sparse bodies a warmup or
	/// bare completion request sends.
	/// </summary>
	[Fact]
	public void Deserialize_GenerateRequest_WhenOnlyModelPresent_LeavesOptionalsNull()
	{
		// Arrange
		const string json = """{ "model": "gpt-4o" }""";

		// Act
		var request = JsonSerializer.Deserialize<OllamaGenerateRequest>(json, Options)!;

		// Assert
		Assert.Equal("gpt-4o", request.Model);
		Assert.Null(request.Prompt);
		Assert.Null(request.Suffix);
		Assert.Null(request.System);
		Assert.Null(request.Images);
		Assert.Null(request.Format);
		Assert.Null(request.Options);
		Assert.Null(request.Stream);
		Assert.Null(request.Think);
		Assert.Null(request.Raw);
		Assert.Null(request.KeepAlive);
		Assert.Null(request.Logprobs);
		Assert.Null(request.TopLogprobs);
	}

	#endregion

	#region OllamaGenerateResponse

	/// <summary>
	/// Verifies that a terminal <c>/api/generate</c> chunk serializes to the complete key set — the streamed
	/// text fields plus the terminal-only timing and token accounting — so a client receives the full accounting
	/// on the final chunk.
	/// </summary>
	[Fact]
	public void Serialize_GenerateResponse_TerminalChunk_EmitsEveryField()
	{
		// Arrange
		OllamaGenerateResponse response = new(
			Model: "gpt-4o",
			CreatedAt: "2026-07-10T00:00:00Z",
			Response: "Hi",
			Done: true,
			DoneReason: "stop",
			TotalDuration: 1_000,
			LoadDuration: 200,
			PromptEvalCount: 5,
			PromptEvalDuration: 300,
			EvalCount: 7,
			EvalDuration: 400,
			Thinking: "reasoning",
			Logprobs: JsonNode.Parse("[]"));

		// Act
		JsonObject json = JsonSerializer.SerializeToNode(response, Options)!.AsObject();

		// Assert: the complete terminal-chunk key set, in declaration order, with every field verified by value.
		Assert.Equal(
			[
				"model", "created_at", "response", "done", "done_reason", "total_duration", "load_duration",
				"prompt_eval_count", "prompt_eval_duration", "eval_count", "eval_duration", "thinking", "logprobs"
			],
			json.Select(property => property.Key));
		Assert.Equal("gpt-4o", (string)json["model"]!);
		Assert.Equal("2026-07-10T00:00:00Z", (string)json["created_at"]!);
		Assert.Equal("Hi", (string)json["response"]!);
		Assert.True((bool)json["done"]!);
		Assert.Equal("stop", (string)json["done_reason"]!);
		Assert.Equal(1_000, (long)json["total_duration"]!);
		Assert.Equal(200, (long)json["load_duration"]!);
		Assert.Equal(5, (int)json["prompt_eval_count"]!);
		Assert.Equal(300, (long)json["prompt_eval_duration"]!);
		Assert.Equal(7, (int)json["eval_count"]!);
		Assert.Equal(400, (long)json["eval_duration"]!);
		Assert.Equal("reasoning", (string)json["thinking"]!);
		Assert.Empty(json["logprobs"]!.AsArray());
	}

	/// <summary>
	/// Verifies that an incremental (non-terminal) chunk omits every null terminal-only field, emitting exactly
	/// the streamed text fields. This is the behavior that keeps timing/token accounting off the mid-stream
	/// chunks, so a client does not misread a partial chunk as final.
	/// </summary>
	[Fact]
	public void Serialize_GenerateResponse_IncrementalChunk_OmitsNullTerminalFields()
	{
		// Arrange
		OllamaGenerateResponse response = new(
			Model: "gpt-4o",
			CreatedAt: "2026-07-10T00:00:00Z",
			Response: "partial",
			Done: false);

		// Act
		JsonObject json = JsonSerializer.SerializeToNode(response, Options)!.AsObject();

		// Assert: only the always-present streamed fields survive null omission.
		Assert.Equal(["model", "created_at", "response", "done"], json.Select(property => property.Key));
		Assert.Equal("partial", (string)json["response"]!);
		Assert.False((bool)json["done"]!);
	}

	#endregion
}
