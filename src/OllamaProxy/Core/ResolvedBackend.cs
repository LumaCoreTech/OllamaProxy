// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Core;

/// <summary>
/// The pair of collaborators needed to service a request against a particular backend. A
/// <b>backend</b> is one configured upstream (a base URL plus credentials, identified by its
/// logical name); a <b>provider</b> is the wire protocol the backend speaks
/// (for example <c>openai</c>, <c>vllm</c>, or <c>venice</c>). One <see cref="IProviderAdapter"/>
/// instance is shared by all backends that share a provider type; the backend identity is carried
/// separately in the <see cref="Context"/> so the adapter can pick the right pre-configured
/// <see cref="System.Net.Http.HttpClient"/>. The endpoints obtain this pair from
/// <see cref="IProviderResolver"/> after the router has mapped a model name to a backend, then
/// invoke the adapter with the context to forward the call.
/// </summary>
/// <param name="Adapter">
/// The provider adapter for the backend's protocol (for example <c>openai</c>, <c>vllm</c>, or
/// <c>venice</c>), selected by the resolver.
/// </param>
/// <param name="Context">The backend identity passed to the adapter on each call.</param>
sealed record ResolvedBackend(IProviderAdapter Adapter, BackendContext Context);
