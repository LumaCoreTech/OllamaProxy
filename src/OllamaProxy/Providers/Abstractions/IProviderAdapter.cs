// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Configuration;
using OllamaProxy.Contracts.Ollama;

namespace OllamaProxy.Providers.Abstractions;

/// <summary>
/// The boundary between the provider-neutral core and a concrete upstream provider. An adapter
/// encapsulates everything provider-specific: translating an inbound Ollama request to the provider wire
/// format, translating the provider response (streaming or not) back to the Ollama format, discovering
/// the available models, and determining each model's capabilities. The endpoints and model router speak
/// only Ollama contracts plus a <see cref="BackendContext"/>, so a new provider is added by implementing
/// this interface without touching the core.
/// </summary>
interface IProviderAdapter
{
	/// <summary>
	/// Gets the provider-type discriminator this adapter handles (for example <c>openai</c>), matched against a
	/// backend's configured provider type to select the adapter at routing time. It is the instance-level
	/// companion to the static <see cref="IProviderDescriptorSource.Descriptor"/>, which the resolver cannot
	/// reach polymorphically through an interface reference.
	/// </summary>
	string ProviderType { get; }

	/// <summary>
	/// Executes a streaming chat completion, yielding one <see cref="OllamaChatResponse"/> chunk per
	/// upstream delta and a terminal chunk carrying the <c>done</c> flag and token accounting.
	/// </summary>
	/// <param name="backend">The backend to route the call to.</param>
	/// <param name="upstreamModel">The resolved upstream model identifier to request.</param>
	/// <param name="request">The inbound Ollama chat request to translate and forward.</param>
	/// <param name="pinnedEffort">
	/// The model's pinned reasoning effort, or <see langword="null"/> when none is pinned. When set it is
	/// authoritative: it overrides both the request's <c>think</c> directive and the backend default, so a
	/// client can never push a level the model rejects.
	/// </param>
	/// <param name="cancellationToken">A token that may be used to cancel the streaming operation.</param>
	/// <returns>An asynchronous sequence of Ollama-formatted response chunks.</returns>
	IAsyncEnumerable<OllamaChatResponse> StreamChatAsync(
		BackendContext    backend,
		string            upstreamModel,
		OllamaChatRequest request,
		ReasoningEffort?  pinnedEffort,
		CancellationToken cancellationToken);

	/// <summary>
	/// Executes a non-streaming chat completion and returns the single aggregated Ollama response.
	/// </summary>
	/// <param name="backend">The backend to route the call to.</param>
	/// <param name="upstreamModel">The resolved upstream model identifier to request.</param>
	/// <param name="request">The inbound Ollama chat request to translate and forward.</param>
	/// <param name="pinnedEffort">
	/// The model's pinned reasoning effort, or <see langword="null"/> when none is pinned. When set it is
	/// authoritative: it overrides both the request's <c>think</c> directive and the backend default, so a
	/// client can never push a level the model rejects.
	/// </param>
	/// <param name="cancellationToken">A token that may be used to cancel the operation.</param>
	/// <returns>The completed Ollama chat response.</returns>
	Task<OllamaChatResponse> CompleteChatAsync(
		BackendContext    backend,
		string            upstreamModel,
		OllamaChatRequest request,
		ReasoningEffort?  pinnedEffort,
		CancellationToken cancellationToken);

	/// <summary>
	/// Produces embedding vectors for the supplied input by forwarding to the backend's embeddings
	/// endpoint and translating the result to the Ollama embeddings format.
	/// </summary>
	/// <param name="backend">The backend to route the call to.</param>
	/// <param name="upstreamModel">The resolved upstream embedding model identifier to request.</param>
	/// <param name="request">The inbound Ollama embeddings request to translate and forward.</param>
	/// <param name="cancellationToken">A token that may be used to cancel the operation.</param>
	/// <returns>The Ollama-formatted embeddings response.</returns>
	Task<OllamaEmbedResponse> CreateEmbeddingsAsync(
		BackendContext     backend,
		string             upstreamModel,
		OllamaEmbedRequest request,
		CancellationToken  cancellationToken);

	/// <summary>
	/// Lists the models the backend currently offers, projected onto the provider-neutral
	/// <see cref="DiscoveredModel"/> shape for use by the registry and capability detection.
	/// </summary>
	/// <param name="backend">The backend to query.</param>
	/// <param name="cancellationToken">A token that may be used to cancel the operation.</param>
	/// <returns>The discovered models; empty when the backend reports none.</returns>
	Task<IReadOnlyList<DiscoveredModel>> DiscoverModelsAsync(
		BackendContext    backend,
		CancellationToken cancellationToken);

	/// <summary>
	/// Resolves the capabilities of a discovered model, applying the provider's detection strategy
	/// (metadata first, then optional active probing).
	/// </summary>
	/// <param name="backend">The backend the model belongs to.</param>
	/// <param name="model">The discovered model whose capabilities are being resolved.</param>
	/// <param name="cancellationToken">A token that may be used to cancel the operation.</param>
	/// <returns>The resolved capability set, annotated with the signal that produced it.</returns>
	Task<ModelCapabilities> DetermineCapabilitiesAsync(
		BackendContext    backend,
		DiscoveredModel   model,
		CancellationToken cancellationToken);
}
