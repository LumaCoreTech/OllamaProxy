// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Admin.Reconciliation;
using OllamaProxy.Configuration;

namespace OllamaProxy.Admin.Ui.Components.Backends;

/// <summary>
/// The payload of the <see cref="BackendModels"/> editor's client-facing-name write-back: the pinned row being
/// edited and the raw field value. Carried as a named record rather than a value tuple so the
/// <see cref="Microsoft.AspNetCore.Components.EventCallback{TValue}"/> surface names both parts explicitly at
/// every call site.
/// </summary>
/// <param name="Model">The pinned row whose client-facing name is being edited.</param>
/// <param name="Value">The raw field value, possibly <see langword="null"/>, blank, or padded.</param>
public readonly record struct PinnedNameEdit(ReconciledModel Model, string? Value);

/// <summary>
/// The payload of the <see cref="BackendModels"/> editor's reasoning-effort write-back: the pinned row being
/// edited and the selected effort (<see langword="null"/> for the blank "Inherit" option). A named record rather
/// than a value tuple so the callback surface stays self-describing.
/// </summary>
/// <param name="Model">The pinned row whose reasoning-effort override is being edited.</param>
/// <param name="Value">The selected effort, or <see langword="null"/> when cleared to inherit.</param>
public readonly record struct PinnedReasoningEdit(ReconciledModel Model, ReasoningEffort? Value);

/// <summary>
/// The payload of the <see cref="BackendModels"/> editor's context-length write-back: the pinned row being
/// edited and the raw token count (<see langword="null"/> when cleared). A named record rather than a value
/// tuple so the callback surface stays self-describing.
/// </summary>
/// <param name="Model">The pinned row whose context-length override is being edited.</param>
/// <param name="Value">The raw field value in tokens, or <see langword="null"/> when cleared.</param>
public readonly record struct PinnedContextEdit(ReconciledModel Model, int? Value);

/// <summary>
/// The payload of the <see cref="BackendModels"/> editor's capability write-back: the pinned row being edited,
/// which capability flag changed, and its new value. A named record rather than a value tuple so the callback
/// surface names every part at the call site.
/// </summary>
/// <param name="Model">The pinned row whose capability is being edited.</param>
/// <param name="Capability">Which capability flag changed.</param>
/// <param name="Value">The new flag value from the checkbox.</param>
public readonly record struct PinnedCapabilityEdit(ReconciledModel Model, PinnedCapability Capability, bool Value);
