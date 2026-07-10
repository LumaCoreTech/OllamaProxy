// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json;
using System.Text.Json.Nodes;

using OllamaProxy.Providers.OpenAiProtocol;
using OllamaProxy.Providers.OpenAiProtocol.Contracts;

namespace OllamaProxy.Tests.Providers.OpenAiProtocol.Contracts;

/// <summary>
/// Tests that pin the JSON wire contract of the OpenAI-protocol payloads the proxy exchanges with an upstream
/// backend. Requests (<see cref="OpenAiChatRequest"/>, <see cref="OpenAiEmbeddingRequest"/>) are <em>outbound</em>
/// shapes the proxy serializes and sends upstream, so their tests assert the emitted key set exactly — including
/// that null optionals are omitted, which is what keeps a strict OpenAI endpoint from receiving a field it would
/// reject. Responses (<see cref="OpenAiChatCompletion"/>, <see cref="OpenAiChatCompletionChunk"/>,
/// <see cref="OpenAiEmbeddingResponse"/>, <see cref="OpenAiErrorEnvelope"/>) are <em>inbound</em> shapes the proxy
/// deserializes from real backends, so their tests drive binding from canonical wire JSON.
/// Every populated field is asserted under its own key with a distinct value, so an incorrect
/// <c>[JsonPropertyName]</c> or a swapped mapping surfaces as a value mismatch rather than slipping through.
/// </summary>
[Trait("Category", "Unit")]
public sealed class OpenAiProtocolContractsTests
{
	// The exact options the provider (de)serializes OpenAI wire payloads with: explicit [JsonPropertyName]
	// keys plus WhenWritingNull omission. Mirroring them here makes the emitted-key assertions authoritative.
	private static readonly JsonSerializerOptions Options = OpenAiSerialization.Options;

	#region OpenAiChatRequest (outbound)

	/// <summary>
	/// Verifies that a fully populated chat request serializes to the complete key set, each field under its own
	/// key with a distinct value, so a wrong or swapped <c>[JsonPropertyName]</c> on any sampling field is caught
	/// before it reaches a strict OpenAI backend.
	/// </summary>
	[Fact]
	public void Serialize_ChatRequest_EmitsEveryField()
	{
		// Arrange: distinct values per field so a swapped mapping surfaces as a value mismatch.
		OpenAiChatRequest request = new(
			Model: "openai/gpt-4o",
			Messages: [new OpenAiChatMessage("user", JsonValue.Create("hello"))],
			Stream: true,
			StreamOptions: new OpenAiStreamOptions(IncludeUsage: true),
			Tools:
			[
				new OpenAiTool("function", new OpenAiToolFunction("get_time", "Returns the time", JsonNode.Parse("{}")))
			],
			ResponseFormat: JsonNode.Parse("""{ "type": "json_object" }"""),
			Temperature: 0.25,
			TopP: 0.9,
			Seed: 42,
			MaxCompletionTokens: 128,
			Stop: ["</s>"],
			FrequencyPenalty: 0.5,
			PresencePenalty: 0.75,
			Logprobs: true,
			TopLogprobs: 3);

		// Act
		JsonObject json = JsonSerializer.SerializeToNode(request, Options)!.AsObject();

		// Assert: the complete key set in declaration order, every field verified by value.
		Assert.Equal(
			[
				"model", "messages", "stream", "stream_options", "tools", "response_format", "temperature",
				"top_p", "seed", "max_completion_tokens", "stop", "frequency_penalty", "presence_penalty",
				"logprobs", "top_logprobs"
			],
			json.Select(property => property.Key));
		Assert.Equal("openai/gpt-4o", (string)json["model"]!);
		JsonObject message = json["messages"]!.AsArray().Single()!.AsObject();
		Assert.Equal("user", (string)message["role"]!);
		Assert.Equal("hello", (string)message["content"]!);
		Assert.True((bool)json["stream"]!);
		Assert.True((bool)json["stream_options"]!.AsObject()["include_usage"]!);
		JsonObject tool = json["tools"]!.AsArray().Single()!.AsObject();
		Assert.Equal("function", (string)tool["type"]!);
		Assert.Equal("get_time", (string)tool["function"]!.AsObject()["name"]!);
		Assert.Equal("json_object", (string)json["response_format"]!.AsObject()["type"]!);
		Assert.Equal(0.25, (double)json["temperature"]!);
		Assert.Equal(0.9, (double)json["top_p"]!);
		Assert.Equal(42, (int)json["seed"]!);
		Assert.Equal(128, (int)json["max_completion_tokens"]!);
		Assert.Equal(["</s>"], json["stop"]!.AsArray().Select(node => (string)node!));
		Assert.Equal(0.5, (double)json["frequency_penalty"]!);
		Assert.Equal(0.75, (double)json["presence_penalty"]!);
		Assert.True((bool)json["logprobs"]!);
		Assert.Equal(3, (int)json["top_logprobs"]!);
	}

