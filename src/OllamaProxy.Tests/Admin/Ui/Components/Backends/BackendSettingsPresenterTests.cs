// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Admin.Editing;
using OllamaProxy.Admin.Ui.Components.Backends;

namespace OllamaProxy.Tests.Admin.Ui.Components.Backends;

/// <summary>
/// Tests for <see cref="BackendSettingsPresenter"/>, the pure mapping behind the <see cref="BackendSettings"/>
/// form's API-key field placeholder.
/// </summary>
/// <remarks>
/// The placeholder encodes the write-only-key contract: a backend being added (<see cref="DesiredBackend.OriginalName"/>
/// is <see langword="null"/>) must supply a key, so the field reads "Required"; an existing backend loads with a
/// blank key by design and keeps its saved secret if left blank, so the field reads the saved-secret hint. These
/// tests pin both placeholders as golden values because they are the operator-facing copy that tells them whether
/// a key is expected.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class BackendSettingsPresenterTests
{
	/// <summary>
	/// Verifies that a backend being added — one that never loaded from an existing configuration, so its
	/// <see cref="DesiredBackend.OriginalName"/> is <see langword="null"/> — shows the "Required" placeholder,
	/// because there is no saved secret to keep.
	/// </summary>
	[Fact]
	public void ApiKeyPlaceholder_WhenBackendIsNew_ReturnsRequired()
	{
		// Arrange: a freshly added backend carries no OriginalName.
		var backend = new DesiredBackend { Name = "new-backend" };

		// Act
		string result = BackendSettingsPresenter.ApiKeyPlaceholder(backend);

		// Assert
		Assert.Equal("Required", result);
	}

	/// <summary>
	/// Verifies that an existing backend — one that loaded with an <see cref="DesiredBackend.OriginalName"/> —
	/// shows the saved-secret hint, telling the operator the field may be left blank to keep the stored key.
	/// </summary>
	[Fact]
	public void ApiKeyPlaceholder_WhenBackendExists_ReturnsSavedSecretHint()
	{
		// Arrange: an existing backend carries the name it loaded with.
		var backend = new DesiredBackend { Name = "openai-prod", OriginalName = "openai-prod" };

		// Act
		string result = BackendSettingsPresenter.ApiKeyPlaceholder(backend);

		// Assert
		Assert.Equal("•••• saved — leave blank to keep", result);
	}

	/// <summary>
	/// Verifies that a rename — <see cref="DesiredBackend.Name"/> differing from
	/// <see cref="DesiredBackend.OriginalName"/> — still counts as an existing backend, so the saved-secret hint
	/// (not "Required") is shown. The placeholder keys off OriginalName, not the current name.
	/// </summary>
	[Fact]
	public void ApiKeyPlaceholder_WhenExistingBackendRenamed_ReturnsSavedSecretHint()
	{
		// Arrange: the operator renamed the backend, but it still loaded from an existing configuration.
		var backend = new DesiredBackend { Name = "openai-renamed", OriginalName = "openai-prod" };

		// Act
		string result = BackendSettingsPresenter.ApiKeyPlaceholder(backend);

		// Assert
		Assert.Equal("•••• saved — leave blank to keep", result);
	}
}
