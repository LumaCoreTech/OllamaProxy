// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json;
using System.Text.Json.Serialization;

namespace OllamaProxy.Contracts.Ollama;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> for serializing the proxy's Ollama-facing responses. A
/// single cached, read-only instance is reused for every response: the options are immutable after
/// construction and <see cref="JsonSerializer"/> is thread-safe, so sharing avoids per-call allocation
/// and metadata-cache warm-up. Null values are omitted so that the terminal-only timing and token
/// fields of a streamed <see cref="OllamaChatResponse"/> do not appear on the incremental chunks.
/// </summary>
static class OllamaJson
{
	/// <summary>
	/// Gets the serializer options used for all Ollama response payloads. Property names come from the
	/// explicit <see cref="JsonPropertyNameAttribute"/> annotations on the contracts, so no naming
	/// policy is applied; only null omission is configured.
	/// </summary>
	public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};
}