	/// <summary>
	/// Verifies that a minimal chat request (only the required <c>model</c>, <c>messages</c>, and <c>stream</c>)
	/// omits every null optional field, so the proxy never sends an empty sampling field a strict OpenAI endpoint
	/// could reject.
	/// </summary>
	[Fact]
	public void Serialize_ChatRequest_WhenOnlyRequiredFieldsSet_OmitsNullOptionals()
	{
		// Arrange
		OpenAiChatRequest request = new(
			Model: "openai/gpt-4o",
			Messages: [new OpenAiChatMessage("user", JsonValue.Create("hi"))],
			Stream: false);

		// Act
		JsonObject json = JsonSerializer.SerializeToNode(request, Options)!.AsObject();

		// Assert: only the always-present fields survive null omission.
		Assert.Equal(["model", "messages", "stream"], json.Select(property => property.Key));
		Assert.False((bool)json["stream"]!);
	}

	/// <summary>
	/// Verifies that a fully populated assistant message serializes every field under its own key, so a swapped
	/// reasoning-field name (the several vendor spellings) or tool-call key cannot slip through unnoticed.
	/// </summary>
	[Fact]
	public void Serialize_ChatMessage_EmitsEveryField()
	{
		// Arrange: distinct values per field so a swap between the reasoning spellings is caught.
		OpenAiChatMessage message = new(
			Role: "assistant",
			Content: JsonValue.Create("answer"),
			ToolCalls:
			[
				new OpenAiToolCall("call_1", "function", new OpenAiToolCallFunction("get_time", "{}"), Index: 0)
			],
			ToolCallId: "call_prev",
			Name: "tool_name",
			ReasoningContent: "content-spelling",
			Reasoning: "openrouter-spelling",
			ReasoningDetails: JsonNode.Parse("""[{ "type": "encrypted" }]"""));

		// Act
		JsonObject json = JsonSerializer.SerializeToNode(message, Options)!.AsObject();

		// Assert
		Assert.Equal(
			[
				"role", "content", "tool_calls", "tool_call_id", "name", "reasoning_content", "reasoning",
				"reasoning_details"
			],
			json.Select(property => property.Key));
		Assert.Equal("assistant", (string)json["role"]!);
		Assert.Equal("answer", (string)json["content"]!);
		JsonObject toolCall = json["tool_calls"]!.AsArray().Single()!.AsObject();
		Assert.Equal("call_1", (string)toolCall["id"]!);
		Assert.Equal("function", (string)toolCall["type"]!);
		Assert.Equal("get_time", (string)toolCall["function"]!.AsObject()["name"]!);
		Assert.Equal(0, (int)toolCall["index"]!);
		Assert.Equal("call_prev", (string)json["tool_call_id"]!);
		Assert.Equal("tool_name", (string)json["name"]!);
		Assert.Equal("content-spelling", (string)json["reasoning_content"]!);
		Assert.Equal("openrouter-spelling", (string)json["reasoning"]!);
		Assert.Equal("encrypted", (string)json["reasoning_details"]!.AsArray().Single()!.AsObject()["type"]!);
	}

	#endregion

	#region OpenAiEmbeddingRequest (outbound)

	/// <summary>
	/// Verifies that an embedding request serializes every field under its own key, including the explicit
	/// <c>encoding_format</c>, so the proxy asks the backend for plain float vectors under the correct key.
	/// </summary>
	[Fact]
	public void Serialize_EmbeddingRequest_EmitsEveryField()
	{
		// Arrange
		OpenAiEmbeddingRequest request = new(
			Model: "text-embedding-3-small",
			Input: JsonNode.Parse("""["a", "b"]"""),
			EncodingFormat: "float");

		// Act
		JsonObject json = JsonSerializer.SerializeToNode(request, Options)!.AsObject();

		// Assert
		Assert.Equal(["model", "input", "encoding_format"], json.Select(property => property.Key));
		Assert.Equal("text-embedding-3-small", (string)json["model"]!);
		Assert.Equal(["a", "b"], json["input"]!.AsArray().Select(node => (string)node!));
		Assert.Equal("float", (string)json["encoding_format"]!);
	}

	#endregion

