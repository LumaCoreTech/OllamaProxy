// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json;
using System.Text.Json.Nodes;

using OllamaProxy.Contracts.Ollama;

namespace OllamaProxy.Tests.Contracts.Ollama;

/// <summary>
/// Tests that pin the serialized shape of the Ollama model-discovery contracts the proxy emits:
/// <c>GET /api/tags</c> (<see cref="OllamaTagsResponse"/>, <see cref="OllamaModelEntry"/>,
/// <see cref="OllamaModelDetails"/>), <c>POST /api/show</c> (<see cref="OllamaShowRequest"/>,
/// <see cref="OllamaShowResponse"/>), <c>GET /api/version</c> (<see cref="OllamaVersionResponse"/>), and
/// <c>GET /api/ps</c> (<see cref="OllamaPsResponse"/>, <see cref="OllamaPsModel"/>). These are the surfaces
/// tool-aware clients (e.g. GitHub Copilot's model picker and its capability probe) read, so each test asserts
/// the exact emitted key set to guard against field creep or drift, using the production
/// <see cref="OllamaJson.Options"/>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class OllamaModelContractsTests
{
	private static readonly JsonSerializerOptions Options = OllamaJson.Options;

	/// <summary>
	/// Builds representative model metadata reused across the tags/show/ps assertions. Every field carries a
	/// distinct, recognizable value so a swapped or mis-keyed property surfaces as a value mismatch, not just a
	/// missing key.
	/// </summary>
	/// <returns>A fully populated <see cref="OllamaModelDetails"/>.</returns>
	private static OllamaModelDetails SampleDetails() => new(
		ParentModel: "parent-x",
		Format: "gguf",
		Family: "llama",
		Families: ["llama", "llama-vision"],
		ParameterSize: "7B",
		QuantizationLevel: "Q4_0");

	/// <summary>
	/// Asserts that a serialized <see cref="OllamaModelDetails"/> object carries every metadata field, each under
	/// its own key with the value <see cref="SampleDetails"/> supplied. Shared by the details, tags, show, and ps
	/// tests so the nested-details contract is verified identically wherever it appears.
	/// </summary>
	/// <param name="detailsJson">The serialized details object to inspect.</param>
	private static void AssertSampleDetails(JsonObject detailsJson)
	{
		Assert.Equal(
			["parent_model", "format", "family", "families", "parameter_size", "quantization_level"],
			detailsJson.Select(property => property.Key));
		Assert.Equal("parent-x", (string)detailsJson["parent_model"]!);
		Assert.Equal("gguf", (string)detailsJson["format"]!);
		Assert.Equal("llama", (string)detailsJson["family"]!);
		Assert.Equal(["llama", "llama-vision"], detailsJson["families"]!.AsArray().Select(node => (string)node!));
		Assert.Equal("7B", (string)detailsJson["parameter_size"]!);
		Assert.Equal("Q4_0", (string)detailsJson["quantization_level"]!);
	}

	#region OllamaModelDetails

	/// <summary>
	/// Verifies that model details serialize to the complete metadata key set shared by <c>/api/tags</c> and
	/// <c>/api/show</c>, since clients read these synthesized fields to describe a model.
	/// </summary>
	[Fact]
	public void Serialize_ModelDetails_EmitsEveryMetadataField()
	{
		// Act
		JsonObject json = JsonSerializer.SerializeToNode(SampleDetails(), Options)!.AsObject();

		// Assert: every field is verified under its own key by the shared helper.
		AssertSampleDetails(json);
	}

	#endregion

	#region OllamaTagsResponse

	/// <summary>
	/// Verifies that a <c>/api/tags</c> response serializes to the <c>models</c> envelope whose entries carry the
	/// full model-entry key set, the shape a client's model picker enumerates.
	/// </summary>
	[Fact]
	public void Serialize_TagsResponse_EmitsModelsEnvelopeWithFullEntries()
	{
		// Arrange: Name and Model differ so a swapped mapping between them is caught, not masked by equal values.
		OllamaModelEntry entry = new(
			Name: "gpt-4o",
			Model: "gpt-4o:latest",
			ModifiedAt: "2026-07-10T00:00:00Z",
			Size: 1_234,
			Digest: "sha256:abc",
			Details: SampleDetails());
		OllamaTagsResponse response = new([entry]);

		// Act
		JsonObject json = JsonSerializer.SerializeToNode(response, Options)!.AsObject();

		// Assert
		Assert.Equal(["models"], json.Select(property => property.Key));
		JsonObject entryJson = json["models"]!.AsArray().Single()!.AsObject();
		Assert.Equal(
			["name", "model", "modified_at", "size", "digest", "details"],
			entryJson.Select(property => property.Key));
		Assert.Equal("gpt-4o", (string)entryJson["name"]!);
		Assert.Equal("gpt-4o:latest", (string)entryJson["model"]!);
		Assert.Equal("2026-07-10T00:00:00Z", (string)entryJson["modified_at"]!);
		Assert.Equal(1_234, (long)entryJson["size"]!);
		Assert.Equal("sha256:abc", (string)entryJson["digest"]!);
		AssertSampleDetails(entryJson["details"]!.AsObject());
	}

	/// <summary>
	/// Verifies that an empty catalog still serializes to a present, empty <c>models</c> array rather than a null
	/// or missing field, which clients tolerate as "no models available."
	/// </summary>
	[Fact]
	public void Serialize_TagsResponse_WhenEmpty_EmitsEmptyModelsArray()
	{
		// Arrange
		OllamaTagsResponse response = new([]);

		// Act
		JsonObject json = JsonSerializer.SerializeToNode(response, Options)!.AsObject();

		// Assert
		Assert.Equal(["models"], json.Select(property => property.Key));
		Assert.Empty(json["models"]!.AsArray());
	}

	#endregion

	#region OllamaShowRequest / OllamaShowResponse

	/// <summary>
	/// Verifies that a <c>/api/show</c> request binds its single required <c>model</c> field from wire JSON.
	/// </summary>
	[Fact]
	public void Deserialize_ShowRequest_BindsModel()
	{
		// Arrange
		const string json = """{ "model": "gpt-4o" }""";

		// Act
		var request = JsonSerializer.Deserialize<OllamaShowRequest>(json, Options)!;

		// Assert
		Assert.Equal("gpt-4o", request.Model);
	}

	/// <summary>
	/// Verifies that a fully populated <c>/api/show</c> response serializes to the complete key set, with the
	/// decisive <c>capabilities</c> array preserved — the field tool-aware clients read to enable function
	/// calling.
	/// </summary>
	[Fact]
	public void Serialize_ShowResponse_WithAllFields_EmitsEveryField()
	{
		// Arrange
		OllamaShowResponse response = new(
			Details: SampleDetails(),
			ModelInfo: new Dictionary<string, object> { ["context_length"] = 128_000 },
			Capabilities: ["completion", "tools", "vision"],
			ModelFile: "FROM gpt-4o",
			Parameters: "stop \"</s>\"",
			Template: "{{ .Prompt }}");

		// Act
		JsonObject json = JsonSerializer.SerializeToNode(response, Options)!.AsObject();

		// Assert: every field under its own key, including the nested details and each optional compatibility string.
		Assert.Equal(
			["details", "model_info", "capabilities", "modelfile", "parameters", "template"],
			json.Select(property => property.Key));
		AssertSampleDetails(json["details"]!.AsObject());
		Assert.Equal(128_000, (int)json["model_info"]!.AsObject()["context_length"]!);
		Assert.Equal(["completion", "tools", "vision"], json["capabilities"]!.AsArray().Select(n => (string)n!));
		Assert.Equal("FROM gpt-4o", (string)json["modelfile"]!);
		Assert.Equal("stop \"</s>\"", (string)json["parameters"]!);
		Assert.Equal("{{ .Prompt }}", (string)json["template"]!);
	}

	/// <summary>
	/// Verifies that a <c>/api/show</c> response omits the null optional compatibility fields (<c>modelfile</c>,
	/// <c>parameters</c>, <c>template</c>), emitting only the always-present descriptive fields.
	/// </summary>
	[Fact]
	public void Serialize_ShowResponse_WithoutOptionalFields_OmitsNullFields()
	{
		// Arrange
		OllamaShowResponse response = new(
			Details: SampleDetails(),
			ModelInfo: new Dictionary<string, object>(),
			Capabilities: ["completion"]);

		// Act
		JsonObject json = JsonSerializer.SerializeToNode(response, Options)!.AsObject();

		// Assert
		Assert.Equal(["details", "model_info", "capabilities"], json.Select(property => property.Key));
	}

	#endregion

	#region OllamaVersionResponse

	/// <summary>
	/// Verifies that a <c>/api/version</c> response serializes to exactly the single <c>version</c> key clients
	/// read to detect an Ollama-compatible endpoint.
	/// </summary>
	[Fact]
	public void Serialize_VersionResponse_EmitsSingleVersionField()
	{
		// Arrange
		OllamaVersionResponse response = new("0.1.0");

		// Act
		JsonObject json = JsonSerializer.SerializeToNode(response, Options)!.AsObject();

		// Assert
		Assert.Equal(["version"], json.Select(property => property.Key));
		Assert.Equal("0.1.0", (string)json["version"]!);
	}

	#endregion

	#region OllamaPsResponse / OllamaPsModel

	/// <summary>
	/// Verifies that a <c>/api/ps</c> response serializes to the <c>models</c> envelope whose entries carry the
	/// full running-model key set, preserving contract completeness even though the proxy never populates it.
	/// </summary>
	[Fact]
	public void Serialize_PsResponse_EmitsModelsEnvelopeWithFullEntries()
	{
		// Arrange: distinct values per field (Name != Model, Size != SizeVram) so any swap surfaces as a mismatch.
		OllamaPsModel model = new(
			Name: "gpt-4o",
			Model: "gpt-4o:latest",
			Size: 1_234,
			Digest: "sha256:abc",
			Details: SampleDetails(),
			ExpiresAt: "2026-07-10T01:00:00Z",
			SizeVram: 512);
		OllamaPsResponse response = new([model]);

		// Act
		JsonObject json = JsonSerializer.SerializeToNode(response, Options)!.AsObject();

		// Assert
		Assert.Equal(["models"], json.Select(property => property.Key));
		JsonObject modelJson = json["models"]!.AsArray().Single()!.AsObject();
		Assert.Equal(
			["name", "model", "size", "digest", "details", "expires_at", "size_vram"],
			modelJson.Select(property => property.Key));
		Assert.Equal("gpt-4o", (string)modelJson["name"]!);
		Assert.Equal("gpt-4o:latest", (string)modelJson["model"]!);
		Assert.Equal(1_234, (long)modelJson["size"]!);
		Assert.Equal("sha256:abc", (string)modelJson["digest"]!);
		AssertSampleDetails(modelJson["details"]!.AsObject());
		Assert.Equal("2026-07-10T01:00:00Z", (string)modelJson["expires_at"]!);
		Assert.Equal(512, (long)modelJson["size_vram"]!);
	}

	/// <summary>
	/// Verifies that the proxy's always-empty running-model list serializes to a present, empty <c>models</c>
	/// array, which clients read as "nothing currently loaded."
	/// </summary>
	[Fact]
	public void Serialize_PsResponse_WhenEmpty_EmitsEmptyModelsArray()
	{
		// Arrange
		OllamaPsResponse response = new([]);

		// Act
		JsonObject json = JsonSerializer.SerializeToNode(response, Options)!.AsObject();

		// Assert
		Assert.Equal(["models"], json.Select(property => property.Key));
		Assert.Empty(json["models"]!.AsArray());
	}

	#endregion
}
