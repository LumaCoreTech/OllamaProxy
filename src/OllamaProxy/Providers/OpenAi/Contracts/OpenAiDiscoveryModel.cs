// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json.Serialization;

namespace OllamaProxy.Providers.OpenAi.Contracts;

/// <summary>
/// A single model entry from the official OpenAI <c>GET /v1/models</c> endpoint. The OpenAI schema is
/// deliberately minimal (it exposes neither a context length nor any capability metadata), so this
/// contract captures only the stable identity fields. Backends that advertise richer metadata under
/// vendor-specific fields are served by their own specialized discovery contracts rather than by
/// widening this one.
/// </summary>
/// <param name="Id">The upstream model identifier.</param>
/// <param name="Created">The Unix timestamp (seconds) the model was created, when reported.</param>
/// <param name="OwnedBy">The owning organization, when reported.</param>
sealed record OpenAiDiscoveryModel(
	[property: JsonPropertyName("id")]       string  Id,
	[property: JsonPropertyName("created")]  long?   Created = null,
	[property: JsonPropertyName("owned_by")] string? OwnedBy = null);
