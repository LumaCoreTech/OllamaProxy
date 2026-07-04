// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Contracts.Ollama;
using OllamaProxy.Core;
using OllamaProxy.Endpoints;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Tests.Endpoints;

/// <summary>
/// Tests for <see cref="ModelProjection"/>, which renders a <see cref="RegisteredModel"/> into the
/// Ollama <c>/api/tags</c> and <c>/api/show</c> shapes. Organized by member:
/// <list type="number">
///     <item>
///         <description><see cref="ModelProjection.ToModelEntry"/> — tags entry + null guard.</description>
///     </item>
///     <item>
///         <description><see cref="ModelProjection.ToShowResponse"/> — capability list, model_info, null guard.</description>
///     </item>
/// </list>
/// </summary>
[Trait("Category", "Unit")]
public sealed class ModelProjectionTests
{
	private static RegisteredModel ModelWith(ModelCapabilities capabilities) => new(
		"gpt-4o",
		"cloud",
		"openai/gpt-4o",
		capabilities,
		ContextLength: 8192);

	#region ToModelEntry()

	/// <summary>
	/// Verifies that <see cref="ModelProjection.ToModelEntry"/> projects the model name, the supplied
	/// timestamp, and the synthesized detail placeholders into the tags entry.
	/// </summary>
	[Fact]
	public void ToModelEntry_WhenGivenModel_ProjectsNameTimestampAndDetails()
	{
		// Arrange
		RegisteredModel model = ModelWith(ModelCapabilities.CompletionOnly);

		// Act
		OllamaModelEntry entry = ModelProjection.ToModelEntry(model, "2026-05-31T00:00:00.0000000Z");

		// Assert
		Assert.Equal("gpt-4o", entry.Name);
		Assert.Equal("gpt-4o", entry.Model);
		Assert.Equal("2026-05-31T00:00:00.0000000Z", entry.ModifiedAt);
		Assert.Equal(0, entry.Size);
		Assert.Equal("gguf", entry.Details.Format);
		Assert.Equal("openai", entry.Details.Family);
	}

	/// <summary>
	/// Verifies that <see cref="ModelProjection.ToModelEntry"/> rejects a <see langword="null"/> model.
	/// </summary>
	[Fact]
	public void ToModelEntry_WhenModelIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception =
			Assert.Throws<ArgumentNullException>(() => ModelProjection.ToModelEntry(null!, "ts"));
		Assert.Equal("model", exception.ParamName);
	}

	#endregion

	#region ToShowResponse()

	/// <summary>
	/// Cases pairing capability flags with the exact ordered Ollama capability token list. Flags are
	/// passed as primitives (not the internal <see cref="ModelCapabilities"/>) so the theory signature
	/// stays public as xUnit requires.
	/// </summary>
	public static TheoryData<string, bool, bool, bool, bool, string[]> CapabilityListCases => new()
	{
		// Completion only → just "completion".
		{ "completion only", true, false, false, false, ["completion"] },

		// Tools added after completion in declaration order.
		{ "completion + tools", true, true, false, false, ["completion", "tools"] },

		// All chat capabilities present, in the fixed completion→tools→vision order.
		{ "completion + tools + vision", true, true, true, false, ["completion", "tools", "vision"] },

		// Embeddings-only model: no completion token, only "embedding".
		{ "embedding only", false, false, false, true, ["embedding"] }
	};

	/// <summary>
	/// Verifies that <see cref="ModelProjection.ToShowResponse"/> builds the exact ordered capability
	/// token list from the model's resolved capabilities.
	/// </summary>
	/// <param name="scenario">A human-readable label for the case.</param>
	/// <param name="completion">The completion flag to project.</param>
	/// <param name="tools">The tool-calling flag to project.</param>
	/// <param name="vision">The vision flag to project.</param>
	/// <param name="embeddings">The embedding flag to project.</param>
	/// <param name="expectedTokens">The exact ordered capability tokens expected.</param>
	[Theory]
	[MemberData(nameof(CapabilityListCases))]
	public void ToShowResponse_WhenGivenCapabilities_BuildsOrderedCapabilityList(
		string   scenario,
		bool     completion,
		bool     tools,
		bool     vision,
		bool     embeddings,
		string[] expectedTokens)
	{
		_ = scenario;

		// Arrange
		RegisteredModel model = ModelWith(
			new ModelCapabilities(completion, tools, vision, embeddings, CapabilitySource.Default));

		// Act
		OllamaShowResponse response = ModelProjection.ToShowResponse(model);

		// Assert
		Assert.Equal(expectedTokens, response.Capabilities);
	}

	/// <summary>
	/// Verifies that <see cref="ModelProjection.ToShowResponse"/> exposes the backend, upstream model,
	/// and capability provenance under <c>model_info</c> for operator diagnostics.
	/// </summary>
	[Fact]
	public void ToShowResponse_WhenGivenModel_ExposesProvenanceInModelInfo()
	{
		// Arrange
		RegisteredModel model = ModelWith(
			new ModelCapabilities(true, true, false, false, CapabilitySource.ProviderMetadata));

		// Act
		OllamaShowResponse response = ModelProjection.ToShowResponse(model);

		// Assert
		Assert.Equal("openai", response.ModelInfo["general.architecture"]);
		Assert.Equal(8192L, response.ModelInfo["openai.context_length"]);
		Assert.Equal("cloud", response.ModelInfo["ollamaproxy.backend"]);
		Assert.Equal("openai/gpt-4o", response.ModelInfo["ollamaproxy.upstream_model"]);
		Assert.Equal("ProviderMetadata", response.ModelInfo["ollamaproxy.capability_source"]);
	}

	/// <summary>
	/// Verifies that <see cref="ModelProjection.ToShowResponse"/> rejects a <see langword="null"/> model.
	/// </summary>
	[Fact]
	public void ToShowResponse_WhenModelIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception =
			Assert.Throws<ArgumentNullException>(() => ModelProjection.ToShowResponse(null!));
		Assert.Equal("model", exception.ParamName);
	}

	#endregion
}
