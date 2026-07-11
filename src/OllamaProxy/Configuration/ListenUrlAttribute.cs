// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.ComponentModel.DataAnnotations;
using System.Net;

namespace OllamaProxy.Configuration;

/// <summary>
/// Validates that a Kestrel listen (bind) address names a <em>specific</em> interface rather than an
/// unresolved DNS host name. Kestrel does not resolve the host part of a bind URL: <c>localhost</c>, an IP
/// literal, and the explicit wildcards (<c>0.0.0.0</c>, <c>[::]</c>, <c>*</c>, <c>+</c>) are honored verbatim,
/// but any other host name is silently treated as "bind every interface". Rejecting such a value up front
/// turns a quiet over-exposure — an operator typing <c>http://my-server:11434</c> expecting a single
/// interface yet binding all of them — into a fail-fast configuration error.
/// </summary>
/// <remarks>
/// The value is parsed with <see cref="BindingAddress"/>, the same parser Kestrel uses to interpret the
/// address, so validation and the eventual bind agree exactly. A <see langword="null"/> value passes: the
/// companion <see cref="RequiredAttribute"/> owns the presence check.
/// </remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class ListenUrlAttribute : ValidationAttribute
{
	/// <summary>
	/// Validates <paramref name="value"/> as a Kestrel listen address, accepting only host forms that bind a
	/// specific, operator-intended interface (loopback, IP literal, or explicit wildcard).
	/// </summary>
	/// <param name="value">The property value to validate; expected to be a listen-URL <see cref="string"/>.</param>
	/// <param name="validationContext">The context carrying the member name for a path-qualified failure.</param>
	/// <returns>
	/// <see cref="ValidationResult.Success"/> when the value is <see langword="null"/> (deferred to
	/// <see cref="RequiredAttribute"/>) or names a specific interface; otherwise a failure describing why the
	/// address is rejected.
	/// </returns>
	protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
	{
		// A null/absent value is the RequiredAttribute's concern, not this rule's.
		if (value is null)
		{
			return ValidationResult.Success;
		}

		string name = validationContext.DisplayName;

		if (value is not string text || string.IsNullOrWhiteSpace(text))
		{
			return Fail($"The {name} field must be an absolute http/https URL.", validationContext);
		}

		BindingAddress address;
		try
		{
			address = BindingAddress.Parse(text);
		}
		catch (Exception ex) when (ex is FormatException or ArgumentException)
		{
			return Fail($"The {name} field '{text}' is not a valid listen URL.", validationContext);
		}

		if (!IsHttpScheme(address.Scheme))
		{
			return Fail($"The {name} field '{text}' must use the http or https scheme.", validationContext);
		}

		if (IsInterfaceBinding(address.Host))
		{
			return ValidationResult.Success;
		}

		return Fail(
			$"The {name} field '{text}' uses the DNS host name '{address.Host}', which Kestrel does not " +
			"resolve — it would bind every network interface instead. Use 'localhost', an IP literal (for " +
			"example 127.0.0.1), or an explicit wildcard (0.0.0.0, [::], * or +) to state the intended interface.",
			validationContext);
	}

	/// <summary>
	/// Determines whether <paramref name="scheme"/> is <c>http</c> or <c>https</c> (case-insensitive).
	/// </summary>
	/// <param name="scheme">The scheme parsed from the listen address.</param>
	/// <returns><see langword="true"/> for an http/https scheme; otherwise <see langword="false"/>.</returns>
	private static bool IsHttpScheme(string scheme) =>
		string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
		string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Determines whether <paramref name="host"/> binds a specific, operator-intended interface: an explicit
	/// Kestrel wildcard, loopback, or an IP literal. Any other value is an unresolved DNS name.
	/// </summary>
	/// <param name="host">The host component parsed from the listen address.</param>
	/// <returns><see langword="true"/> for a safe, interface-specific host; otherwise <see langword="false"/>.</returns>
	private static bool IsInterfaceBinding(string host)
	{
		// Explicit Kestrel wildcards: the operator clearly opted into binding every interface.
		if (host is "*" or "+")
		{
			return true;
		}

		// Loopback: the one DNS name Kestrel resolves deterministically (to 127.0.0.1 and [::1]).
		if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		// IP literals — including 0.0.0.0 and [::]. BindingAddress keeps IPv6 in [ ] brackets, so strip them
		// before parsing.
		string candidate = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
		return IPAddress.TryParse(candidate, out var _);
	}

	/// <summary>
	/// Builds a <see cref="ValidationResult"/> whose member name points at the offending property so the
	/// failure is path-qualified rather than a bare message.
	/// </summary>
	/// <param name="message">The error message to report.</param>
	/// <param name="validationContext">The context carrying the member name.</param>
	/// <returns>The failure result, member-qualified when a member name is available.</returns>
	private static ValidationResult Fail(string message, ValidationContext validationContext) =>
		validationContext.MemberName is { } member
			? new ValidationResult(message, [member])
			: new ValidationResult(message);
}
