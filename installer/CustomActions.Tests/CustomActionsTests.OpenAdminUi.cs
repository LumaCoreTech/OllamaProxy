// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System;

using Xunit;

namespace OllamaProxy.CustomActions.Tests;

public partial class CustomActionsTests
{
	/// <summary>
	/// Tests for the pure helper behind <see cref="CustomActions.OpenAdminUi"/>: the launch gate
	/// (<see cref="CustomActions.TryGetLaunchableAdminUrl"/>) that admits only an absolute http/https URL
	/// so a mistyped or hostile value is skipped rather than handed to the shell. The
	/// <see cref="CustomActions.OpenAdminUi"/> entry point itself is not covered here because it is bound
	/// to a live installer <c>Session</c> and starts the operator's browser through
	/// <see cref="System.Diagnostics.Process.Start(string)"/>.
	/// </summary>
	[Trait("Category", "Unit")]
	public sealed class OpenAdminUi
	{
		/// <summary>
		/// Provides launchable admin URLs and the exact <see cref="Uri.AbsoluteUri"/> the gate must hand to
		/// the shell, covering both schemes and confirming the authority-only form is normalized with the
		/// trailing slash that <see cref="System.Diagnostics.Process.Start(string)"/> ultimately receives.
		/// </summary>
		public static TheoryData<string, string, string> LaunchableUrlCases => new()
		{
			{
				"http scheme with host and port is normalized with a trailing slash",
				"http://localhost:11435",
				"http://localhost:11435/"
			},
			{
				"https scheme with an explicit path is preserved verbatim",
				"https://admin.example.com/admin",
				"https://admin.example.com/admin"
			}
		};

		/// <summary>
		/// Verifies that <see cref="CustomActions.TryGetLaunchableAdminUrl"/> accepts an absolute http/https
		/// URL, returning <see langword="true"/> and the parsed URI the browser launch will use.
		/// </summary>
		/// <param name="scenario">A readable description of the case under test.</param>
		/// <param name="adminUrl">The admin URL the operator entered.</param>
		/// <param name="expectedAbsoluteUri">The <see cref="Uri.AbsoluteUri"/> the gate should yield.</param>
		[Theory]
		[MemberData(nameof(LaunchableUrlCases))]
		public void TryGetLaunchableAdminUrl_WhenUrlIsAbsoluteHttpOrHttps_ReturnsTrueWithUri(
			string scenario,
			string adminUrl,
			string expectedAbsoluteUri)
		{
			_ = scenario;

			// Act
			bool result = CustomActions.TryGetLaunchableAdminUrl(adminUrl, out Uri launchUri);

			// Assert
			AssertLaunchGate(result, launchUri, expectedSuccess: true, expectedAbsoluteUri);
		}

		/// <summary>
		/// Provides admin URLs the gate must reject. Beyond blank and relative inputs, the non-http schemes
		/// are the security-relevant cases: <c>file://</c> would otherwise hand an executable path to
		/// ShellExecute and <c>javascript:</c> a script URI, so both must be skipped rather than launched.
		/// </summary>
		public static TheoryData<string, string> NonLaunchableUrlCases => new()
		{
			// Blank inputs: the action logs these separately before the gate, but the gate is defensive too.
			{ "empty string", "" },
			{ "whitespace only", "   " },
			// Relative input: no scheme, so it is not an absolute URL.
			{ "relative URL without a scheme", "admin.example.com/admin" },
			// Non-http schemes: parsed as absolute URIs but rejected by the scheme check.
			{ "absolute URL with the ftp scheme", "ftp://example.com" },
			{
				"absolute URL with the file scheme (would hand an executable path to the shell)",
				"file:///C:/Windows/System32/calc.exe"
			},
			{ "absolute URL with the javascript scheme (a script URI)", "javascript:alert(1)" }
		};

		/// <summary>
		/// Verifies that <see cref="CustomActions.TryGetLaunchableAdminUrl"/> rejects a blank, relative, or
		/// non-http/https URL, returning <see langword="false"/> and a <see langword="null"/> URI so the
		/// caller skips the launch.
		/// </summary>
		/// <param name="scenario">A readable description of the case under test.</param>
		/// <param name="adminUrl">The admin URL the operator entered.</param>
		[Theory]
		[MemberData(nameof(NonLaunchableUrlCases))]
		public void TryGetLaunchableAdminUrl_WhenUrlNotLaunchable_ReturnsFalseWithNullUri(
			string scenario,
			string adminUrl)
		{
			_ = scenario;

			// Act
			bool result = CustomActions.TryGetLaunchableAdminUrl(adminUrl, out Uri launchUri);

			// Assert
			AssertLaunchGate(result, launchUri, expectedSuccess: false, expectedAbsoluteUri: null);
		}

		/// <summary>
		/// Asserts the complete observable state of a gate call in one place: the boolean verdict and the
		/// out parameter, which must carry the launch URI on success and be <see langword="null"/> on
		/// rejection so a skipped launch can never dereference a stale value.
		/// </summary>
		/// <param name="result">The boolean the gate returned.</param>
		/// <param name="launchUri">The URI the gate produced through its out parameter.</param>
		/// <param name="expectedSuccess">The expected boolean verdict.</param>
		/// <param name="expectedAbsoluteUri">
		/// The expected <see cref="Uri.AbsoluteUri"/> when <paramref name="expectedSuccess"/> is
		/// <see langword="true"/>; ignored (and the URI asserted <see langword="null"/>) otherwise.
		/// </param>
		private static void AssertLaunchGate(
			bool   result,
			Uri    launchUri,
			bool   expectedSuccess,
			string expectedAbsoluteUri)
		{
			Assert.Equal(expectedSuccess, result);

			if (expectedSuccess)
			{
				Assert.NotNull(launchUri);
				Assert.Equal(expectedAbsoluteUri, launchUri.AbsoluteUri);
			}
			else
			{
				Assert.Null(launchUri);
			}
		}
	}
}
