// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Net;

using Xunit;

namespace OllamaProxy.CustomActions.Tests;

public partial class CustomActionsTests
{
	/// <summary>
	/// Tests for the pure helpers behind <see cref="CustomActions.CheckPorts"/>: the endpoint URL parser
	/// that extracts host and port (and rejects malformed input), and the host-to-bind-address mapper. The
	/// <see cref="CustomActions.CheckPorts"/> entry point and the live TCP bind probe are not covered here
	/// because they require an installer <c>Session</c> and a real socket.
	/// </summary>
	/// <remarks>
	/// The DNS fallback in <see cref="CustomActions.ResolveBindAddress"/> (<c>Dns.GetHostAddresses(host)[0]</c>
	/// for a non-loopback, non-wildcard, non-literal host) is intentionally not unit-tested: it is a thin
	/// delegation to the BCL resolver whose result depends on the host's DNS configuration, so any assertion
	/// would either test the framework or be non-deterministic. The three deterministic branches it guards
	/// (loopback alias, wildcard, IP literal) are covered exactly below.
	/// </remarks>
	[Trait("Category", "Unit")]
	public sealed class CheckPorts
	{
		#region ParseLocalEndpoint()

		/// <summary>
		/// Provides valid endpoint URLs and the host/port each must yield, covering an explicit port, the
		/// http and https default ports when the port is omitted, and an IPv4 literal.
		/// </summary>
		public static TheoryData<string, string, string, int> ValidEndpointCases => new()
		{
			{
				"explicit port on a loopback host",
				"http://localhost:11434",
				"localhost",
				11434
			},
			{
				"https default port when omitted",
				"https://api.openai.com/v1",
				"api.openai.com",
				443
			},
			{
				"http default port when omitted",
				"http://localhost",
				"localhost",
				80
			},
			{
				"ipv4 literal with an explicit port",
				"http://127.0.0.1:8080",
				"127.0.0.1",
				8080
			},
			{
				// On net472 Uri.Host returns an IPv6 literal in its bracketed form ("[::1]"); pinned here
				// so a framework change to that form is caught. ResolveBindAddress is verified to accept
				// that same bracketed host in its own theory below.
				"bracketed ipv6 literal with an explicit port",
				"http://[::1]:8080",
				"[::1]",
				8080
			}
		};

		/// <summary>
		/// Verifies that <see cref="CustomActions.ParseLocalEndpoint"/> extracts the host and port from a
		/// valid http/https URL, applying the scheme's default port when none is specified.
		/// </summary>
		/// <param name="scenario">A readable description of the case under test.</param>
		/// <param name="url">The endpoint URL the operator entered.</param>
		/// <param name="expectedHost">The host the parser should extract.</param>
		/// <param name="expectedPort">The port the parser should extract.</param>
		[Theory]
		[MemberData(nameof(ValidEndpointCases))]
		public void ParseLocalEndpoint_WhenUrlValid_ReturnsHostAndPort(
			string scenario,
			string url,
			string expectedHost,
			int    expectedPort)
		{
			_ = scenario;

			// Act
			EndpointParseResult result = CustomActions.ParseLocalEndpoint(url, "Ollama listener");

			// Assert
			AssertParseResult(result, expectedSuccess: true, expectedHost, expectedPort, expectedErrorMessage: null);
		}

		/// <summary>
		/// Provides invalid endpoint URLs paired with the role label and the exact operator-facing message
		/// each must yield. The role is interpolated into every message, so the cases vary it to confirm the
		/// substitution.
		/// </summary>
		/// <remarks>
		/// The "missing host" branch of <see cref="CustomActions.ParseLocalEndpoint"/> is not represented
		/// here: on net472 <see cref="System.Uri.TryCreate(string, System.UriKind, out System.Uri)"/> rejects
		/// an empty-host authority (for example <c>http:///models</c>) before the host check runs, so such
		/// inputs surface the absolute-URL message instead and the host guard is unreachable from this entry
		/// point.
		/// </remarks>
		public static TheoryData<string, string, string, string> InvalidEndpointCases => new()
		{
			{
				"blank URL",
				"   ",
				"Ollama listener",
				"Please enter the Ollama listener URL (for example http://localhost:11434)."
			},
			{
				"relative URL without a scheme (admin role to confirm label interpolation)",
				"api.openai.com/v1",
				"Admin panel",
				"The Admin panel URL must be an absolute http or https URL (for example http://localhost:11434)."
			},
			{
				"absolute URL with a non-http scheme",
				"ftp://example.com",
				"Ollama listener",
				"The Ollama listener URL must be an absolute http or https URL (for example http://localhost:11434)."
			}
		};

