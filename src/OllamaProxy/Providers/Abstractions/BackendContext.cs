// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Configuration;

namespace OllamaProxy.Providers.Abstractions;

/// <summary>
/// Identifies the backend an <see cref="IProviderAdapter"/> operation targets, in one of two shapes:
/// <list type="bullet">
///     <item>
///         <description>
///         A <b>committed</b> backend (<see cref="Draft"/> is <see langword="null"/>) is identified by
///         its <see cref="Name"/>, the logical key from configuration. The adapter and the HTTP client
///         infrastructure resolve everything else (base address, authentication, probing settings) from
///         that name, so the call carries only the key. This is the steady-state routing path.
///         </description>
///     </item>
///     <item>
///         <description>
///         A <b>draft</b> backend (<see cref="Draft"/> is non-<see langword="null"/>) carries its own
///         configuration inline because it is not yet part of the committed options the infrastructure
///         was built from, for example a backend an operator is previewing before saving it. The
///         adapter must use the inline <see cref="Draft"/> for the base address, credentials, and
///         probing settings rather than a name-based lookup, since no committed entry exists to resolve.
///         </description>
///     </item>
/// </list>
/// Carrying the identity as a named record rather than a bare string keeps call sites self-documenting
/// and is the seam that makes preview-before-commit discovery possible without registering the backend.
/// </summary>
/// <param name="Name">
/// The logical backend identifier. For a committed backend this is its configuration key; for a draft
/// backend it is a synthetic placeholder used only for diagnostics, because no committed entry exists.
/// </param>
/// <param name="Draft">
/// The inline configuration for a draft (not-yet-committed) backend, or <see langword="null"/> for a
/// committed backend whose configuration is resolved by <see cref="Name"/>. When non-<see langword="null"/>
/// it is authoritative: the adapter and HTTP client build an ad-hoc client and read probing settings
/// from it instead of any name-based lookup.
/// </param>
sealed record BackendContext(string Name, BackendOptions? Draft = null);