	#region OpenAiChatCompletion (inbound)

	/// <summary>
	/// Verifies that a full non-streaming chat completion deserializes every mapped field, so the proxy reads the
	/// id, model, timestamp, first choice, and usage a real backend returns rather than silently dropping any.
	/// </summary>
	[Fact]
	public void Deserialize_ChatCompletion_BindsEveryField()
	{
		// Arrange
		const string json = """
		                    {
		                      "id": "chatcmpl-1",
		                      "model": "gpt-4o",
		                      "created": 1700000000,
		                      "choices": [
		                    	{
		                    	  "index": 0,
		                    	  "message": { "role": "assistant", "content": "hello" },
		                    	  "finish_reason": "stop"
		                    	}
		                      ],
		                      "usage": { "prompt_tokens": 11, "completion_tokens": 22, "total_tokens": 33 }
		                    }
		                    """;

		// Act
		var completion = JsonSerializer.Deserialize<OpenAiChatCompletion>(json, Options)!;

		// Assert
		Assert.Equal("chatcmpl-1", completion.Id);
		Assert.Equal("gpt-4o", completion.Model);
		Assert.Equal(1_700_000_000, completion.Created);
		OpenAiChatChoice choice = Assert.Single(completion.Choices!);
		Assert.Equal(0, choice.Index);
		Assert.Equal("assistant", choice.Message.Role);
		Assert.Equal("hello", choice.Message.Content!.GetValue<string>());
		Assert.Equal("stop", choice.FinishReason);
		Assert.Equal(11, completion.Usage!.PromptTokens);
		Assert.Equal(22, completion.Usage.CompletionTokens);
		Assert.Equal(33, completion.Usage.TotalTokens);
	}

	/// <summary>
	/// Verifies that a streamed chunk deserializes its delta and terminal usage, so the proxy correctly reads the
	/// incremental content and the final token accounting a backend emits when <c>include_usage</c> is set.
	/// </summary>
	[Fact]
	public void Deserialize_ChatCompletionChunk_BindsDeltaAndUsage()
	{
		// Arrange
		const string json = """
		                    {
		                      "id": "chatcmpl-1",
		                      "model": "gpt-4o",
		                      "created": 1700000000,
		                      "choices": [
		                    	{ "index": 0, "delta": { "role": "assistant", "content": "partial" }, "finish_reason": null }
		                      ],
		                      "usage": { "prompt_tokens": 5, "completion_tokens": 7, "total_tokens": 12 }
		                    }
		                    """;

		// Act
		var chunk = JsonSerializer.Deserialize<OpenAiChatCompletionChunk>(json, Options)!;

		// Assert
		Assert.Equal("chatcmpl-1", chunk.Id);
		Assert.Equal("gpt-4o", chunk.Model);
		Assert.Equal(1_700_000_000, chunk.Created);
		OpenAiChatChunkChoice choice = Assert.Single(chunk.Choices!);
		Assert.Equal(0, choice.Index);
		Assert.Equal("assistant", choice.Delta!.Role);
		Assert.Equal("partial", choice.Delta.Content!.GetValue<string>());
		Assert.Null(choice.FinishReason);
		Assert.Equal(5, chunk.Usage!.PromptTokens);
		Assert.Equal(7, chunk.Usage.CompletionTokens);
		Assert.Equal(12, chunk.Usage.TotalTokens);
	}

	#endregion

	#region OpenAiEmbeddingResponse (inbound)

	/// <summary>
	/// Verifies that an embeddings response deserializes every field, including each data entry's vector and
	/// index and the usage accounting, so the proxy maps the vectors and token counts a backend returns.
	/// </summary>
	[Fact]
	public void Deserialize_EmbeddingResponse_BindsEveryField()
	{
		// Arrange
		const string json = """
		                    {
		                      "data": [
		                    	{ "embedding": [0.1, 0.2], "index": 0 },
		                    	{ "embedding": [0.3, 0.4], "index": 1 }
		                      ],
		                      "model": "text-embedding-3-small",
		                      "usage": { "prompt_tokens": 5, "total_tokens": 5 }
		                    }
		                    """;

		// Act
		var response = JsonSerializer.Deserialize<OpenAiEmbeddingResponse>(json, Options)!;

		// Assert
		Assert.Equal(2, response.Data!.Count);
		Assert.Equal([0.1f, 0.2f], response.Data[0].Embedding);
		Assert.Equal(0, response.Data[0].Index);
		Assert.Equal([0.3f, 0.4f], response.Data[1].Embedding);
		Assert.Equal(1, response.Data[1].Index);
		Assert.Equal("text-embedding-3-small", response.Model);
		Assert.Equal(5, response.Usage!.PromptTokens);
		Assert.Equal(5, response.Usage.TotalTokens);
	}

