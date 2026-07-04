// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.CustomActions;

/// <summary>
/// The managed Windows Installer custom actions backing the installer's configuration dialogs.
/// Four actions are exposed, each implemented in its own partial-class file:
/// <list type="bullet">
///     <item><see cref="TestBackend"/> validates the operator's backend URL and key.</item>
///     <item><see cref="CheckPorts"/> verifies that the chosen local endpoints are not already in use.</item>
///     <item><see cref="OpenAdminUi"/> opens the admin UI in the operator's browser after install.</item>
///     <item><see cref="WriteAppSettings"/> writes the entered values into the protected data folder.</item>
/// </list>
/// </summary>
public static partial class CustomActions { }
