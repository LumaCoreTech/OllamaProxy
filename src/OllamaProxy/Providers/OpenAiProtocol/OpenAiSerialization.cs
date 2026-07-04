// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json;
using System.Text.Json.Serialization;

namespace OllamaProxy.Providers.OpenAiProtocol;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> for OpenAI wire (de)serialization. A single cached,
/// read-only instance is reused across all requests: the options are immutable after construction and
/// <see cref="JsonSerializer"/> is thread-safe, so sharing avoids the per-call allocation and the
/// metadata-cache warm-up that a fresh options object would incur.
/// </summary>
static class OpenAiSerialization
{
	/// <summary>
	/// Gets the serializer options used for all OpenAI request and response payloads. Null values are
	/// omitted so optional fields the proxy did not set are not sent upstream, and unknown response
	/// members are ignored for forward compatibility with provider extensions.
	/// </summary>
	public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};
}