	#endregion

	#region OpenAiErrorEnvelope (inbound)

	/// <summary>
	/// Verifies that an OpenAI error envelope deserializes its nested detail fields, so the proxy reads the
	/// message, type, and code a backend returns on a non-success response.
	/// </summary>
	[Fact]
	public void Deserialize_ErrorEnvelope_BindsNestedDetail()
	{
		// Arrange
		const string json = """
		                    {
		                      "error": {
		                    	"message": "model not found",
		                    	"type": "invalid_request_error",
		                    	"code": "model_not_found"
		                      }
		                    }
		                    """;

		// Act
		var envelope = JsonSerializer.Deserialize<OpenAiErrorEnvelope>(json, Options)!;

		// Assert
		Assert.Equal("model not found", envelope.Error!.Message);
		Assert.Equal("invalid_request_error", envelope.Error.Type);
		Assert.Equal("model_not_found", envelope.Error.Code);
	}

	#endregion

	#region Inbound robustness (forward compatibility)

	/// <summary>
	/// Verifies that a chat completion carrying vendor-specific extra fields the proxy does not model (both at the
	/// top level and nested in the message) still binds the known fields and silently ignores the unknowns, the
	/// forward-compatibility behavior real OpenAI-compatible backends rely on when they add proprietary members.
	/// </summary>
	[Fact]
	public void Deserialize_ChatCompletion_WithUnknownFields_IgnoresThemAndBindsKnown()
	{
		// Arrange: system_fingerprint, service_tier, and a nested vendor_extra are not on the proxy's contracts.
		const string json = """
		                    {
		                      "id": "chatcmpl-1",
		                      "model": "gpt-4o",
		                      "created": 1700000000,
		                      "system_fingerprint": "fp_abc123",
		                      "service_tier": "default",
		                      "choices": [
		                    	{
		                    	  "index": 0,
		                    	  "message": { "role": "assistant", "content": "hello", "vendor_extra": { "x": 1 } },
		                    	  "finish_reason": "stop"
		                    	}
		                      ],
		                      "usage": { "prompt_tokens": 1, "completion_tokens": 2, "total_tokens": 3 }
		                    }
		                    """;

		// Act
		var completion = JsonSerializer.Deserialize<OpenAiChatCompletion>(json, Options)!;

		// Assert: the known fields bound and the unknown members were tolerated (no throw).
		Assert.Equal("chatcmpl-1", completion.Id);
		OpenAiChatChoice choice = Assert.Single(completion.Choices!);
		Assert.Equal("hello", choice.Message.Content!.GetValue<string>());
		Assert.Equal("stop", choice.FinishReason);
	}

	/// <summary>
	/// Verifies that an assistant message with an explicit <c>content: null</c> binds a null
	/// <see cref="OpenAiChatMessage.Content"/> rather than throwing, the shape a backend emits for a
	/// tool-call-only turn where there is no visible text.
	/// </summary>
	[Fact]
	public void Deserialize_ChatCompletion_WhenAssistantContentIsNull_BindsNullContent()
	{
		// Arrange
		const string json = """
		                    {
		                      "id": "chatcmpl-1",
		                      "model": "gpt-4o",
		                      "created": 1700000000,
		                      "choices": [
		                    	{ "index": 0, "message": { "role": "assistant", "content": null }, "finish_reason": "stop" }
		                      ]
		                    }
		                    """;

		// Act
		var completion = JsonSerializer.Deserialize<OpenAiChatCompletion>(json, Options)!;

		// Assert
		OpenAiChatChoice choice = Assert.Single(completion.Choices!);
		Assert.Equal("assistant", choice.Message.Role);
		Assert.Null(choice.Message.Content);
	}

