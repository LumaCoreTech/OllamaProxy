// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json.Serialization;

namespace OllamaProxy.Contracts.Ollama;

/// <summary>
/// The error body the Ollama API returns for a failed request: a single <c>error</c> string. The
/// proxy emits this shape (with an English message) for routing failures (unknown model), upstream
/// provider failures, and malformed requests, so Ollama-aware clients surface a meaningful message
/// rather than an opaque status code.
/// </summary>
/// <param name="Error">The human-readable error message.</param>
sealed record OllamaErrorResponse([property: JsonPropertyName("error")] string Error);
