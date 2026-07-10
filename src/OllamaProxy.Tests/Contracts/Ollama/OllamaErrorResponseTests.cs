// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json;
using System.Text.Json.Nodes;

using OllamaProxy.Contracts.Ollama;

namespace OllamaProxy.Tests.Contracts.Ollama;

/// <summary>
/// Tests that pin the serialized shape of <see cref="OllamaErrorResponse"/>, the single-<c>error</c>-string body
/// the proxy emits for routing failures, upstream provider failures, and malformed requests. Ollama-aware
/// clients surface this message instead of an opaque status code, so the emitted key set is asserted exactly
/// using the production <see cref="OllamaJson.Options"/>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class OllamaErrorResponseTests
{
	private static readonly JsonSerializerOptions Options = OllamaJson.Options;

	/// <summary>
	/// Verifies that an error response serializes to exactly the single <c>error</c> key carrying the
	/// human-readable message, the shape Ollama clients parse to display a failure.
	/// </summary>
	[Fact]
	public void Serialize_ErrorResponse_EmitsSingleErrorField()
	{
		// Arrange
		OllamaErrorResponse response = new("model 'foo' not found");

		// Act
		JsonObject json = JsonSerializer.SerializeToNode(response, Options)!.AsObject();

		// Assert
		Assert.Equal(["error"], json.Select(property => property.Key));
		Assert.Equal("model 'foo' not found", (string)json["error"]!);
	}

	/// <summary>
	/// Verifies that the error body round-trips, so a value serialized by the proxy deserializes back to the same
	/// message — the contract a client relies on when reading the field.
	/// </summary>
	[Fact]
	public void RoundTrip_ErrorResponse_PreservesMessage()
	{
		// Arrange
		OllamaErrorResponse original = new("upstream provider unavailable");

		// Act
		string json = JsonSerializer.Serialize(original, Options);
		var roundTripped = JsonSerializer.Deserialize<OllamaErrorResponse>(json, Options)!;

		// Assert
		Assert.Equal(original.Error, roundTripped.Error);
	}
}
