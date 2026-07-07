// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Admin.Editing;
using OllamaProxy.Admin.Ui.Components.Backends;
using OllamaProxy.Configuration;

namespace OllamaProxy.Tests.Admin.Ui.Components.Backends;

/// <summary>
/// Tests for <see cref="BackendCardPresenter"/>, the pure mapping behind the <see cref="BackendCard"/> header:
/// the display name shown in the collapsed row and the operating-mode badge label and tooltip.
/// </summary>
/// <remarks>
/// The presenter answers three small questions the card header would otherwise embed in markup: what name to
/// show when the operator has not named a backend yet, what short label a mode's badge reads, and what tooltip
/// describes that mode. These tests pin down each with golden values (Assert.Equal), because the strings are the
/// operator-facing copy the header renders, and confirm the three modes map to three distinct descriptions so a
/// future copy edit cannot silently collapse two of them.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class BackendCardPresenterTests
{
	/// <summary>
	/// Verifies that a backend with a non-blank name renders that name verbatim in the header.
	/// </summary>
	[Fact]
	public void DisplayName_WhenNamed_ReturnsName()
	{
		// Arrange
		var backend = new DesiredBackend { Name = "openai-prod" };

		// Act
		string result = BackendCardPresenter.DisplayName(backend);

		// Assert
		Assert.Equal("openai-prod", result);
	}

	/// <summary>
	/// Verifies that a backend whose name is null, empty, or whitespace falls back to the unnamed placeholder,
	/// so a not-yet-named backend still has a stable header label.
	/// </summary>
	/// <param name="name">The blank name variant under test.</param>
	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void DisplayName_WhenBlank_ReturnsPlaceholder(string? name)
	{
		// Arrange
		var backend = new DesiredBackend { Name = name! };

		// Act
		string result = BackendCardPresenter.DisplayName(backend);

		// Assert
		Assert.Equal("(unnamed backend)", result);
	}

	/// <summary>
	/// Verifies that each defined operating mode maps to its human-readable badge label.
	/// </summary>
	/// <param name="mode">The operating mode under test.</param>
	/// <param name="expected">The expected badge label.</param>
	[Theory]
	[InlineData(OperatingMode.PlugAndPlay, "Plug-and-play")]
	[InlineData(OperatingMode.Hybrid, "Hybrid")]
	[InlineData(OperatingMode.Explicit, "Explicit")]
	public void ModeLabel_WhenModeDefined_ReturnsLabel(OperatingMode mode, string expected)
	{
		// Act
		string result = BackendCardPresenter.ModeLabel(mode);

		// Assert
		Assert.Equal(expected, result);
	}

	/// <summary>
	/// Verifies that each defined operating mode maps to a non-blank tooltip description.
	/// </summary>
	/// <param name="mode">The operating mode under test.</param>
	[Theory]
	[InlineData(OperatingMode.PlugAndPlay)]
	[InlineData(OperatingMode.Hybrid)]
	[InlineData(OperatingMode.Explicit)]
	public void ModeDescription_WhenModeDefined_ReturnsNonBlankDescription(OperatingMode mode)
	{
		// Act
		string result = BackendCardPresenter.ModeDescription(mode);

		// Assert
		Assert.False(string.IsNullOrWhiteSpace(result), $"Mode '{mode}' produced a blank description.");
	}

	/// <summary>
	/// Verifies that the three modes map to three distinct descriptions, so a copy edit cannot silently
	/// collapse two modes onto the same tooltip.
	/// </summary>
	[Fact]
	public void ModeDescription_ForAllModes_AreDistinct()
	{
		// Arrange
		string plugAndPlay = BackendCardPresenter.ModeDescription(OperatingMode.PlugAndPlay);
		string hybrid = BackendCardPresenter.ModeDescription(OperatingMode.Hybrid);
		string @explicit = BackendCardPresenter.ModeDescription(OperatingMode.Explicit);

		// Assert
		Assert.Equal(3, new HashSet<string>([plugAndPlay, hybrid, @explicit]).Count);
	}
}
