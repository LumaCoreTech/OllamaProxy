// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.CustomActions.Tests;

/// <summary>
/// Anchor for the unit tests covering the pure helpers behind the four managed installer custom actions
/// in <see cref="CustomActions"/>. Each action keeps its installer-bound entry point (the live
/// <c>Session</c>, the file system, sockets, and the network) untested and instead exposes the decision
/// logic as <see langword="internal"/> helpers that are exercised here on net472 — the same framework the
/// custom action ships on.
/// <para>
/// The suite is split one partial file per action, each a self-contained chapter:
/// </para>
/// <list type="number">
///     <item>
///     <see cref="TestBackend"/> (in <c>CustomActionsTests.TestBackend.cs</c>) — syntactic input
///     validation (<see cref="CustomActions.ValidateSyntax"/>) and HTTP-status interpretation
///     (<see cref="CustomActions.InterpretResponse"/>).
///     </item>
///     <item>
///     <see cref="CheckPorts"/> (in <c>CustomActionsTests.CheckPorts.cs</c>) — endpoint URL parsing
///     (<see cref="CustomActions.ParseLocalEndpoint"/>) and host-to-bind-address mapping
///     (<see cref="CustomActions.ResolveBindAddress"/>).
///     </item>
///     <item>
///     <see cref="OpenAdminUi"/> (in <c>CustomActionsTests.OpenAdminUi.cs</c>) — the absolute
///     http/https launch gate (<see cref="CustomActions.TryGetLaunchableAdminUrl"/>) that decides
///     whether an operator-entered admin URL is safe to hand to the shell.
///     </item>
///     <item>
///     <see cref="WriteAppSettings"/> (in <c>CustomActionsTests.WriteAppSettings.cs</c>) — the
///     config-write policy (<see cref="CustomActions.DecideConfigWriteAction"/>), the timestamped
///     backup-path builder, the two JSON document builders, and the JSON string escaper.
///     </item>
/// </list>
/// <para>
/// Reading order: the chapters are independent and can be read in any order; each nested class carries
/// its own summary describing the helper it pins and why the live entry point is out of scope. The
/// struct carriers these helpers return are tested separately in <see cref="TestOutcomeTests"/> and
/// <see cref="EndpointParseResultTests"/>.
/// </para>
/// </summary>
public partial class CustomActionsTests { }
