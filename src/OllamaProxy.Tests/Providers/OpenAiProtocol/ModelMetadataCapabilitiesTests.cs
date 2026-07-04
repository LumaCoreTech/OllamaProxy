// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Providers.Abstractions;
using OllamaProxy.Providers.OpenAiProtocol;

namespace OllamaProxy.Tests.Providers.OpenAiProtocol;

/// <summary>
/// Tests for <see cref="ModelMetadataCapabilities.FromMetadata"/>, the pure derivation that turns an
/// OpenAI-compatible backend's reported modality and parameter metadata into an authoritative
/// <see cref="ModelCapabilities"/> that bypasses probing. Every case asserts the full record (which also pins
/// <see cref="CapabilitySource.ProviderMetadata"/> and the always-false embeddings flag), so a regression in any
/// single flag surfaces. The theory walks each branch: the no-metadata null path, vision from an image input
/// modality, tools from a tools parameter, the output-modality completion logic (absent stays conservatively
/// true, text confirms, image-only denies), and case-insensitive matching.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ModelMetadataCapabilitiesTests
{
	/// <summary>
	/// Cases pairing reported metadata (input modalities, output modalities, supported parameters) with the
	/// expected derived capabilities. A <see langword="null"/> expectation marks the "no usable metadata" path
	/// where the derivation defers to probing.
	/// </summary>
	public static TheoryData<string, string[]?, string[]?, string[]?, ModelCapabilities?> MetadataCases => new()
	{
		// No metadata at all: the signal cannot conclude anything, so it defers to probing with null.
		{ "all metadata absent yields null", null, null, null, null },

		// Present-but-empty lists count as no metadata (the Count > 0 guard), so they defer to probing too.
		{ "all metadata empty yields null", [], [], [], null },

		// An image input modality infers vision; with no output modalities, completion stays conservatively on.
		{
			"image input infers vision",
			["image"], null, null,
			new ModelCapabilities(
				SupportsCompletion: true,
				SupportsTools: false,
				SupportsVision: true,
				SupportsEmbeddings: false,
				CapabilitySource.ProviderMetadata)
		},

		// Input metadata present but without "image": vision is off, and completion stays on (no output metadata).
		{
			"non-image input leaves vision off",
			["text"], null, null,
			new ModelCapabilities(
				SupportsCompletion: true,
				SupportsTools: false,
				SupportsVision: false,
				SupportsEmbeddings: false,
				CapabilitySource.ProviderMetadata)
		},

		// A tools parameter infers tool support; no input or output metadata leaves vision off, completion on.
		{
			"tools parameter infers tool support",
			null, null, ["tools"],
			new ModelCapabilities(
				SupportsCompletion: true,
				SupportsTools: true,
				SupportsVision: false,
				SupportsEmbeddings: false,
				CapabilitySource.ProviderMetadata)
		},

		// Parameter metadata present but without "tools": tool support is off (the no-match branch).
		{
			"non-tools parameter leaves tools off",
			null, null, ["temperature"],
			new ModelCapabilities(
				SupportsCompletion: true,
				SupportsTools: false,
				SupportsVision: false,
				SupportsEmbeddings: false,
				CapabilitySource.ProviderMetadata)
		},

		// A text output modality confirms completion authoritatively.
		{
			"text output confirms completion",
			null, ["text"], null,
			new ModelCapabilities(
				SupportsCompletion: true,
				SupportsTools: false,
				SupportsVision: false,
				SupportsEmbeddings: false,
				CapabilitySource.ProviderMetadata)
		},

		// A generation-only model producing solely image is honestly marked as not supporting completion.
		{
			"image-only output denies completion",
			null, ["image"], null,
			new ModelCapabilities(
				SupportsCompletion: false,
				SupportsTools: false,
				SupportsVision: false,
				SupportsEmbeddings: false,
				CapabilitySource.ProviderMetadata)
		},

		// Matching is case-insensitive, so an upper-case "IMAGE" input modality still infers vision.
		{
			"case-insensitive image matches vision",
			["IMAGE"], null, null,
			new ModelCapabilities(
				SupportsCompletion: true,
				SupportsTools: false,
				SupportsVision: true,
				SupportsEmbeddings: false,
				CapabilitySource.ProviderMetadata)
		},

		// A fully annotated multimodal, tool-capable model: every signal present, multi-entry lists exercised.
		{
			"full multimodal tool-capable model",
			["text", "image"], ["text"], ["temperature", "tools"],
			new ModelCapabilities(
				SupportsCompletion: true,
				SupportsTools: true,
				SupportsVision: true,
				SupportsEmbeddings: false,
				CapabilitySource.ProviderMetadata)
		}
	};

	/// <summary>
	/// Verifies that <see cref="ModelMetadataCapabilities.FromMetadata"/> derives the expected capability record
	/// (or <see langword="null"/> when no metadata is usable) for each reported metadata combination.
	/// </summary>
	/// <param name="scenario">A human-readable description of the metadata combination under test.</param>
	/// <param name="inputModalities">The reported input modalities, or <see langword="null"/> when not reported.</param>
	/// <param name="outputModalities">The reported output modalities, or <see langword="null"/> when not reported.</param>
	/// <param name="supportedParameters">The reported supported parameters, or <see langword="null"/> when not reported.</param>
	/// <param name="expected">The expected derived capabilities, or <see langword="null"/> when probing should take over.</param>
	[Theory]
	[MemberData(nameof(MetadataCases))]
	public void FromMetadata_WhenMetadataVaries_DerivesCapabilitiesOrDefersToProbing(
		string             scenario,
		string[]?          inputModalities,
		string[]?          outputModalities,
		string[]?          supportedParameters,
		ModelCapabilities? expected)
	{
		// Arrange
		_ = scenario;

		// Act
		ModelCapabilities? result =
			ModelMetadataCapabilities.FromMetadata(inputModalities, outputModalities, supportedParameters);

		// Assert: full-record equality pins every flag plus the ProviderMetadata source in one comparison.
		Assert.Equal(expected, result);
	}
}
