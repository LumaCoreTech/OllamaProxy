// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json.Nodes;

using OllamaProxy.Configuration;
using OllamaProxy.Providers.OpenAiProtocol.Mapping;

namespace OllamaProxy.Tests.Providers.OpenAiProtocol.Mapping;

/// <summary>
/// Tests for <see cref="ReasoningEffortParser"/>, which resolves the effective reasoning effort from
/// an inbound Ollama <c>think</c> directive and a backend default. The story moves from the per-request
/// directive itself — both the boolean shorthand and the richer level string — through the precedence
/// of a request value over the backend default, and finally to the "unspecified means nothing" cases
/// where an absent or unrecognized directive resolves to <see langword="null"/>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ReasoningEffortParserTests
{
	// --- 1. Boolean shorthand: true => Medium, false => None ---

	/// <summary>
	/// Verifies that the Ollama boolean shorthand <c>think: true</c> resolves to a balanced
	/// <see cref="ReasoningEffort.Medium"/> budget.
	/// </summary>
	[Fact]
	public void Resolve_WhenThinkIsTrue_ResolvesToMedium()
	{
		// Act
		ReasoningResolution result = ReasoningEffortParser.Resolve(JsonValue.Create(true), backendDefault: null);

		// Assert
		Assert.Equal(ReasoningEffort.Medium, result.Effort);
		Assert.Equal(ReasoningEffortSource.Request, result.Source);
		Assert.Null(result.BackendDefault);
	}

	/// <summary>
	/// Verifies that the Ollama boolean shorthand <c>think: false</c> resolves to
	/// <see cref="ReasoningEffort.None"/>, explicitly turning reasoning off.
	/// </summary>
	[Fact]
	public void Resolve_WhenThinkIsFalse_ResolvesToNone()
	{
		// Act
		ReasoningResolution result = ReasoningEffortParser.Resolve(JsonValue.Create(false), backendDefault: null);

		// Assert
		Assert.Equal(ReasoningEffort.None, result.Effort);
		Assert.Equal(ReasoningEffortSource.Request, result.Source);
	}

	// --- 2. Level strings: every recognized token, case-insensitive ---

	/// <summary>
	/// Verifies that each recognized level string maps to its neutral effort, irrespective of casing
	/// and surrounding whitespace.
	/// </summary>
	/// <param name="level">The raw <c>think</c> level string.</param>
	/// <param name="expectedName">The name of the expected resolved effort.</param>
	[Theory]
	[InlineData("none", nameof(ReasoningEffort.None))]
	[InlineData("minimal", nameof(ReasoningEffort.Minimal))]
	[InlineData("low", nameof(ReasoningEffort.Low))]
	[InlineData("medium", nameof(ReasoningEffort.Medium))]
	[InlineData("high", nameof(ReasoningEffort.High))]
	[InlineData("xhigh", nameof(ReasoningEffort.XHigh))]
	[InlineData("max", nameof(ReasoningEffort.Max))]
	[InlineData("HIGH", nameof(ReasoningEffort.High))]
	[InlineData("  Medium  ", nameof(ReasoningEffort.Medium))]
	public void Resolve_WhenThinkIsKnownLevel_ResolvesToMatchingEffort(string level, string expectedName)
	{
		// Arrange
		var expected = Enum.Parse<ReasoningEffort>(expectedName);

		// Act
		ReasoningResolution result = ReasoningEffortParser.Resolve(JsonValue.Create(level), backendDefault: null);

		// Assert
		Assert.Equal(expected, result.Effort);
		Assert.Equal(ReasoningEffortSource.Request, result.Source);
	}

	// --- 3. Precedence: a request directive overrides the backend default ---

	/// <summary>
	/// Verifies that an explicit per-request directive wins over the backend default.
	/// </summary>
	[Fact]
	public void Resolve_WhenThinkAndBackendDefaultDiffer_PrefersThink()
	{
		// Act
		ReasoningResolution result = ReasoningEffortParser.Resolve(JsonValue.Create("high"), ReasoningEffort.Low);

		// Assert
		Assert.Equal(ReasoningEffort.High, result.Effort);
		Assert.Equal(ReasoningEffortSource.Request, result.Source);
		Assert.Equal(ReasoningEffort.Low, result.BackendDefault);
	}

	/// <summary>
	/// Verifies that the backend default applies when the request carries no <c>think</c> directive.
	/// </summary>
	[Fact]
	public void Resolve_WhenThinkAbsent_FallsBackToBackendDefault()
	{
		// Act
		ReasoningResolution result = ReasoningEffortParser.Resolve(think: null, ReasoningEffort.High);

		// Assert
		Assert.Equal(ReasoningEffort.High, result.Effort);
		Assert.Equal(ReasoningEffortSource.BackendDefault, result.Source);
		Assert.Equal(ReasoningEffort.High, result.BackendDefault);
	}

	// --- 4. Unspecified means nothing: absent / unrecognized / non-value nodes resolve to null ---

	/// <summary>
	/// Verifies that an absent directive and an absent backend default resolve to
	/// <see langword="null"/>, so no reasoning is sent.
	/// </summary>
	[Fact]
	public void Resolve_WhenThinkAndBackendDefaultAbsent_ResolvesToNull()
	{
		// Act
		ReasoningResolution result = ReasoningEffortParser.Resolve(think: null, backendDefault: null);

		// Assert
		Assert.Null(result.Effort);
		Assert.Equal(ReasoningEffortSource.Unspecified, result.Source);
		Assert.Null(result.BackendDefault);
	}

	/// <summary>
	/// Verifies that an unrecognized level string is ignored — falling through to the backend default —
	/// rather than guessed, so a typo never silently changes behavior.
	/// </summary>
	[Fact]
	public void Resolve_WhenThinkIsUnknownLevel_FallsBackToBackendDefault()
	{
		// Act
		ReasoningResolution result = ReasoningEffortParser.Resolve(JsonValue.Create("turbo"), ReasoningEffort.Low);

		// Assert
		Assert.Equal(ReasoningEffort.Low, result.Effort);
		Assert.Equal(ReasoningEffortSource.BackendDefault, result.Source);
	}

	/// <summary>
	/// Verifies that a non-value node (an object rather than a boolean or string) carries no usable
	/// directive and resolves to <see langword="null"/> when no backend default applies.
	/// </summary>
	[Fact]
	public void Resolve_WhenThinkIsNonValueNode_ResolvesToNull()
	{
		// Arrange: a JSON object is neither the boolean nor the string shape the parser understands.
		JsonObject think = new() { ["mode"] = "deep" };

		// Act
		ReasoningResolution result = ReasoningEffortParser.Resolve(think, backendDefault: null);

		// Assert
		Assert.Null(result.Effort);
		Assert.Equal(ReasoningEffortSource.Unspecified, result.Source);
	}
}
