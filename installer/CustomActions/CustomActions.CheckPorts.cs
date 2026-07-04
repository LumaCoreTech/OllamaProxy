// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

using WixToolset.Dtf.WindowsInstaller;

namespace OllamaProxy.CustomActions;

public static partial class CustomActions
{
	/// <summary>
	/// Checks whether the local endpoints entered on the port-configuration page are valid and available.
	/// Runs immediately when the operator clicks "Check ports". The proxy engine and the admin chassis
	/// refuse to share a port, so this action first validates that the two URLs differ, then attempts a
	/// brief local bind on each port to detect an already-running Ollama instance or another listener.
	/// The verdict is reported through <c>OLLAMAPROXY_PORTRESULT</c> and <c>OLLAMAPROXY_PORTOK</c>.
	/// </summary>
	/// <param name="session">The running installer session exposing the entered properties.</param>
	/// <returns>
	/// <see cref="ActionResult.Success"/> regardless of the test outcome; the verdict travels in the
	/// result properties.
	/// </returns>
	[CustomAction]
	public static ActionResult CheckPorts(Session session)
	{
		if (session == null) throw new ArgumentNullException(nameof(session));

		string listenUrl = (session["OLLAMAPROXY_LISTENURL"] ?? string.Empty).Trim();
		string adminUrl = (session["OLLAMAPROXY_ADMINURL"] ?? string.Empty).Trim();

		session.Log("OllamaProxy: checking local endpoints listen='{0}' admin='{1}'.", listenUrl, adminUrl);

		EndpointParseResult listen = ParseLocalEndpoint(listenUrl, "Ollama listener");
		if (!listen.IsSuccess)
		{
			return ReportPortResult(session, ok: false, message: listen.ErrorMessage);
		}

		EndpointParseResult admin = ParseLocalEndpoint(adminUrl, "Admin panel");
		if (!admin.IsSuccess)
		{
			return ReportPortResult(session, ok: false, message: admin.ErrorMessage);
		}

		if (listen.Port == admin.Port)
		{
			return ReportPortResult(
				session,
				ok: false,
				message: "The Ollama listener and the admin panel must use different ports.");
		}

		string listenError = TryBindEndpoint(listen.Host, listen.Port);
		if (listenError != null)
		{
			return ReportPortResult(
				session,
				ok: false,
				message: string.Format(
					CultureInfo.CurrentCulture,
					"The Ollama listener endpoint ({0}:{1}) is already in use: {2}",
					listen.Host,
					listen.Port,
					listenError));
		}

		string adminError = TryBindEndpoint(admin.Host, admin.Port);
		if (adminError != null)
		{
			return ReportPortResult(
				session,
				ok: false,
				message: string.Format(
					CultureInfo.CurrentCulture,
					"The admin panel endpoint ({0}:{1}) is already in use: {2}",
					admin.Host,
					admin.Port,
					adminError));
		}

		return ReportPortResult(
			session,
			ok: true,
			message: string.Format(
				CultureInfo.CurrentCulture,
				"Both endpoints are available ({0}:{1} and {2}:{3}).",
				listen.Host,
				listen.Port,
				admin.Host,
				admin.Port));
	}

	/// <summary>
	/// Stores the port-check outcome on the session properties the dialog reads and logs it.
	/// </summary>
	/// <param name="session">The installer session whose properties carry the result.</param>
	/// <param name="ok">Whether the ports are available.</param>
	/// <param name="message">The operator-facing message describing the outcome.</param>
	/// <returns>Always <see cref="ActionResult.Success"/>; the verdict travels in the properties.</returns>
	private static ActionResult ReportPortResult(Session session, bool ok, string message)
	{
		session["OLLAMAPROXY_PORTOK"] = ok ? "1" : "0";
		session["OLLAMAPROXY_PORTRESULT"] = message;
		session.Log("OllamaProxy: port check {0}: {1}", ok ? "succeeded" : "failed", message);

		return ActionResult.Success;
	}

