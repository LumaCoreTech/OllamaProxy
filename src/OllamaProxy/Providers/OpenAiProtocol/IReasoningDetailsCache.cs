// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json.Nodes;

namespace OllamaProxy.Providers.OpenAiProtocol;

/// <summary>
/// A short-lived, process-local store that carries a backend's opaque <c>reasoning_details</c> blob across
/// a multi-turn tool-call conversation. The Ollama wire format has no field to convey that array and a
/// typed Ollama client would drop a non-standard one, so the proxy holds it instead: it
/// <see cref="Store"/>s the blob keyed by the assistant turn's tool calls when a backend returns it, and
/// <see cref="Retrieve"/>s it when that same turn is replayed on the follow-up request, re-attaching it so
/// the model can resume its paused reasoning.
/// <para>
/// The store is a best-effort cache, not durable state: a miss is normal and the caller simply omits the
/// field and lets the model continue without the signature. Implementations must be safe for concurrent
/// use, since one cache is shared across all in-flight requests.
/// </para>
/// </summary>
interface IReasoningDetailsCache
{
	/// <summary>
	/// Stores a <c>reasoning_details</c> blob under the supplied correlation key, renewing the entry's
	/// lifetime if the key is already present. The node is detached (deep-cloned) from any document it
	/// belongs to, so the caller may continue to use its own copy freely. A no-op when the round-trip is
	/// disabled by configuration.
	/// </summary>
	/// <param name="correlationKey">The key derived from the assistant turn's tool calls.</param>
	/// <param name="details">The opaque reasoning-details node captured from the backend response.</param>
	/// <exception cref="ArgumentException">
	/// <paramref name="correlationKey"/> is empty or consists only of white-space characters.
	/// </exception>
	/// <exception cref="ArgumentNullException"><paramref name="details"/> is <see langword="null"/>.</exception>
	void Store(string correlationKey, JsonNode details);

	/// <summary>
	/// Retrieves the <c>reasoning_details</c> blob previously stored under the supplied correlation key,
	/// renewing its lifetime (sliding expiration) so an active conversation keeps the entry warm. Returns a
	/// detached (deep-cloned) node the caller may freely parent onto an outgoing request, or
	/// <see langword="null"/> when no live entry exists for the key.
	/// </summary>
	/// <param name="correlationKey">The key derived from the assistant turn's tool calls.</param>
	/// <returns>A detached copy of the stored node, or <see langword="null"/> on a miss.</returns>
	/// <exception cref="ArgumentException">
	/// <paramref name="correlationKey"/> is empty or consists only of white-space characters.
	/// </exception>
	JsonNode? Retrieve(string correlationKey);
}
