// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Xunit;

namespace OllamaProxy.CustomActions.Tests;

/// <summary>
/// Tests for <see cref="TestOutcome"/>, the immutable result carrier returned by the backend probe and
/// its interpretation. The struct's single constructor stores a success flag and an operator-facing
/// message verbatim.
/// </summary>
[Trait("Category", "Unit")]
public sealed class TestOutcomeTests
{
	/// <summary>
	/// Verifies that the constructor stores a successful outcome's flag and message verbatim.
	/// </summary>
	[Fact]
	public void Constructor_WhenOk_StoresFlagAndMessage()
	{
		// Act
		var outcome = new TestOutcome(true, "Success: the backend is reachable and the API key was accepted.");

		// Assert
		Assert.True(outcome.Ok);
		Assert.Equal("Success: the backend is reachable and the API key was accepted.", outcome.Message);
	}

	/// <summary>
	/// Verifies that the constructor stores a failed outcome's flag and message verbatim.
	/// </summary>
	[Fact]
	public void Constructor_WhenNotOk_StoresFlagAndMessage()
	{
		// Act
		var outcome = new TestOutcome(
			false,
			"The backend rejected the API key (HTTP 401). Check the key and try again.");

		// Assert
		Assert.False(outcome.Ok);
		Assert.Equal("The backend rejected the API key (HTTP 401). Check the key and try again.", outcome.Message);
	}
}
