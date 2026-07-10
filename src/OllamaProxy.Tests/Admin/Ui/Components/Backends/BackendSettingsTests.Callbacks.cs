// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Bunit;

using OllamaProxy.Admin.Ui.Components.Backends;
using OllamaProxy.Configuration;

namespace OllamaProxy.Tests.Admin.Ui.Components.Backends;

// Callbacks: every operator edit raises the EventCallback the page supplies.
//
// BackendSettings is presentational — it owns no mutation, only the DOM that triggers one. The form splits its
// change notifications by intent: the three plain-bound fields share one OnConfigurationChanged notifier (via
// @bind:after), while Provider and Mode each carry a dedicated callback (via @bind:set) so the page can run their
// provider/mode-switch side effects. These tests change each control and assert the matching callback fired — and,
// for Provider and Mode, that the new value was carried — so a rewiring that drops or crosses a handler is caught:
//
//   1. Configuration-changed fields: Name, Base URL, and API key each raise OnConfigurationChanged (Name /
//      BaseUrl / ApiKey).
//   2. Provider picker: raises OnProviderTypeChanged with the chosen provider type (Provider).
//   3. Mode picker: raises OnModeChanged with the chosen mode, and with null for the provider-based default
//      (Mode / ModeDefault).
public sealed partial class BackendSettingsTests
{
	// --- 1. Configuration-changed fields ---

	/// <summary>
	/// Verifies that editing the Name field raises <see cref="BackendSettings.OnConfigurationChanged"/> so the
	/// page can re-evaluate its dirty state.
	/// </summary>
	[Fact]
	public void Change_Name_InvokesOnConfigurationChanged()
	{
		// Arrange
		int invocations = 0;

		IRenderedComponent<BackendSettings> cut = RenderSettings(
			configure: parameters => parameters.Add(
				component => component.OnConfigurationChanged,
				() => invocations++));

		// Act
		cut.Find("input[type=text]").Change("renamed-backend");

		// Assert
		Assert.Equal(1, invocations);
	}

	/// <summary>
	/// Verifies that editing the Base URL field raises <see cref="BackendSettings.OnConfigurationChanged"/> so the
	/// page can re-evaluate its dirty state.
	/// </summary>
	[Fact]
	public void Change_BaseUrl_InvokesOnConfigurationChanged()
	{
		// Arrange
		int invocations = 0;

		IRenderedComponent<BackendSettings> cut = RenderSettings(
			configure: parameters => parameters.Add(
				component => component.OnConfigurationChanged,
				() => invocations++));

		// Act
		cut.Find("input[type=url]").Change("https://new.example.test/v1");

		// Assert
		Assert.Equal(1, invocations);
	}

	/// <summary>
	/// Verifies that editing the API key field raises <see cref="BackendSettings.OnConfigurationChanged"/> so the
	/// page can re-evaluate its dirty state.
	/// </summary>
	[Fact]
	public void Change_ApiKey_InvokesOnConfigurationChanged()
	{
		// Arrange
		int invocations = 0;

		IRenderedComponent<BackendSettings> cut = RenderSettings(
			configure: parameters => parameters.Add(
				component => component.OnConfigurationChanged,
				() => invocations++));

		// Act
		cut.Find("input[type=password]").Change("new-secret-key");

		// Assert
		Assert.Equal(1, invocations);
	}

	// --- 2. Provider picker ---

	/// <summary>
	/// Verifies that picking a different provider raises <see cref="BackendSettings.OnProviderTypeChanged"/> with
	/// the chosen provider-type discriminator, so the page can run its "switch provider → refill default base URL"
	/// side effect.
	/// </summary>
	[Fact]
	public void Change_Provider_InvokesOnProviderTypeChangedWithSelectedType()
	{
		// Arrange
		string? received = null;

		IRenderedComponent<BackendSettings> cut = RenderSettings(
			configure: parameters => parameters.Add(
				component => component.OnProviderTypeChanged,
				value => received = value));

		// Act
		ProviderSelect(cut).Change("venice");

		// Assert
		Assert.Equal("venice", received);
	}

	// --- 3. Mode picker ---

	/// <summary>
	/// Verifies that picking a concrete mode raises <see cref="BackendSettings.OnModeChanged"/> with that mode, so
	/// the page can write it and refresh the reconciliation table.
	/// </summary>
	[Fact]
	public void Change_Mode_InvokesOnModeChangedWithSelectedMode()
	{
		// Arrange
		OperatingMode? received = null;
		bool invoked = false;

		IRenderedComponent<BackendSettings> cut = RenderSettings(
			configure: parameters => parameters.Add(
				component => component.OnModeChanged,
				value =>
				{
					received = value;
					invoked = true;
				}));

		// Act
		ModeSelect(cut).Change(nameof(OperatingMode.PlugAndPlay));

		// Assert
		Assert.True(invoked);
		Assert.Equal(OperatingMode.PlugAndPlay, received);
	}

	/// <summary>
	/// Verifies that picking the empty "provider-based default" option raises
	/// <see cref="BackendSettings.OnModeChanged"/> with <see langword="null"/>, so clearing the mode is carried to
	/// the page as an unset mode rather than a concrete one.
	/// </summary>
	[Fact]
	public void Change_Mode_WhenProviderDefaultSelected_InvokesOnModeChangedWithNull()
	{
		// Arrange: start from a concrete mode so the change to the empty default is a real transition.
		OperatingMode? received = OperatingMode.Explicit;
		bool invoked = false;

		IRenderedComponent<BackendSettings> cut = RenderSettings(
			backend: CreateBackend(mode: OperatingMode.Explicit),
			configure: parameters => parameters.Add(
				component => component.OnModeChanged,
				value =>
				{
					received = value;
					invoked = true;
				}));

		// Act: the empty-valued option maps to the provider-based default.
		ModeSelect(cut).Change(string.Empty);

		// Assert
		Assert.True(invoked);
		Assert.Null(received);
	}
}
