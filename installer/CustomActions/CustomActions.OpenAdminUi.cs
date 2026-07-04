// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System;
using System.Diagnostics;

using WixToolset.Dtf.WindowsInstaller;

namespace OllamaProxy.CustomActions;

public static partial class CustomActions
{
	/// <summary>
	/// Opens the admin UI in the operator's default browser at the end of a successful install. Runs
	/// immediate in the UI sequence, only when a full UI is present, so silent installs are not
	/// disturbed. The service has just been started; the browser may load slightly before the admin
	/// host is ready, but a short retry inside the browser is the least intrusive way to handle that.
	/// An admin URL that is not an absolute http/https address is skipped rather than handed to the
	/// shell, so a mistyped value cannot invoke an unintended handler.
	/// </summary>
	/// <param name="session">The running installer session exposing the admin URL property.</param>
	/// <returns>
	/// <see cref="ActionResult.Success"/> even if the browser could not be started: a failure here
	/// must not roll back an otherwise successful installation.
	/// </returns>
	[CustomAction]
	public static ActionResult OpenAdminUi(Session session)
	{
		if (session == null) throw new ArgumentNullException(nameof(session));

		string adminUrl = (session["OLLAMAPROXY_ADMINURL"] ?? string.Empty).Trim();
		if (string.IsNullOrEmpty(adminUrl))
		{
			session.Log("OllamaProxy: no admin URL configured; skipping browser launch.");
			return ActionResult.Success;
		}

		// The admin URL is operator-entered, and Process.Start hands an arbitrary string to ShellExecute
		// (which would act on a file path or a foreign URI scheme). Restrict the launch to an absolute
		// http/https URL so a mistyped value is skipped rather than invoking an unintended handler.
		if (!TryGetLaunchableAdminUrl(adminUrl, out Uri parsed))
		{
			session.Log(
				"OllamaProxy: admin URL '{0}' is not an absolute http/https URL; skipping browser launch.",
				adminUrl);
			return ActionResult.Success;
		}

		session.Log("OllamaProxy: opening admin UI at '{0}'.", parsed.AbsoluteUri);

		try
		{
			Process.Start(parsed.AbsoluteUri);
		}
		catch (Exception exception)
		{
			// Do not fail the install because a browser could not be launched.
			session.Log("OllamaProxy: could not open browser: {0}", exception.Message);
		}

		return ActionResult.Success;
	}

	/// <summary>
	/// Determines whether the operator-entered admin URL is safe to hand to the shell for a browser
	/// launch, accepting only an absolute <c>http</c>/<c>https</c> URL.
	/// <see cref="Process.Start(string)"/> forwards an arbitrary string to ShellExecute (which would act
	/// on a file path or a foreign URI scheme), so a mistyped or non-web value must be rejected rather
	/// than invoking an unintended handler. A blank value is also rejected here (the caller logs that
	/// case separately before reaching this gate).
	/// </summary>
	/// <param name="adminUrl">The trimmed admin URL the operator entered.</param>
	/// <param name="launchUri">
	/// When this method returns <see langword="true"/>, the parsed absolute http/https URI to launch;
	/// otherwise <see langword="null"/>.
	/// </param>
	/// <returns>
	/// <see langword="true"/> when <paramref name="adminUrl"/> is an absolute http/https URL and
	/// <paramref name="launchUri"/> has been set; otherwise <see langword="false"/>.
	/// </returns>
	// internal (not private): exercised directly by the Windows-only custom-action test project.
	internal static bool TryGetLaunchableAdminUrl(string adminUrl, out Uri launchUri)
	{
		if (Uri.TryCreate(adminUrl, UriKind.Absolute, out Uri parsed) &&
		    (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
		{
			launchUri = parsed;
			return true;
		}

		launchUri = null;
		return false;
	}
}
