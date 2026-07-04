// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Providers.Abstractions;

/// <summary>
/// The static seam through which a provider publishes its <see cref="ProviderDescriptor"/>: its cheap,
/// options-free identity and defaults, without an instance. It is kept separate from
/// <see cref="IProviderAdapter"/> because a <see langword="static abstract"/> member would make its declaring
/// interface unusable as a generic type argument (CS8920), and <see cref="IProviderAdapter"/> must stay usable
/// as one. Concrete providers implement both: the registration helper reads <see cref="Descriptor"/> through
/// this constraint to register the descriptor alongside the adapter, without constructing the adapter or
/// touching the options graph it depends on.
/// </summary>
interface IProviderDescriptorSource
{
	/// <summary>
	/// Gets the provider's self-describing metadata: its canonical type, display name, and mode/URL defaults.
	/// It is <see langword="static"/> so the proxy can read a provider's identity and defaults without
	/// constructing the adapter: the registration helper publishes it, and the provider catalog aggregates the
	/// descriptors to drive configuration validation, the admin picker, and the defaults.
	/// </summary>
	static abstract ProviderDescriptor Descriptor { get; }
}
