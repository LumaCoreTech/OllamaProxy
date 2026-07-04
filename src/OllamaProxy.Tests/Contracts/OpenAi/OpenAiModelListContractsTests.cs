// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json;
using System.Text.Json.Nodes;

using OllamaProxy.Contracts.OpenAi;

namespace OllamaProxy.Tests.Contracts.OpenAi;

/// <summary>
/// Tests that pin the serialized shape of the inbound <c>GET /v1/models</c> contract
/// (<see cref="OpenAiModelListResponse"/> and <see cref="OpenAiModelListEntry"/>). The proxy is an
/// Ollama proxy that also speaks OpenAI, so this surface is the <em>standard</em> OpenAI model object —
/// nothing more. Each test asserts the <em>complete</em> key set rather than the presence of one field,
/// so it fails if <em>any</em> unexpected field ever leaks onto this client-facing surface. This is the
/// single authoritative guard against field creep here, which is why "field X is absent" assertions are
/// deliberately not scattered elsewhere.
/// </summary>
[Trait("Category", "Unit")]
public sealed class OpenAiModelListContractsTests
{
	// Mirrors how the minimal-API endpoint serializes the response. Every contract property carries an
	// explicit [JsonPropertyName], so the emitted keys are fully determined by the contract regardless of
	// the naming policy these options apply.
	private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

	#region OpenAiModelListEntry

	/// <summary>
	/// Verifies that a serialized <see cref="OpenAiModelListEntry"/> carries exactly the four standard
	/// OpenAI model-object fields (<c>id</c>, <c>created</c>, <c>owned_by</c>, <c>object</c>) and no
	/// proxy-specific extension — in particular, none of the upstream context-window fields this surface
	/// intentionally does not expose.
	/// </summary>
	[Fact]
	public void Serialize_ModelListEntry_EmitsExactlyTheStandardOpenAiFields()
	{
		// Arrange
		OpenAiModelListEntry entry = new("gpt-4o", Created: 1_700_000_000, OwnedBy: "cloud");

		// Act
		JsonObject json = JsonSerializer.SerializeToNode(entry, Options)!.AsObject();

		// Assert: the complete key set is the standard model object — nothing unexpected leaks through.
		Assert.Equal(["id", "created", "owned_by", "object"], json.Select(property => property.Key));
		Assert.Equal("gpt-4o", (string)json["id"]!);
		Assert.Equal(1_700_000_000, (long)json["created"]!);
		Assert.Equal("cloud", (string)json["owned_by"]!);
		Assert.Equal("model", (string)json["object"]!);
	}

	#endregion

	#region OpenAiModelListResponse

	/// <summary>
	/// Verifies that a serialized <see cref="OpenAiModelListResponse"/> carries exactly the OpenAI list
	/// envelope fields (<c>data</c>, <c>object</c>) and no proxy-specific extension, with the discriminator
	/// fixed to <c>list</c>.
	/// </summary>
	[Fact]
	public void Serialize_ModelListResponse_EmitsExactlyTheListEnvelopeFields()
	{
		// Arrange
		OpenAiModelListResponse response = new([new OpenAiModelListEntry("gpt-4o", 1_700_000_000, "cloud")]);

		// Act
		JsonObject json = JsonSerializer.SerializeToNode(response, Options)!.AsObject();

		// Assert: the envelope is exactly the OpenAI list shape carrying the single entry.
		Assert.Equal(["data", "object"], json.Select(property => property.Key));
		Assert.Equal("list", (string)json["object"]!);
		Assert.Single(json["data"]!.AsArray());
	}

	#endregion
}
