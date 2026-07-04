// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Configuration;

namespace OllamaProxy.Tests.Configuration;

/// <summary>
/// Tests for <see cref="ReasoningEffortExtensions"/>, which translates a neutral
/// <see cref="ReasoningEffort"/> onto the canonical OpenAI <c>reasoning_effort</c> wire token shared by
/// most OpenAI-compatible backends.
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
}
