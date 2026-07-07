// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Configuration;

namespace OllamaProxy.Tests.Configuration;

/// <summary>
/// Tests for <see cref="ReasoningEffortExtensions"/>, which translates a neutral
/// <see cref="ReasoningEffort"/> either onto the canonical OpenAI <c>reasoning_effort</c> wire token shared by
/// most OpenAI-compatible backends, or onto the human-readable label the admin UI shows in its
/// reasoning-effort selectors.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ReasoningEffortExtensionsTests
{
	/// <summary>
	/// Verifies that each defined effort maps to its lowercase OpenAI wire token.
	/// </summary>
	/// <param name="effortName">The name of the neutral effort under test.</param>
	/// <param name="expected">The expected wire token.</param>
	[Theory]
	[InlineData(nameof(ReasoningEffort.None), "none")]
	[InlineData(nameof(ReasoningEffort.Minimal), "minimal")]
	[InlineData(nameof(ReasoningEffort.Low), "low")]
	[InlineData(nameof(ReasoningEffort.Medium), "medium")]
	[InlineData(nameof(ReasoningEffort.High), "high")]
	[InlineData(nameof(ReasoningEffort.XHigh), "xhigh")]
	[InlineData(nameof(ReasoningEffort.Max), "max")]
	public void ToWireValue_WhenEffortDefined_ReturnsCanonicalToken(string effortName, string expected)
	{
		// Arrange
		var effort = Enum.Parse<ReasoningEffort>(effortName);

		// Act
		string result = effort.ToWireValue();

		// Assert
		Assert.Equal(expected, result);
	}

	/// <summary>
	/// Verifies that each defined effort maps to its human-readable admin-UI display label. The labels are
	/// golden values matched exactly (Assert.Equal), because they are the operator-facing copy the
	/// reasoning-effort selectors render — including the two that differ from a title-cased enum name
	/// (<see cref="ReasoningEffort.XHigh"/> reads "Extra high").
	/// </summary>
	/// <param name="effortName">The name of the neutral effort under test.</param>
	/// <param name="expected">The expected display label.</param>
	[Theory]
	[InlineData(nameof(ReasoningEffort.None), "None")]
	[InlineData(nameof(ReasoningEffort.Minimal), "Minimal")]
	[InlineData(nameof(ReasoningEffort.Low), "Low")]
	[InlineData(nameof(ReasoningEffort.Medium), "Medium")]
	[InlineData(nameof(ReasoningEffort.High), "High")]
	[InlineData(nameof(ReasoningEffort.XHigh), "Extra high")]
	[InlineData(nameof(ReasoningEffort.Max), "Max")]
	public void ToDisplayLabel_WhenEffortDefined_ReturnsDisplayLabel(string effortName, string expected)
	{
		// Arrange
		var effort = Enum.Parse<ReasoningEffort>(effortName);

		// Act
		string result = effort.ToDisplayLabel();

		// Assert
		Assert.Equal(expected, result);
	}

	/// <summary>
	/// Verifies that every defined <see cref="ReasoningEffort"/> value has a display label, so the selector
	/// options never fall through to the switch's <c>UnreachableException</c> arm as the enum grows.
	/// </summary>
	[Fact]
	public void ToDisplayLabel_ForEveryDefinedEffort_ReturnsNonEmptyLabel()
	{
		// Arrange
		ReasoningEffort[] all = Enum.GetValues<ReasoningEffort>();

		// Act & Assert: no defined value throws, and every label is non-blank.
		foreach (ReasoningEffort effort in all)
		{
			string label = effort.ToDisplayLabel();
			Assert.False(string.IsNullOrWhiteSpace(label), $"Effort '{effort}' produced a blank display label.");
		}
	}
}
