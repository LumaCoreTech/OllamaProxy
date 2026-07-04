// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Net;

using Xunit;

namespace OllamaProxy.CustomActions.Tests;

public partial class CustomActionsTests
{
	/// <summary>
	/// Tests for the pure helpers behind <see cref="CustomActions.TestBackend"/>: the syntactic input
	/// validation the dialog applies before probing, and the HTTP-status interpretation that turns a probe
	/// response into an operator-facing verdict. The <see cref="CustomActions.TestBackend"/> entry point and
	/// the live network probe are not covered here because they require an installer <c>Session</c> and a
	/// reachable endpoint.
	/// </summary>
	[Trait("Category", "Unit")]
	public sealed class TestBackend
	{
		#region ValidateSyntax()

		/// <summary>
		/// Verifies that <see cref="CustomActions.ValidateSyntax"/> accepts an absolute http/https URL paired
		/// with a key at or above the minimum length, including the exact boundary length, returning
		/// <see langword="null"/> to signal valid syntax.
		/// </summary>
		/// <param name="scenario">A readable description of the case under test.</param>
		/// <param name="baseUrl">The backend base URL the operator entered.</param>
		/// <param name="apiKey">The API key the operator entered.</param>
		[Theory]
		[InlineData("https scheme with a long key", "https://api.openai.com/v1", "secret-key-1234")]
		[InlineData("http scheme with a long key", "http://localhost:11434/v1", "secret-key-1234")]
		[InlineData("key exactly at the 8-character minimum", "https://api.openai.com/v1", "12345678")]
		public void ValidateSyntax_WhenInputValid_ReturnsNull(string scenario, string baseUrl, string apiKey)
		{
			_ = scenario;

			// Act
			string result = CustomActions.ValidateSyntax(baseUrl, apiKey);

			// Assert
			Assert.Null(result);
		}

		/// <summary>
		/// Provides invalid (baseUrl, apiKey) pairs and the exact operator-facing message each must yield.
		/// The URL is validated before the key, so each row isolates a single failure by keeping the other
		/// field valid.
		/// </summary>
		public static TheoryData<string, string, string, string> InvalidSyntaxCases => new()
		{
			// --- URL failures (key kept valid) ---
			{
				"blank URL",
				"   ",
				"secret-key-1234",
				"Please enter the backend base URL (for example https://api.openai.com/v1)."
			},
			{
				"relative URL without a scheme",
				"api.openai.com/v1",
				"secret-key-1234",
				"The backend base URL must be an absolute http or https URL (for example https://api.openai.com/v1)."
			},
			{
				"absolute URL with a non-http scheme",
				"ftp://example.com/v1",
				"secret-key-1234",
				"The backend base URL must be an absolute http or https URL (for example https://api.openai.com/v1)."
			},
			// --- Key failures (URL kept valid) ---
			{
				"empty key",
				"https://api.openai.com/v1",
				"",
				"Please enter the backend API key."
			},
			{
				"key one character below the minimum",
				"https://api.openai.com/v1",
				"1234567",
				"The API key must be at least 8 characters long."
			}
		};

		/// <summary>
		/// Verifies that <see cref="CustomActions.ValidateSyntax"/> rejects each invalid input with the
		/// specific operator-facing message for that failure.
		/// </summary>
		/// <param name="scenario">A readable description of the case under test.</param>
		/// <param name="baseUrl">The backend base URL the operator entered.</param>
		/// <param name="apiKey">The API key the operator entered.</param>
		/// <param name="expected">The expected error message.</param>
		[Theory]
		[MemberData(nameof(InvalidSyntaxCases))]
		public void ValidateSyntax_WhenInputInvalid_ReturnsErrorMessage(
			string scenario,
			string baseUrl,
			string apiKey,
			string expected)
		{
			_ = scenario;

			// Act
			string result = CustomActions.ValidateSyntax(baseUrl, apiKey);

			// Assert
			Assert.Equal(expected, result);
		}

		#endregion

		#region InterpretResponse()

		/// <summary>
		/// Verifies that any 2xx status is interpreted as a successful, accepted-key outcome.
		/// </summary>
		/// <param name="scenario">A readable description of the case under test.</param>
		/// <param name="status">The 2xx status code the backend returned.</param>
		[Theory]
		[InlineData("200 OK", HttpStatusCode.OK)]
		[InlineData("201 Created", HttpStatusCode.Created)]
		[InlineData("202 Accepted", HttpStatusCode.Accepted)]
		[InlineData("204 No Content", HttpStatusCode.NoContent)]
		public void InterpretResponse_WhenStatusSuccess_ReturnsOk(string scenario, HttpStatusCode status)
		{
			_ = scenario;

			// Act
			TestOutcome outcome = CustomActions.InterpretResponse(status);

			// Assert
			AssertOutcome(outcome, expectedOk: true, "Success: the backend is reachable and the API key was accepted.");
		}