		/// <summary>
		/// Verifies that <see cref="CustomActions.ParseLocalEndpoint"/> rejects each malformed URL with the
		/// specific operator-facing message for that failure, with the role label interpolated.
		/// </summary>
		/// <param name="scenario">A readable description of the case under test.</param>
		/// <param name="url">The endpoint URL the operator entered.</param>
		/// <param name="role">The human-readable endpoint role used in the message.</param>
		/// <param name="expectedMessage">The expected error message.</param>
		[Theory]
		[MemberData(nameof(InvalidEndpointCases))]
		public void ParseLocalEndpoint_WhenUrlInvalid_ReturnsFailure(
			string scenario,
			string url,
			string role,
			string expectedMessage)
		{
			_ = scenario;

			// Act
			EndpointParseResult result = CustomActions.ParseLocalEndpoint(url, role);

			// Assert
			AssertParseResult(
				result,
				expectedSuccess: false,
				expectedHost: null,
				expectedPort: 0,
				expectedMessage);
		}

		#endregion

		#region ResolveBindAddress()

		/// <summary>
		/// Provides hosts and the IP address each must map to: the case-insensitive <c>localhost</c> alias to
		/// loopback, the two wildcard indicators to any-interface, and IPv4/IPv6 literals parsed directly.
		/// </summary>
		public static TheoryData<string, string, string> BindAddressCases => new()
		{
			{
				"localhost maps to loopback",
				"localhost",
				"127.0.0.1"
			},
			{
				"localhost is matched case-insensitively",
				"LOCALHOST",
				"127.0.0.1"
			},
			{
				"asterisk wildcard maps to any interface",
				"*",
				"0.0.0.0"
			},
			{
				"plus wildcard maps to any interface",
				"+",
				"0.0.0.0"
			},
			{
				"ipv4 literal is parsed directly",
				"192.168.1.5",
				"192.168.1.5"
			},
			{
				"ipv6 literal is parsed directly",
				"::1",
				"::1"
			},
			{
				// The bracketed form is the real runtime input: ParseLocalEndpoint yields Host="[::1]" for
				// "http://[::1]:8080", and CheckPorts feeds that straight into ResolveBindAddress. Verified
				// on net472 that IPAddress.TryParse accepts the bracketed literal and strips it to ::1, so
				// the bind path resolves correctly rather than falling through to DNS.
				"bracketed ipv6 literal is parsed to the same address",
				"[::1]",
				"::1"
			}
		};

		/// <summary>
		/// Verifies that <see cref="CustomActions.ResolveBindAddress"/> maps the loopback alias, the wildcard
		/// indicators, and IP literals to the correct <see cref="IPAddress"/> without consulting DNS.
		/// </summary>
		/// <param name="scenario">A readable description of the case under test.</param>
		/// <param name="host">The host name or IP literal to resolve.</param>
		/// <param name="expectedIp">The IP address the host should map to.</param>
		[Theory]
		[MemberData(nameof(BindAddressCases))]
		public void ResolveBindAddress_ForKnownHost_ReturnsExpectedAddress(
			string scenario,
			string host,
			string expectedIp)
		{
			_ = scenario;

			// Act
			IPAddress result = CustomActions.ResolveBindAddress(host);

			// Assert
			Assert.Equal(IPAddress.Parse(expectedIp), result);
		}

		#endregion

		#region Helpers

		/// <summary>
		/// Asserts the complete observable state of an <see cref="EndpointParseResult"/> in one place so
		/// every property is verified consistently across the success and failure cases.
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
}
