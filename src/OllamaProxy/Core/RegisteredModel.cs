// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Configuration;
using OllamaProxy.Endpoints;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Core;

/// <summary>
/// A fully resolved, client-facing model entry produced by the model catalog. It binds the name a
/// client uses to the backend that serves it, the upstream model identifier to request, and the
/// model's resolved capabilities. The router returns this for a model lookup, and the
/// <c>/api/tags</c> and <c>/api/show</c> endpoints project it into their Ollama responses.
/// </summary>
/// <remarks>
/// The catalog combines two sources: explicit entries from each backend's model registry
/// (<see cref="ModelRegistrationOptions"/>, configured under <c>OllamaProxy:Backends:&lt;name&gt;:Models</c>)
/// and models discovered live from the configured backends. The assembly is performed once at startup by
/// <see cref="ModelCatalogBuilder"/>.
/// <para>
/// Value equality is the compiler-generated, field-by-field comparison across all primary constructor
/// parameters, including the <see cref="ModelCapabilities"/> and <see cref="ProviderModelMetadata"/> records,
/// which are themselves compared by value. Do not override <see cref="Equals(object?)"/> or
/// <see cref="GetHashCode"/> without auditing the consequences: callers may rely on the default semantics (e.g.
/// in dictionaries, test assertions, or cache keys).
/// </para>
/// </remarks>
/// <param name="Name">The model name exposed to clients.</param>
/// <param name="BackendName">
/// The logical backend that serves the model. Backend names are compared case-insensitively
/// throughout the proxy (see <see cref="ModelRouter"/> and <see cref="ProviderResolver"/>).
/// </param>
/// <param name="UpstreamModel">The upstream model identifier requested from the backend.</param>
/// <param name="Capabilities">The resolved capabilities advertised for the model.</param>
/// <param name="ContextLength">
/// The effective maximum context window (in tokens) advertised for the model. Resolved at startup under a
/// fallback rule: an explicit per-model override wins, otherwise the backend's reported value, otherwise the
/// backend default. So the reported value is never silently narrowed, while a backend that reports none can
/// still expose the model through its default. Enforced at request time by
/// <see cref="EndpointRouting.TryValidateContextWindow"/>, which rejects any request whose client-supplied
/// <c>options.num_ctx</c> exceeds this limit.
/// </param>
/// <param name="ReasoningEffort">
/// The fixed reasoning effort pinned to this model via its registry entry
/// (<see cref="ModelRegistrationOptions.ReasoningEffort"/>), or <see langword="null"/> when none is pinned.
/// When set it is authoritative for chat requests to this model: it overrides both the inbound <c>think</c>
/// directive and the backend default, so a client can never push a value the model rejects. Only pinned
/// (registry) models can carry this; discovered models always resolve <see langword="null"/> here.
/// </param>
/// <param name="CreatedAtUtc">
/// The UTC timestamp when the backend listed this model (Unix epoch seconds converted to
/// <see cref="DateTimeOffset"/>), or <see langword="null"/> when the backend reported no creation time
/// or the model was pinned via the registry. This is the backend's listing date, not necessarily the
/// model's original release date.
/// </param>
/// <param name="Metadata">
/// Optional descriptive metadata (display name, description, tokenizer, quantization, pricing, …) the backend
/// published for a discovered model, or <see langword="null"/> when the backend reported none or the model was
/// pinned via the registry (registry pins carry no live backend metadata). It never affects routing; the admin
/// model surface uses it to show the richest honest picture each backend offers.
/// </param>
public sealed record RegisteredModel(
	string                 Name,
	string                 BackendName,
	string                 UpstreamModel,
	ModelCapabilities      Capabilities,
	long                   ContextLength,
	ReasoningEffort?       ReasoningEffort = null,
	DateTimeOffset?        CreatedAtUtc    = null,
	ProviderModelMetadata? Metadata        = null);
