// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Xunit;

namespace OllamaProxy.CustomActions.Tests;

/// <summary>
/// Tests for <see cref="EndpointParseResult"/>, the result carrier returned by
/// <see cref="CustomActions.ParseLocalEndpoint"/>. The struct has two factory methods that establish two
/// mutually exclusive states: a success carrying a host and port, and a failure carrying a message.
/// </summary>
[Trait("Category", "Unit")]
public sealed class EndpointParseResultTests
{
	#region Success()

	/// <summary>
	/// Verifies that <see cref="EndpointParseResult.Success"/> produces a successful result that carries
	/// the host and port and leaves the error message <see langword="null"/>.
	/// </summary>
	[Fact]
	public void Success_WithHostAndPort_SetsSuccessStateAndNoError()
	{
		// Act
		EndpointParseResult result = EndpointParseResult.Success("localhost", 11434);

		// Assert
		AssertParseResult(
			result,
			expectedSuccess: true,
			expectedHost: "localhost",
			expectedPort: 11434,
			expectedErrorMessage: null);
	}

	#endregion

	#region Failure()

	/// <summary>
	/// Verifies that <see cref="EndpointParseResult.Failure"/> produces a failed result that carries the
	/// error message and leaves the host <see langword="null"/> and the port zero.
	/// </summary>
	[Fact]
	public void Failure_WithMessage_SetsFailureStateAndNoHostOrPort()
	{
		// Act
		EndpointParseResult result = EndpointParseResult.Failure("Please enter the Admin panel URL.");

		// Assert
		AssertParseResult(
			result,
			expectedSuccess: false,
			expectedHost: null,
			expectedPort: 0,
			expectedErrorMessage: "Please enter the Admin panel URL.");
	}

	#endregion

	#region Helpers

	/// <summary>
	/// Asserts the complete observable state of an <see cref="EndpointParseResult"/> in one place so
	/// every property is verified consistently across both the success and failure cases.
	/// </summary>
	/// <param name="result">The result under test.</param>
	/// <param name="expectedSuccess">The expected <see cref="EndpointParseResult.IsSuccess"/> value.</param>
	/// <param name="expectedHost">The expected <see cref="EndpointParseResult.Host"/> value.</param>
	/// <param name="expectedPort">The expected <see cref="EndpointParseResult.Port"/> value.</param>
	/// <param name="expectedErrorMessage">The expected <see cref="EndpointParseResult.ErrorMessage"/> value.</param>
	private static void AssertParseResult(
		EndpointParseResult result,
		bool                expectedSuccess,
		string              expectedHost,
		int                 expectedPort,
		string              expectedErrorMessage)
	{
		Assert.Equal(expectedSuccess, result.IsSuccess);
		Assert.Equal(expectedHost, result.Host);
		Assert.Equal(expectedPort, result.Port);
		Assert.Equal(expectedErrorMessage, result.ErrorMessage);
	}

	#endregion
}
