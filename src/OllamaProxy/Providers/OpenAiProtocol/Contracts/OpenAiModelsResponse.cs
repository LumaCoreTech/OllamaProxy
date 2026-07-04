// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json.Serialization;

namespace OllamaProxy.Providers.OpenAiProtocol.Contracts;

/// <summary>
/// Response body for the OpenAI-style <c>GET /v1/models</c> endpoint, used during model discovery. The
/// envelope shape (<c>{ "data": [ … ] }</c>) is shared across every OpenAI-compatible backend, while the
/// per-entry shape differs per provider, so the entry type is left open as <typeparamref name="TModel"/>
/// and each provider supplies its own discovery contract. The shared transport seam
/// <see cref="OpenAiCompatibleProvider.DiscoverModelsCoreAsync{TModel}"/> deserializes into this type.
/// </summary>
/// <typeparam name="TModel">The provider-specific model-entry contract carried under <c>data</c>.</typeparam>
/// <param name="Data">
/// The available upstream models. May be absent (the property is <see langword="null"/>) when a backend
/// omits the array; the discovery seam treats that as an empty listing.
/// </param>
sealed record OpenAiModelsResponse<TModel>([property: JsonPropertyName("data")] IReadOnlyList<TModel>? Data);
