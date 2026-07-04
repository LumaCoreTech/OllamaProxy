// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Endpoints;

/// <summary>
/// Aggregates the individual endpoint groups into a single mapping call so the application startup
/// reads as one intent ("expose the Ollama surface") rather than a list of route registrations. Each
/// group remains independently mappable and testable; this extension only composes them in a fixed,
/// readable order.
/// </summary>
static class OllamaEndpointRouteBuilderExtensions
{
	/// <summary>
	/// Maps the complete Ollama-compatible API surface: chat, generate, model listing/inspection,
	/// embeddings, and the system/health endpoints, plus the inbound OpenAI-compatible <c>/v1</c>
	/// surface that real Ollama also exposes, so OpenAI-native clients connect unchanged.
	/// </summary>
	/// <param name="endpoints">The route builder the endpoints are added to.</param>
	/// <returns>The same <paramref name="endpoints"/> instance, to support call chaining.</returns>
	public static IEndpointRouteBuilder MapOllamaApi(this IEndpointRouteBuilder endpoints)
	{
		ArgumentNullException.ThrowIfNull(endpoints);

		endpoints.MapChatEndpoints();
		endpoints.MapGenerateEndpoints();
		endpoints.MapEmbeddingEndpoints();
		endpoints.MapModelEndpoints();
		endpoints.MapSystemEndpoints();
		endpoints.MapOpenAiApi();

		return endpoints;
	}
}
