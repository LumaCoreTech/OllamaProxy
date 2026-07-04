// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json.Serialization;

namespace OllamaProxy.Providers.Venice.Contracts;

/// <summary>
/// A single model entry from a Venice <c>GET /v1/models</c> response. Venice does not report neutral
/// modality lists; instead it exposes a top-level <c>type</c> discriminator and a structured
/// <c>model_spec</c> block of capability flags. <see cref="VeniceProvider"/> translates these into the
/// neutral modality/parameter metadata the shared capability detection understands.
/// </summary>
/// <param name="Id">The upstream model identifier.</param>
/// <param name="Created">The Unix timestamp (seconds) the model was created, when reported.</param>
/// <param name="Type">
/// The Venice model type discriminator (<c>text</c> for language models, <c>image</c> for
/// generation-only models), translated into output modalities; <see langword="null"/> when not reported.
/// </param>
/// <param name="ModelSpec">The structured capability/context specification, when reported.</param>
sealed record VeniceDiscoveryModel(
	[property: JsonPropertyName("id")]         string           Id,
	[property: JsonPropertyName("created")]    long?            Created   = null,
	[property: JsonPropertyName("type")]       string?          Type      = null,
	[property: JsonPropertyName("model_spec")] VeniceModelSpec? ModelSpec = null);

/// <summary>
/// The <c>model_spec</c> block of a Venice model entry, carrying the available context window and a
/// nested capability map. The context window feeds the neutral context length, while the capabilities
/// are translated into neutral input modalities and supported parameters.
/// </summary>
/// <param name="AvailableContextTokens">
/// The maximum context window (in tokens) Venice serves for this model, when reported.
/// </param>
/// <param name="Name">The human-facing model name Venice displays, when reported.</param>
/// <param name="Description">A short human description of the model, when reported.</param>
/// <param name="ModelSource">A link to the model's source/card (typically Hugging Face), when reported.</param>
/// <param name="MaxCompletionTokens">
/// The maximum number of tokens Venice will generate in one response, when reported.
/// </param>
/// <param name="Capabilities">The structured capability flags (vision, function calling), when reported.</param>
/// <param name="Pricing">The per-million-token pricing in USD, when reported.</param>
sealed record VeniceModelSpec(
	[property: JsonPropertyName("availableContextTokens")] long?                        AvailableContextTokens = null,
	[property: JsonPropertyName("name")]                   string?                      Name                   = null,
	[property: JsonPropertyName("description")]            string?                      Description            = null,
	[property: JsonPropertyName("modelSource")]            string?                      ModelSource            = null,
	[property: JsonPropertyName("maxCompletionTokens")]    long?                        MaxCompletionTokens    = null,
	[property: JsonPropertyName("capabilities")]           VeniceModelSpecCapabilities? Capabilities           = null,
	[property: JsonPropertyName("pricing")]                VenicePricing?               Pricing                = null);

/// <summary>
/// The <c>capabilities</c> map nested in a Venice <c>model_spec</c>. Each flag is translated into the
/// neutral metadata the shared capability detection consumes: vision becomes an <c>image</c> input
/// modality, and function calling becomes a <c>tools</c> supported parameter.
/// </summary>
/// <param name="SupportsVision">
/// Whether the model accepts image input; translated into an <c>image</c> input modality.
/// </param>
/// <param name="SupportsFunctionCalling">
/// Whether the model supports function/tool calling; translated into a <c>tools</c> supported parameter.
/// </param>
/// <param name="Quantization">
/// The weight quantization Venice serves the model at (e.g. <c>fp16</c>, <c>fp8</c>), when reported. This is the
/// one capability field that maps onto Ollama's own <c>quantization_level</c> notion.
/// </param>
sealed record VeniceModelSpecCapabilities(
	[property: JsonPropertyName("supportsVision")]          bool?   SupportsVision          = null,
	[property: JsonPropertyName("supportsFunctionCalling")] bool?   SupportsFunctionCalling = null,
	[property: JsonPropertyName("quantization")]            string? Quantization            = null);

/// <summary>
/// The <c>pricing</c> block of a Venice <c>model_spec</c>. Venice reports per-million-token prices as nested
/// objects carrying a USD figure (and a Diem figure the proxy ignores), so the neutral metadata adopts the USD
/// values directly without scaling.
/// </summary>
/// <param name="Input">The input/prompt price per one million tokens, when reported.</param>
/// <param name="Output">The output/completion price per one million tokens, when reported.</param>
sealed record VenicePricing(
	[property: JsonPropertyName("input")]  VenicePriceAmount? Input  = null,
	[property: JsonPropertyName("output")] VenicePriceAmount? Output = null);

/// <summary>
/// A single Venice price amount, carrying the USD figure the proxy surfaces (the parallel Diem figure is
/// vendor currency and deliberately ignored).
/// </summary>
/// <param name="Usd">The price in USD per one million tokens, when reported.</param>
sealed record VenicePriceAmount([property: JsonPropertyName("usd")] decimal? Usd = null);
