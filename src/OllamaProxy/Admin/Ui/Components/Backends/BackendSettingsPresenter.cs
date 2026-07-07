// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Admin.Editing;

namespace OllamaProxy.Admin.Ui.Components.Backends;

/// <summary>
/// Translates a backend's draft state into the copy the <see cref="BackendSettings"/> form renders: currently
/// the API-key field placeholder, which differs between a newly added backend and an existing one.
/// </summary>
/// <remarks>
/// The mapping logic lives here rather than in the component's code-behind so it can be unit-tested as a pure
/// function without rendering the component. <see cref="BackendSettings"/> is a thin renderer over the strings
/// this presenter returns.
/// </remarks>
static class BackendSettingsPresenter
{
	/// <summary>
	/// Gets the placeholder shown in the API-key field. An existing backend shows a "saved secret" hint (the
	/// field is blank by the write-only contract, and leaving it blank keeps the saved key); a newly added
	/// backend shows that a key is required.
	/// </summary>
	/// <param name="backend">The backend whose API-key field is being rendered.</param>
	/// <returns>The placeholder text for the field.</returns>
	public static string ApiKeyPlaceholder(DesiredBackend backend)
	{
		return backend.OriginalName is null
			       ? "Required"
			       : "•••• saved — leave blank to keep";
	}
}
