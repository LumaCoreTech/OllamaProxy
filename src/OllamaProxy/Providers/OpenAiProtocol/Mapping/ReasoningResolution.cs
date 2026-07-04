// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Configuration;

namespace OllamaProxy.Providers.OpenAiProtocol.Mapping;

/// <summary>
/// Identifies where a resolved reasoning effort originated. This is the provenance answer to "why did
/// the proxy send (or not send) a reasoning directive?", surfaced in request traces so an operator can
/// tell a client-supplied effort apart from a backend default or an unspecified request.
/// </summary>
enum ReasoningEffortSource
{
	/// <summary>Neither the request nor the backend specified an effort; nothing is sent upstream.</summary>
	Unspecified,

	/// <summary>The effort came from the inbound request's <c>think</c> directive.</summary>
	Request,

	/// <summary>The effort came from the backend's configured default.</summary>
	BackendDefault,

	/// <summary>
	/// The effort came from a model's pinned registry entry
	/// (<see cref="OllamaProxy.Configuration.ModelRegistrationOptions.ReasoningEffort"/>). A pinned effort is
	/// authoritative: it overrides both an inbound <c>think</c> directive and the backend default, so this
	/// source is recorded when a pin shadowed whatever the request or backend would otherwise have selected.
	/// </summary>
	Pinned
}

/// <summary>
/// The outcome of resolving a chat request's reasoning effort: the resolved <see cref="Effort"/> (or
/// <see langword="null"/> when unspecified), the <see cref="Source"/> it came from, and the
/// <see cref="BackendDefault"/> that was in play. The provider applies <see cref="Effort"/> to the
/// upstream payload and records the full triple in the request trace for provenance.
/// </summary>
/// <param name="Effort">The resolved effort, or <see langword="null"/> when reasoning is unspecified.</param>
/// <param name="Source">Where the resolved effort originated.</param>
/// <param name="BackendDefault">The backend's configured default effort, or <see langword="null"/>.</param>
readonly record struct ReasoningResolution(
	ReasoningEffort?      Effort,
	ReasoningEffortSource Source,
	ReasoningEffort?      BackendDefault);
