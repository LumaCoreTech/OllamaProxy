// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Configuration;

namespace OllamaProxy.Providers.Abstractions;

/// <summary>
/// A provider's self-describing "business card": the cheap, options-free metadata that identifies a provider
/// family and supplies its sensible defaults, kept deliberately separate from the heavy, options-dependent
/// behavior of an <see cref="IProviderAdapter"/>. Each adapter publishes one through
/// <see cref="IProviderDescriptorSource.Descriptor"/>, and the proxy aggregates the registered descriptors (rather than
/// constructing the adapters) wherever it only needs to <em>know about</em> a provider rather than call it:
/// configuration validation, the admin provider picker, and the mode/URL defaults. Centralizing these four facts
/// on the adapter is what lets a new provider be added by implementing and registering it alone.
/// </summary>
/// <param name="ProviderType">
/// The canonical provider-type discriminator (for example <c>openai</c>), matched case-insensitively against a
/// backend's configured <see cref="BackendOptions.ProviderType"/> to select the adapter. It is the descriptor's
/// identity: the catalog indexes by this value and rejects two descriptors that declare the same one.
/// </param>
/// <param name="DisplayName">
/// The human-facing label for the provider (for example <c>OpenAI</c>, <c>vLLM</c>), shown in the admin provider
/// picker. Purely cosmetic, never matched or persisted.
/// </param>
/// <param name="DefaultMode">
/// The <see cref="OperatingMode"/> a freshly added backend of this provider should start in when the operator
/// pins no explicit mode. Capability-rich families (Venice, OpenRouter) advertise enough metadata to publish a
/// complete catalog immediately, so they default to <see cref="OperatingMode.PlugAndPlay"/>; metadata-poor
/// families (OpenAI, vLLM) default to the conservative <see cref="OperatingMode.Explicit"/>. Only a starting
/// default: an explicit <see cref="BackendOptions.Mode"/> always wins.
/// </param>
/// <param name="DefaultBaseUrl">
/// The canonical base URL prefilled in the admin UI for a freshly added backend of this provider, or
/// <see cref="string.Empty"/> when the provider has no fixed public endpoint (for example vLLM, which is always
/// self-hosted). A convenience prefill only: the operator may change it at any time, and it is never validated as
/// "the correct" URL.
/// </param>
/// <remarks>
/// The type is intentionally free of any dependency on <see cref="IProviderAdapter"/>'s collaborators (HTTP
/// clients, options, the clock). That independence is what makes it safe to register and read a descriptor during
/// options validation without materializing an adapter, which would re-enter the very options graph being
/// validated. The record is compared by value, so two descriptors with identical fields compare equal.
/// </remarks>
public sealed record ProviderDescriptor(
	string        ProviderType,
	string        DisplayName,
	OperatingMode DefaultMode,
	string        DefaultBaseUrl);
