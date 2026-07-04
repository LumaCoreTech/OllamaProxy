// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Providers.Abstractions;

/// <summary>
/// A provider-neutral description of a single model returned by a backend during discovery. The
/// adapter projects each provider's native listing shape onto this type so the core never depends on
/// provider-specific contracts. Each adapter is responsible for how it determines capabilities: a
/// metadata-rich provider (e.g. OpenRouter, Venice) maps them directly from its native listing into
/// <see cref="Capabilities"/> during projection, whereas a metadata-poor provider (e.g. strict OpenAI,
/// vLLM) leaves <see cref="Capabilities"/> <see langword="null"/> so the adapter falls back to active
/// probing.
/// </summary>
/// <param name="Id">The upstream model identifier as the backend knows it.</param>
/// <param name="Created">
/// The UTC timestamp when the backend listed this model (provider-native timestamp converted to
/// <see cref="DateTimeOffset"/> at projection time), or <see langword="null"/> when the backend reported
/// no creation time. This is the backend's listing date, not necessarily the model's original release date.
/// It flows through discovery to enrich the admin surface.
/// </param>
/// <param name="Capabilities">
/// The capabilities the adapter derived directly from the backend's native model listing, or
/// <see langword="null"/> when the listing carries no capability signal. A non-<see langword="null"/>
/// value is authoritative and bypasses probing; <see langword="null"/> defers capability resolution to
/// the adapter's active probing fallback.
/// </param>
/// <param name="ContextLength">
/// The maximum context window (in tokens) the backend will serve for this model, when reported (for
/// example vLLM's <c>max_model_len</c>). Surfaced to clients so they can size their requests and used
/// to enforce the proxy's context guardrail. <see langword="null"/> when the backend advertises no
/// context length, in which case an operator-supplied configuration value is required.
/// </param>
/// <param name="Metadata">
/// Optional descriptive metadata (display name, description, tokenizer, quantization, pricing, …) the backend
/// published about the model, or <see langword="null"/> when it reported none. Never affects routing or
/// capability resolution; it flows through discovery to enrich the admin surface so the operator sees the best
/// picture each backend offers rather than a bare id.
/// </param>
sealed record DiscoveredModel(
	string                 Id,
	DateTimeOffset?        Created       = null,
	ModelCapabilities?     Capabilities  = null,
	long?                  ContextLength = null,
	ProviderModelMetadata? Metadata      = null);
