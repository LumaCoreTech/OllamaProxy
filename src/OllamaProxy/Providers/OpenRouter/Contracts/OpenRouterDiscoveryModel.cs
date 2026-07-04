// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json.Serialization;

namespace OllamaProxy.Providers.OpenRouter.Contracts;

/// <summary>
/// A single model entry from an OpenRouter <c>GET /v1/models</c> response. OpenRouter annotates each
/// model with rich, structured metadata that maps directly onto the neutral discovered model: a
/// top-level context window, the underlying provider's context window, an architecture block of
/// input/output modalities, and the list of accepted request parameters. Capability detection consumes
/// these fields natively without any provider-specific reconstruction.
/// </summary>
/// <param name="Id">The upstream model identifier.</param>
/// <param name="Created">The Unix timestamp (seconds) the model was created, when reported.</param>
/// <param name="Name">The human-facing model name OpenRouter displays, when reported.</param>
/// <param name="Description">A short human description of the model, when reported.</param>
/// <param name="ContextLength">The maximum context window (in tokens), when reported at the top level.</param>
/// <param name="Architecture">The input/output modality metadata, when reported.</param>
/// <param name="TopProvider">The underlying provider metadata, carrying a fallback context window.</param>
/// <param name="Pricing">The per-token pricing in USD, when reported.</param>
/// <param name="SupportedParameters">
/// The request parameters the model accepts (e.g. <c>tools</c>), used to infer tool support.
/// </param>
sealed record OpenRouterDiscoveryModel(
	[property: JsonPropertyName("id")]                   string                  Id,
	[property: JsonPropertyName("created")]              long?                   Created             = null,
	[property: JsonPropertyName("name")]                 string?                 Name                = null,
	[property: JsonPropertyName("description")]          string?                 Description         = null,
	[property: JsonPropertyName("context_length")]       long?                   ContextLength       = null,
	[property: JsonPropertyName("architecture")]         OpenRouterArchitecture? Architecture        = null,
	[property: JsonPropertyName("top_provider")]         OpenRouterTopProvider?  TopProvider         = null,
	[property: JsonPropertyName("pricing")]              OpenRouterPricing?      Pricing             = null,
	[property: JsonPropertyName("supported_parameters")] IReadOnlyList<string>?  SupportedParameters = null);

/// <summary>
/// The <c>architecture</c> block of an OpenRouter model entry, describing which modalities the model
/// accepts as input and emits as output. Output modalities let the shared completion guard withhold
/// completion from generation-only (e.g. image) models.
/// </summary>
/// <param name="InputModalities">The modalities the model accepts as input (e.g. <c>text</c>, <c>image</c>).</param>
/// <param name="OutputModalities">The modalities the model emits as output (e.g. <c>text</c>, <c>image</c>).</param>
/// <param name="Tokenizer">The tokenizer family the model uses (e.g. <c>GPT</c>, <c>Llama3</c>), when reported.</param>
sealed record OpenRouterArchitecture(
	[property: JsonPropertyName("input_modalities")]  IReadOnlyList<string>? InputModalities  = null,
	[property: JsonPropertyName("output_modalities")] IReadOnlyList<string>? OutputModalities = null,
	[property: JsonPropertyName("tokenizer")]         string?                Tokenizer        = null);

/// <summary>
/// The <c>top_provider</c> block of an OpenRouter model entry, carrying the underlying provider's
/// context window. It serves as the fallback when the top-level <c>context_length</c> is absent.
/// </summary>
/// <param name="ContextLength">The underlying provider's maximum context window (in tokens), when reported.</param>
/// <param name="MaxCompletionTokens">
/// The maximum number of tokens the underlying provider will generate in one response, when reported.
/// </param>
sealed record OpenRouterTopProvider(
	[property: JsonPropertyName("context_length")]        long? ContextLength       = null,
	[property: JsonPropertyName("max_completion_tokens")] long? MaxCompletionTokens = null);

/// <summary>
/// The <c>pricing</c> block of an OpenRouter model entry. OpenRouter reports prices as strings in USD per
/// <em>single</em> token (e.g. <c>"0.0000004"</c>), which the adapter parses and scales to a per-million-token
/// figure for the neutral metadata. Fields stay strings here so a malformed or absent value degrades to "no
/// price" rather than failing the whole listing deserialization.
/// </summary>
/// <param name="Prompt">The input price in USD per single token, as a string, when reported.</param>
/// <param name="Completion">The output price in USD per single token, as a string, when reported.</param>
sealed record OpenRouterPricing(
	[property: JsonPropertyName("prompt")]     string? Prompt     = null,
	[property: JsonPropertyName("completion")] string? Completion = null);
