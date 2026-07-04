// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json.Serialization;

namespace OllamaProxy.Providers.Vllm.Contracts;

/// <summary>
/// A single model entry from a vLLM <c>GET /v1/models</c> response. Beyond the standard OpenAI identity
/// fields, vLLM advertises the maximum context window it will serve under <c>max_model_len</c>, which
/// this contract captures so discovery can surface it to clients and the context guardrail. vLLM
/// reports no capability metadata, so none is modeled here.
/// </summary>
/// <param name="Id">The upstream model identifier.</param>
/// <param name="Created">The Unix timestamp (seconds) the model was created, when reported.</param>
/// <param name="MaxModelLen">
/// The maximum context window (in tokens) vLLM will serve for this model, reported as
/// <c>max_model_len</c>; <see langword="null"/> when not advertised.
/// </param>
sealed record VllmDiscoveryModel(
	[property: JsonPropertyName("id")]            string Id,
	[property: JsonPropertyName("created")]       long?  Created     = null,
	[property: JsonPropertyName("max_model_len")] long?  MaxModelLen = null);
