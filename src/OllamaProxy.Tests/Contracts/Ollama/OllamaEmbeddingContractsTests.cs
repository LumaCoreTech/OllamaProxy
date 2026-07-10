// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json;
using System.Text.Json.Nodes;

using OllamaProxy.Contracts.Ollama;

namespace OllamaProxy.Tests.Contracts.Ollama;

/// <summary>
/// Tests that pin the JSON contract of the Ollama embeddings payloads for both the current
/// <c>POST /api/embed</c> surface (<see cref="OllamaEmbedRequest"/>, <see cref="OllamaEmbedResponse"/>) and the
/// legacy <c>POST /api/embeddings</c> surface (<see cref="OllamaLegacyEmbeddingsRequest"/>,
/// <see cref="OllamaLegacyEmbeddingsResponse"/>). Requests are inbound shapes driven from canonical wire JSON;
/// responses are outbound shapes serialized through <see cref="OllamaJson.Options"/>, asserted by their exact
/// emitted key set including null omission.
/// </summary>
[Trait("Category", "Unit")]
public sealed class OllamaEmbeddingContractsTests
{
	private static readonly JsonSerializerOptions Options = OllamaJson.Options;

	#region OllamaEmbedRequest

	/// <summary>
	/// Verifies that a full <c>/api/embed</c> request deserializes every mapped field, including the polymorphic
	/// <c>input</c> (array form) preserved as a raw node for later normalization.
	/// </summary>
	[Fact]
	public void Deserialize_EmbedRequest_BindsEveryField()
	{
		// Arrange
		const string json = """
		                    {
		                      "model": "embed-model",
		                      "input": ["a", "b"],
		                      "truncate": true,
		                      "dimensions": 256,
		                      "keep_alive": "5m",
		                      "options": { "temperature": 0 }
		                    }
		                    """;

		// Act
		var request = JsonSerializer.Deserialize<OllamaEmbedRequest>(json, Options)!;

		// Assert
		Assert.Equal("embed-model", request.Model);
		Assert.Equal(2, request.Input!.AsArray().Count);
		Assert.True(request.Truncate);
		Assert.Equal(256, request.Dimensions);
		Assert.Equal("5m", request.KeepAlive!.GetValue<string>());
		Assert.NotNull(request.Options);
	}

	/// <summary>
	/// Verifies that a single-string <c>input</c> also binds, confirming the raw-node modeling tolerates both the
	/// scalar and array shapes the Ollama embed API accepts.
	/// </summary>
	[Fact]
	public void Deserialize_EmbedRequest_WhenInputIsSingleString_BindsScalar()
	{
		// Arrange
		const string json = """{ "model": "embed-model", "input": "hello" }""";

		// Act
		var request = JsonSerializer.Deserialize<OllamaEmbedRequest>(json, Options)!;

		// Assert
		Assert.Equal("hello", request.Input!.GetValue<string>());
		Assert.Null(request.Truncate);
		Assert.Null(request.Dimensions);
	}

	#endregion

	#region OllamaEmbedResponse

	/// <summary>
	/// Verifies that a full <c>/api/embed</c> response serializes to the complete key set including the optional
	/// timing/token fields, so a client receives the whole accounting when the proxy supplies it.
	/// </summary>
	[Fact]
	public void Serialize_EmbedResponse_WithTimings_EmitsEveryField()
	{
		// Arrange: distinct duration/count values so a swapped mapping between them surfaces as a mismatch.
		OllamaEmbedResponse response = new(
			Model: "embed-model",
			Embeddings: [[0.1f, 0.2f], [0.3f, 0.4f]],
			TotalDuration: 1_000,
			LoadDuration: 200,
			PromptEvalCount: 5);

		// Act
		JsonObject json = JsonSerializer.SerializeToNode(response, Options)!.AsObject();

		// Assert: every field verified under its own key, including both embedding rows.
		Assert.Equal(
			["model", "embeddings", "total_duration", "load_duration", "prompt_eval_count"],
			json.Select(property => property.Key));
		Assert.Equal("embed-model", (string)json["model"]!);
		JsonArray embeddings = json["embeddings"]!.AsArray();
		Assert.Equal([0.1f, 0.2f], embeddings[0]!.AsArray().Select(n => (float)n!));
		Assert.Equal([0.3f, 0.4f], embeddings[1]!.AsArray().Select(n => (float)n!));
		Assert.Equal(1_000, (long)json["total_duration"]!);
		Assert.Equal(200, (long)json["load_duration"]!);
		Assert.Equal(5, (int)json["prompt_eval_count"]!);
	}

	/// <summary>
	/// Verifies that a response without timings omits every null optional field, emitting only the required
	/// <c>model</c> and <c>embeddings</c> keys.
	/// </summary>
	[Fact]
	public void Serialize_EmbedResponse_WithoutTimings_OmitsNullFields()
	{
		// Arrange
		OllamaEmbedResponse response = new(Model: "embed-model", Embeddings: [[0.1f, 0.2f]]);

		// Act
		JsonObject json = JsonSerializer.SerializeToNode(response, Options)!.AsObject();

		// Assert
		Assert.Equal(["model", "embeddings"], json.Select(property => property.Key));
	}

	#endregion

	#region OllamaLegacyEmbeddingsRequest

	/// <summary>
	/// Verifies that a legacy <c>/api/embeddings</c> request binds its single <c>prompt</c> and the required
	/// <c>model</c>, so clients that have not migrated to <c>/api/embed</c> still deserialize correctly.
	/// </summary>
	[Fact]
	public void Deserialize_LegacyEmbeddingsRequest_BindsPromptAndModel()
	{
		// Arrange
		const string json = """{ "model": "embed-model", "prompt": "hello" }""";

		// Act
		var request = JsonSerializer.Deserialize<OllamaLegacyEmbeddingsRequest>(json, Options)!;

		// Assert
		Assert.Equal("embed-model", request.Model);
		Assert.Equal("hello", request.Prompt);
		Assert.Null(request.Options);
		Assert.Null(request.KeepAlive);
	}

	#endregion

	#region OllamaLegacyEmbeddingsResponse

	/// <summary>
	/// Verifies that the legacy response serializes to exactly the single <c>embedding</c> key carrying its
	/// vector, the shape a pre-<c>/api/embed</c> client expects.
	/// </summary>
	[Fact]
	public void Serialize_LegacyEmbeddingsResponse_EmitsSingleEmbeddingField()
	{
		// Arrange
		OllamaLegacyEmbeddingsResponse response = new([0.1f, 0.2f, 0.3f]);

		// Act
		JsonObject json = JsonSerializer.SerializeToNode(response, Options)!.AsObject();

		// Assert
		Assert.Equal(["embedding"], json.Select(property => property.Key));
		Assert.Equal([0.1f, 0.2f, 0.3f], json["embedding"]!.AsArray().Select(n => (float)n!));
	}

	#endregion
}
