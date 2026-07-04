// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Providers.Abstractions;

/// <summary>
/// Optional, provider-neutral descriptive metadata a backend may publish about a model alongside its
/// capabilities and context window: a display name, a human description, the tokenizer family, the
/// quantization, an upper bound on generated tokens, a source link, and per-million-token pricing. None of
/// these are needed to <em>route</em> a request; they exist purely to enrich the admin surface so the
/// operator sees the best picture each backend actually offers (e.g. OpenRouter's <c>name</c>/<c>pricing</c>
/// or Venice's <c>model_spec</c>), rather than a bare id. Each field is independently optional: a metadata-poor
/// backend (strict OpenAI, vLLM) leaves the whole record <see langword="null"/>, and a metadata-rich backend
/// populates only the fields it reports, so the admin surface shows what is genuinely known and nothing more.
/// </summary>
/// <param name="DisplayName">
/// The backend's human-facing model name (e.g. OpenRouter's <c>name</c>, Venice's <c>model_spec.name</c>),
/// or <see langword="null"/> when not reported. Distinct from the upstream id used for routing.
/// </param>
/// <param name="Description">A short human description of the model, or <see langword="null"/> when not reported.</param>
/// <param name="Tokenizer">
/// The tokenizer family the model uses (e.g. OpenRouter's <c>architecture.tokenizer</c>), or
/// <see langword="null"/> when not reported.
/// </param>
/// <param name="Quantization">
/// The weight quantization the backend serves (e.g. Venice's <c>capabilities.quantization</c> such as
/// <c>fp16</c>/<c>fp8</c>), or <see langword="null"/> when not reported. This is the one field that maps onto
/// Ollama's own <c>quantization_level</c> notion, so it is the only GGUF-shaped field a backend can genuinely fill.
/// </param>
/// <param name="MaxCompletionTokens">
/// The maximum number of tokens the backend will generate in a single response, when reported (OpenRouter's
/// <c>top_provider.max_completion_tokens</c>, Venice's <c>maxCompletionTokens</c>); <see langword="null"/> otherwise.
/// </param>
/// <param name="SourceUrl">
/// A link to the model's source or card (e.g. Venice's <c>modelSource</c> Hugging Face URL), or
/// <see langword="null"/> when not reported.
/// </param>
/// <param name="PromptUsdPerMillionTokens">
/// The input/prompt price in USD per one million tokens, or <see langword="null"/> when not reported or not
/// parseable. Normalized across providers (see the type remarks).
/// </param>
/// <param name="CompletionUsdPerMillionTokens">
/// The output/completion price in USD per one million tokens, or <see langword="null"/> when not reported or not
/// parseable. Normalized across providers (see the type remarks).
/// </param>
/// <remarks>
/// Pricing is normalized to <em>USD per one million tokens</em> across providers so the admin surface can show
/// a single comparable figure regardless of the unit a backend reported (OpenRouter prices per single token,
/// Venice per million); the projecting adapter does the conversion. The record is compared by value like the
/// rest of the discovery types, so two models with identical metadata compare equal.
/// </remarks>
public sealed record ProviderModelMetadata(
	string?  DisplayName                   = null,
	string?  Description                   = null,
	string?  Tokenizer                     = null,
	string?  Quantization                  = null,
	long?    MaxCompletionTokens           = null,
	string?  SourceUrl                     = null,
	decimal? PromptUsdPerMillionTokens     = null,
	decimal? CompletionUsdPerMillionTokens = null)
{
	/// <summary>
	/// Indicates whether this record carries at least one populated field. The discovery projection uses it to
	/// collapse an all-empty record to <see langword="null"/>, so a backend that reported no descriptive metadata
	/// is represented as "no metadata" rather than an empty shell the admin surface would render as blank rows.
	/// </summary>
	public bool HasAny => DisplayName is not null
	                      || Description is not null
	                      || Tokenizer is not null
	                      || Quantization is not null
	                      || MaxCompletionTokens is not null
	                      || SourceUrl is not null
	                      || PromptUsdPerMillionTokens is not null
	                      || CompletionUsdPerMillionTokens is not null;
}