	/// <summary>
	/// Verifies that a tool-call-only assistant message (null content, a populated <c>tool_calls</c> array) binds
	/// the tool call with its function name and JSON-string arguments, the exact shape the proxy must read to
	/// translate an OpenAI tool call into the Ollama structured form.
	/// </summary>
	[Fact]
	public void Deserialize_ChatCompletion_WhenToolCallOnlyMessage_BindsToolCalls()
	{
		// Arrange
		const string json = """
		                    {
		                      "id": "chatcmpl-1",
		                      "model": "gpt-4o",
		                      "created": 1700000000,
		                      "choices": [
		                    	{
		                    	  "index": 0,
		                    	  "message": {
		                    		"role": "assistant",
		                    		"content": null,
		                    		"tool_calls": [
		                    		  { "id": "call_1", "type": "function", "function": { "name": "get_time", "arguments": "{\"tz\":\"utc\"}" } }
		                    		]
		                    	  },
		                    	  "finish_reason": "tool_calls"
		                    	}
		                      ]
		                    }
		                    """;

		// Act
		var completion = JsonSerializer.Deserialize<OpenAiChatCompletion>(json, Options)!;

		// Assert
		OpenAiChatChoice choice = Assert.Single(completion.Choices!);
		Assert.Null(choice.Message.Content);
		Assert.Equal("tool_calls", choice.FinishReason);
		OpenAiToolCall toolCall = Assert.Single(choice.Message.ToolCalls!);
		Assert.Equal("call_1", toolCall.Id);
		Assert.Equal("function", toolCall.Type);
		Assert.Equal("get_time", toolCall.Function!.Name);
		Assert.Equal("{\"tz\":\"utc\"}", toolCall.Function.Arguments);
	}

	/// <summary>
	/// Verifies that a chat completion with no <c>usage</c> object binds a null <see cref="OpenAiChatCompletion.Usage"/>,
	/// the shape a backend returns when it does not report token accounting, so the proxy treats missing usage as
	/// "unknown" rather than failing to deserialize.
	/// </summary>
	[Fact]
	public void Deserialize_ChatCompletion_WhenUsageAbsent_BindsNullUsage()
	{
		// Arrange
		const string json = """
		                    {
		                      "id": "chatcmpl-1",
		                      "model": "gpt-4o",
		                      "created": 1700000000,
		                      "choices": [
		                    	{ "index": 0, "message": { "role": "assistant", "content": "hi" }, "finish_reason": "stop" }
		                      ]
		                    }
		                    """;

		// Act
		var completion = JsonSerializer.Deserialize<OpenAiChatCompletion>(json, Options)!;

		// Assert
		Assert.Null(completion.Usage);
		Assert.Single(completion.Choices!);
	}

	/// <summary>
	/// Verifies that a chat completion with an empty <c>choices</c> array binds an empty (non-null) list, so the
	/// proxy's "first choice" access sees an empty collection rather than a null reference — the malformed-but-2xx
	/// shape a non-conforming backend can return.
	/// </summary>
	[Fact]
	public void Deserialize_ChatCompletion_WhenChoicesEmpty_BindsEmptyList()
	{
		// Arrange
		const string json = """
		                    {
		                      "id": "chatcmpl-1",
		                      "model": "gpt-4o",
		                      "created": 1700000000,
		                      "choices": []
		                    }
		                    """;

		// Act
		var completion = JsonSerializer.Deserialize<OpenAiChatCompletion>(json, Options)!;

		// Assert
		Assert.NotNull(completion.Choices);
		Assert.Empty(completion.Choices);
	}

	/// <summary>
	/// Verifies that an embeddings response omitting the <c>data</c> array binds a null
	/// <see cref="OpenAiEmbeddingResponse.Data"/>, the non-conforming 2xx shape the contract documents, so the
	/// proxy detects the missing vectors rather than throwing during deserialization.
	/// </summary>
	[Fact]
	public void Deserialize_EmbeddingResponse_WhenDataAbsent_BindsNullData()
	{
		// Arrange
		const string json = """{ "model": "text-embedding-3-small" }""";

		// Act
		var response = JsonSerializer.Deserialize<OpenAiEmbeddingResponse>(json, Options)!;

		// Assert
		Assert.Null(response.Data);
		Assert.Equal("text-embedding-3-small", response.Model);
	}

	/// <summary>
	/// Verifies that an error envelope whose detail omits the optional <c>code</c> binds a null
	/// <see cref="OpenAiError.Code"/> while still binding the message and type, so a backend that reports an error
	/// without a machine-readable code is still surfaced to the client.
	/// </summary>
	[Fact]
	public void Deserialize_ErrorEnvelope_WhenCodeAbsent_BindsNullCode()
	{
		// Arrange
		const string json = """
		                    {
		                      "error": { "message": "bad request", "type": "invalid_request_error" }
		                    }
		                    """;

		// Act
		var envelope = JsonSerializer.Deserialize<OpenAiErrorEnvelope>(json, Options)!;

		// Assert
		Assert.Equal("bad request", envelope.Error!.Message);
		Assert.Equal("invalid_request_error", envelope.Error.Type);
		Assert.Null(envelope.Error.Code);
	}

	#endregion
}
