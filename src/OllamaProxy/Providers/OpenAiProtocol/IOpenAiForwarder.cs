// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json.Nodes;

using OllamaProxy.Configuration;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Providers.OpenAiProtocol;

/// <summary>
/// An optional capability a provider adapter exposes when its upstream speaks the OpenAI wire format
/// natively. It lets the inbound <c>/v1</c> endpoints forward an OpenAI request to the backend without
/// a lossy round-trip through the Ollama contracts: the request and response JSON are passed through
/// verbatim (only the caller rewrites the <c>model</c> field), preserving any provider extensions the
/// proxy's typed contracts do not model. Adapters whose upstream is not OpenAI-compatible simply do
/// not implement this interface, and the endpoints report a clear gateway error rather than guessing a
/// translation.
/// </summary>
interface IOpenAiForwarder
{
	/// <summary>
	/// Forwards a non-streaming OpenAI POST request to the backend and returns the parsed JSON
	/// response object verbatim.
	/// </summary>
	/// <param name="backend">The backend to route the call to.</param>
	/// <param name="path">The backend-relative request path (for example <c>chat/completions</c>).</param>
	/// <param name="body">The request body to forward, with its <c>model</c> field already rewritten.</param>
	/// <param name="pinnedEffort">
	/// The resolved model's pinned reasoning effort, or <see langword="null"/> when none is pinned. When set it
	/// is authoritative: it overrides any reasoning directive already present in <paramref name="body"/>, so a
	/// pinned model is crash-safe even when the client sends its own reasoning field on the <c>/v1</c> surface.
	/// </param>
	/// <param name="cancellationToken">A token that may be used to cancel the operation.</param>
	/// <returns>The upstream JSON response object.</returns>
	Task<JsonObject> ForwardJsonAsync(
		BackendContext    backend,
		string            path,
		JsonObject        body,
		ReasoningEffort?  pinnedEffort,
		CancellationToken cancellationToken);

	/// <summary>
	/// Forwards a streaming OpenAI POST request to the backend and yields the raw JSON payload of each
	/// Server-Sent-Events <c>data:</c> frame in stream order, excluding the terminating sentinel.
	/// </summary>
	/// <param name="backend">The backend to route the call to.</param>
	/// <param name="path">The backend-relative request path (for example <c>chat/completions</c>).</param>
	/// <param name="body">The request body to forward, with its <c>model</c> field already rewritten.</param>
	/// <param name="pinnedEffort">
	/// The resolved model's pinned reasoning effort, or <see langword="null"/> when none is pinned. When set it
	/// is authoritative: it overrides any reasoning directive already present in <paramref name="body"/>, so a
	/// pinned model is crash-safe even when the client sends its own reasoning field on the <c>/v1</c> surface.
	/// </param>
	/// <param name="cancellationToken">A token that may be used to cancel the streaming operation.</param>
	/// <returns>An asynchronous sequence of raw JSON event payloads.</returns>
	IAsyncEnumerable<string> ForwardSseAsync(
		BackendContext    backend,
		string            path,
		JsonObject        body,
		ReasoningEffort?  pinnedEffort,
		CancellationToken cancellationToken);
}