		/// <summary>
		/// Verifies that a 401 or 403 is interpreted as a rejected key, echoing the exact status code in the
		/// message so the operator can tell the two apart.
		/// </summary>
		/// <param name="scenario">A readable description of the case under test.</param>
		/// <param name="status">The auth-failure status code the backend returned.</param>
		/// <param name="expectedMessage">The expected operator-facing message.</param>
		[Theory]
		[InlineData(
			"401 Unauthorized",
			HttpStatusCode.Unauthorized,
			"The backend rejected the API key (HTTP 401). Check the key and try again.")]
		[InlineData(
			"403 Forbidden",
			HttpStatusCode.Forbidden,
			"The backend rejected the API key (HTTP 403). Check the key and try again.")]
		public void InterpretResponse_WhenUnauthorizedOrForbidden_ReturnsKeyRejected(
			string         scenario,
			HttpStatusCode status,
			string         expectedMessage)
		{
			_ = scenario;

			// Act
			TestOutcome outcome = CustomActions.InterpretResponse(status);

			// Assert
			AssertOutcome(outcome, expectedOk: false, expectedMessage);
		}

		/// <summary>
		/// Verifies that a 404 is interpreted as a missing version segment on the base URL — the single
		/// misconfiguration this maps specially, distinct from the generic failure path.
		/// </summary>
		[Fact]
		public void InterpretResponse_WhenNotFound_ReturnsVersionSegmentHint()
		{
			// Act
			TestOutcome outcome = CustomActions.InterpretResponse(HttpStatusCode.NotFound);

			// Assert
			AssertOutcome(
				outcome,
				expectedOk: false,
				"The backend returned HTTP 404 for the models endpoint. The base URL is most likely " +
				"missing its version segment — for example it should end in '/v1'.");
		}

		/// <summary>
		/// Verifies that any other non-success status falls through to the generic failure message, echoing
		/// the status code so the operator can diagnose it.
		/// </summary>
		/// <param name="scenario">A readable description of the case under test.</param>
		/// <param name="status">The status code the backend returned.</param>
		/// <param name="expectedMessage">The expected operator-facing message.</param>
		[Theory]
		[InlineData(
			"302 Found (a 3xx redirect is not a success and is not specially mapped)",
			HttpStatusCode.Found,
			"The backend responded with HTTP 302. Verify the base URL points at an OpenAI-compatible API.")]
		[InlineData(
			"400 Bad Request",
			HttpStatusCode.BadRequest,
			"The backend responded with HTTP 400. Verify the base URL points at an OpenAI-compatible API.")]
		[InlineData(
			"500 Internal Server Error",
			HttpStatusCode.InternalServerError,
			"The backend responded with HTTP 500. Verify the base URL points at an OpenAI-compatible API.")]
		[InlineData(
			"502 Bad Gateway",
			HttpStatusCode.BadGateway,
			"The backend responded with HTTP 502. Verify the base URL points at an OpenAI-compatible API.")]
		public void InterpretResponse_WhenOtherStatus_ReturnsGenericFailure(
			string         scenario,
			HttpStatusCode status,
			string         expectedMessage)
		{
			_ = scenario;

			// Act
			TestOutcome outcome = CustomActions.InterpretResponse(status);

			// Assert
			AssertOutcome(outcome, expectedOk: false, expectedMessage);
		}

		/// <summary>
		/// Asserts the complete observable state of a <see cref="TestOutcome"/> in one place so both the
		/// success flag and the operator-facing message are verified consistently across every case.
		/// </summary>
		/// <param name="outcome">The outcome under test.</param>
		/// <param name="expectedOk">The expected <see cref="TestOutcome.Ok"/> value.</param>
		/// <param name="expectedMessage">The expected <see cref="TestOutcome.Message"/> value.</param>
		private static void AssertOutcome(TestOutcome outcome, bool expectedOk, string expectedMessage)
		{
			Assert.Equal(expectedOk, outcome.Ok);
			Assert.Equal(expectedMessage, outcome.Message);
		}

		#endregion
	}
}