	/// <summary>
	/// Parses an endpoint URL and extracts the host and port. Only <c>http</c> and <c>https</c>
	/// schemes are accepted; the host must be present. The installer deals in full URLs so operators
	/// can bind other addresses, but for the local availability probe we focus on the host and port.
	/// Default ports (80 for http, 443 for https) are returned when the URL omits an explicit port.
	/// </summary>
	/// <param name="url">The URL entered by the operator.</param>
	/// <param name="role">A human-readable name for the endpoint (used in error messages).</param>
	/// <returns>The parse result carrying either the host and port or an error message.</returns>
	// internal (not private): exercised directly by the Windows-only custom-action test project.
	internal static EndpointParseResult ParseLocalEndpoint(string url, string role)
	{
		if (string.IsNullOrWhiteSpace(url))
		{
			return EndpointParseResult.Failure(
				string.Format(
					CultureInfo.CurrentCulture,
					"Please enter the {0} URL (for example http://localhost:11434).",
					role));
		}

		if (!Uri.TryCreate(url, UriKind.Absolute, out Uri parsed) ||
		    (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
		{
			return EndpointParseResult.Failure(
				string.Format(
					CultureInfo.CurrentCulture,
					"The {0} URL must be an absolute http or https URL (for example http://localhost:11434).",
					role));
		}

		// Defensive guard, deliberately kept although effectively unreachable on net472: Uri.TryCreate
		// above already rejects an empty-host http/https authority (for example "http:///models") and
		// routes it to the absolute-URL message — verified against the net472 Uri parser for a range of
		// empty-host inputs in this work. Uri host handling is not contractually guaranteed to behave
		// identically on other runtimes or future framework versions, and a missing host must never fall
		// through to a bind attempt, so the check stays. (The net472 test project consequently cannot
		// cover this line.)
		if (string.IsNullOrEmpty(parsed.Host))
		{
			return EndpointParseResult.Failure(
				string.Format(
					CultureInfo.CurrentCulture,
					"The {0} URL must include a host (for example http://localhost:11434).",
					role));
		}

		return EndpointParseResult.Success(parsed.Host, parsed.Port);
	}

	/// <summary>
	/// Attempts a brief TCP bind on the specified endpoint to detect an existing listener. The socket
	/// is closed immediately after the check. Loopback hosts bind on loopback; wildcard hosts
	/// (<c>0.0.0.0</c>, <c>*</c>, <c>+</c>) bind on any interface; everything else is resolved and
	/// bound on the resulting address. Returns <see langword="null"/> when the endpoint is available,
	/// otherwise a short error message.
	/// </summary>
	/// <param name="host">The host name or IP address literal from the endpoint URL.</param>
	/// <param name="port">The port to probe.</param>
	/// <returns><see langword="null"/> if the endpoint is available; otherwise an error message.</returns>
	/// <exception cref="ArgumentNullException">
	/// <paramref name="host"/> is <see langword="null"/>.
	/// </exception>
	private static string TryBindEndpoint(string host, int port)
	{
		IPAddress address;
		try
		{
			address = ResolveBindAddress(host);
		}
		catch (SocketException exception)
		{
			return string.Format(
				CultureInfo.CurrentCulture,
				"the host '{0}' could not be resolved ({1}).",
				host,
				exception.SocketErrorCode);
		}

		try
		{
			using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
			socket.Bind(new IPEndPoint(address, port));
			return null;
		}
		catch (SocketException exception) when (exception.SocketErrorCode == SocketError.AccessDenied)
		{
			return "another process is already listening here (access denied).";
		}
		catch (SocketException exception) when (exception.SocketErrorCode == SocketError.AddressAlreadyInUse)
		{
			return "another process is already listening on this port.";
		}
		catch (SocketException exception)
		{
			return "could not bind to the port: " + exception.Message;
		}
	}

	/// <summary>
	/// Maps a host name from an endpoint URL to the IP address that the availability probe should
	/// bind on. <c>localhost</c> maps to loopback, wildcard indicators (<c>*</c>, <c>+</c>) probe any
	/// interface, IP literals are parsed directly, and everything else is resolved through DNS.
	/// </summary>
	/// <param name="host">The host name or IP address literal.</param>
	/// <returns>The <see cref="IPAddress"/> to bind.</returns>
	/// <exception cref="ArgumentNullException">
	/// <paramref name="host"/> is <see langword="null"/>.
	/// </exception>
	/// <exception cref="SocketException">
	/// The host name could not be resolved by DNS.
	/// </exception>
	// internal (not private): exercised directly by the Windows-only custom-action test project.
	internal static IPAddress ResolveBindAddress(string host)
	{
		if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
		{
			return IPAddress.Loopback;
		}

		if (string.Equals(host, "*", StringComparison.OrdinalIgnoreCase) ||
		    string.Equals(host, "+", StringComparison.OrdinalIgnoreCase))
		{
			return IPAddress.Any;
		}

		if (IPAddress.TryParse(host, out IPAddress parsed))
		{
			return parsed;
		}

		return Dns.GetHostAddresses(host)[0];
	}
}
