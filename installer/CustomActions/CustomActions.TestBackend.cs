// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;

using WixToolset.Dtf.WindowsInstaller;

namespace OllamaProxy.CustomActions;

public static partial class CustomActions
{
	/// <summary>
	/// The minimum API-key length the proxy accepts, mirrored from
	/// <c>OllamaProxy.Configuration.BackendOptions.MinimumApiKeyLength</c> so the installer rejects an
	/// obviously truncated secret with the same threshold the service would fail startup on. Keep this
	/// in sync with that constant.
	/// </summary>
	private const int MinimumApiKeyLength = 8;

	/// <summary>
	/// The maximum time a single backend connectivity probe is allowed to take. Kept short so a
	/// misconfigured or unreachable endpoint fails the dialog quickly instead of hanging setup.
	/// </summary>
	private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

	/// <summary>
	/// Validates the backend URL and key entered in the configuration dialog and probes the endpoint
	/// for reachability. Runs immediately (in the UI sequence) when the operator clicks "Test
	/// connection", and reports the outcome back to the dialog through the
	/// <c>OLLAMAPROXY_TESTRESULT</c> property (a human-readable message) and <c>OLLAMAPROXY_TESTOK</c>
	/// ("1" on success, "0" otherwise), so the UI can show the result without blocking the install.
	/// </summary>
	/// <param name="session">The running installer session exposing the entered properties.</param>
	/// <returns>
	/// <see cref="ActionResult.Success"/> regardless of the test outcome: a failed probe is a
	/// user-correctable condition surfaced through the result properties, not an installer error.
	/// </returns>
	[CustomAction]
	public static ActionResult TestBackend(Session session)
	{
		if (session == null) throw new ArgumentNullException(nameof(session));

		string baseUrl = (session["OLLAMAPROXY_BASEURL"] ?? string.Empty).Trim();
		string apiKey = session["OLLAMAPROXY_APIKEY"] ?? string.Empty;

		session.Log("OllamaProxy: testing backend connectivity to '{0}'.", baseUrl);

		string syntaxError = ValidateSyntax(baseUrl, apiKey);
		if (syntaxError != null)
		{
			return ReportTestResult(session, ok: false, message: syntaxError);
		}

		TestOutcome outcome = ProbeBackend(baseUrl, apiKey);
		return ReportTestResult(session, outcome.Ok, outcome.Message);
	}

	/// <summary>
	/// Applies the proxy's own syntactic rules to the entered values so the dialog rejects the same
	/// inputs the service would reject at startup: a non-blank, absolute URL and a key of at least
	/// <see cref="MinimumApiKeyLength"/> characters.
	/// </summary>
	/// <param name="baseUrl">The backend base URL the operator entered.</param>
	/// <param name="apiKey">The API key the operator entered.</param>
	/// <returns>A human-readable error message, or <see langword="null"/> when the syntax is valid.</returns>
	// internal (not private): exercised directly by the Windows-only custom-action test project.
	internal static string ValidateSyntax(string baseUrl, string apiKey)
	{
		if (string.IsNullOrWhiteSpace(baseUrl))
		{
			return "Please enter the backend base URL (for example https://api.openai.com/v1).";
		}

		if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri parsed) ||
		    (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
		{
			return "The backend base URL must be an absolute http or https URL " +
			       "(for example https://api.openai.com/v1).";
		}

		if (string.IsNullOrEmpty(apiKey))
		{
			return "Please enter the backend API key.";
		}

		if (apiKey.Length < MinimumApiKeyLength)
		{
			return string.Format(
				CultureInfo.CurrentCulture,
				"The API key must be at least {0} characters long.",
				MinimumApiKeyLength);
		}

		return null;
	}

	/// <summary>
	/// Issues a single, short-lived <c>GET {baseUrl}/models</c> with the supplied bearer token and
	/// maps the result to an operator-facing verdict. A 2xx confirms the endpoint; a 401/403 points at
	/// the key; a 404 most often means the base URL is missing its <c>/v1</c> (or equivalent) suffix;
	/// anything else, including a transport failure, is reported as unreachable.
	/// </summary>
	/// <param name="baseUrl">The validated, absolute backend base URL.</param>
	/// <param name="apiKey">The bearer token to authenticate the probe with.</param>
	/// <returns>The probe outcome carrying the success flag and a message.</returns>
	private static TestOutcome ProbeBackend(string baseUrl, string apiKey)
	{
		// .NET Framework 4.7.2 can default to older protocols; opt into TLS 1.2 so HTTPS backends are
		// reachable. (TLS 1.3 has no SecurityProtocolType value on net472, so 1.2 is the practical floor.)
		ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

		string modelsUrl = baseUrl.TrimEnd('/') + "/models";

		try
		{
			using var client = new HttpClient();
			client.Timeout = ProbeTimeout;
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

			using HttpResponseMessage response = client
				.GetAsync(modelsUrl, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None)
				.GetAwaiter()
				.GetResult();
			return InterpretResponse(response.StatusCode);
		}
		catch (Exception exception) when (exception is HttpRequestException ||
		                                  exception is OperationCanceledException)
		{
			return new TestOutcome(
				false,
				"Could not reach the backend: " + Innermost(exception).Message +
				"  Check the URL and your network connection.");
		}
	}

	/// <summary>
	/// Maps an HTTP status code from the probe to an operator-facing outcome, calling out the two
	/// misconfigurations a non-developer hits most often: a wrong key and a base URL missing its
	/// version segment.
	/// </summary>
	/// <param name="status">The status code the backend returned.</param>
	/// <returns>The interpreted outcome.</returns>
	// internal (not private): exercised directly by the Windows-only custom-action test project.
	internal static TestOutcome InterpretResponse(HttpStatusCode status)
	{
		int code = (int)status;

		if (code is >= 200 and < 300)
		{
			return new TestOutcome(true, "Success: the backend is reachable and the API key was accepted.");
		}

		if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
		{
			return new TestOutcome(
				false,
				"The backend rejected the API key (HTTP " + code + "). Check the key and try again.");
		}

		if (status == HttpStatusCode.NotFound)
		{
			return new TestOutcome(
				false,
				"The backend returned HTTP 404 for the models endpoint. The base URL is most likely " +
				"missing its version segment — for example it should end in '/v1'.");
		}

		return new TestOutcome(
			false,
			"The backend responded with HTTP " + code + ". Verify the base URL points at an " +
			"OpenAI-compatible API.");
	}

	/// <summary>
	/// Stores the test outcome on the session properties the dialog reads and logs it.
	/// </summary>
	/// <param name="session">The installer session whose properties carry the result.</param>
	/// <param name="ok">Whether the backend test succeeded.</param>
	/// <param name="message">The operator-facing message describing the outcome.</param>
	/// <returns>Always <see cref="ActionResult.Success"/>; the verdict travels in the properties.</returns>
	private static ActionResult ReportTestResult(Session session, bool ok, string message)
	{
		session["OLLAMAPROXY_TESTOK"] = ok ? "1" : "0";
		session["OLLAMAPROXY_TESTRESULT"] = message;
		session.Log("OllamaProxy: backend test {0}: {1}", ok ? "succeeded" : "failed", message);

		return ActionResult.Success;
	}

	/// <summary>
	/// Walks to the innermost exception so the operator sees the root transport cause rather than a
	/// wrapper message.
	/// </summary>
	/// <param name="exception">The exception to unwrap.</param>
	/// <returns>The innermost exception.</returns>
	private static Exception Innermost(Exception exception)
	{
		while (exception.InnerException != null) exception = exception.InnerException;
		return exception;
	}
}
