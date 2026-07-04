// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json.Serialization;

namespace OllamaProxy.Contracts.OpenAi;

/// <summary>
/// Response body the proxy returns from the inbound OpenAI <c>GET /v1/models</c> endpoint.
/// It mirrors the OpenAI list envelope (<c>{ "object": "list", "data": [ ... ] }</c>) so OpenAI-native clients
/// parse the model catalog unchanged (for example the OpenAI SDK used by GitHub Copilot).
/// </summary>
/// <param name="Data">The catalog entries, one per client-facing model.</param>
/// <param name="Object">The list discriminator; always <c>list</c>.</param>
sealed record OpenAiModelListResponse(
	[property: JsonPropertyName("data")]   IReadOnlyList<OpenAiModelListEntry> Data,
	[property: JsonPropertyName("object")] string                              Object = "list");

/// <summary>
/// A single entry in the inbound <c>GET /v1/models</c> response. It is the standard OpenAI model
/// object (<c>id</c>, <c>created</c>, <c>owned_by</c>, and the <c>object</c> discriminator) and
/// carries no proxy-specific extensions, so OpenAI-native clients (for example the OpenAI SDK used by
/// GitHub Copilot) parse it unchanged. Per-model details outside the OpenAI schema (such as the context
/// window) are surfaced on the native <c>/api/show</c> surface instead, where Ollama clients read them,
/// rather than scattered here as custom fields no standard OpenAI client looks for.
/// </summary>
/// <param name="Id">The model identifier exposed to clients.</param>
/// <param name="Created">The Unix timestamp (seconds) reported as the model's creation time.</param>
/// <param name="OwnedBy">The owning backend, surfaced for operator transparency.</param>
/// <param name="Object">The object discriminator; always <c>model</c>.</param>
sealed record OpenAiModelListEntry(
	[property: JsonPropertyName("id")]       string Id,
	[property: JsonPropertyName("created")]  long   Created,
	[property: JsonPropertyName("owned_by")] string OwnedBy,
	[property: JsonPropertyName("object")]   string Object = "model");
