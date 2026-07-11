// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.ComponentModel.DataAnnotations;

using Microsoft.AspNetCore.Http;

using OllamaProxy.Configuration;

namespace OllamaProxy.Tests.Configuration;

/// <summary>
/// Tests for <see cref="ListenUrlAttribute"/>, the fail-fast guard that keeps a Kestrel listen address from
/// silently binding every interface. The story runs from the addresses that name a specific,
/// operator-intended interface (loopback, IP literals, explicit wildcards) through the ones that must be
/// rejected: an unresolved DNS host name (the security-relevant case), a non-http scheme, and a malformed or
/// blank value. A <see langword="null"/> value passes because presence is the companion
/// <see cref="RequiredAttribute"/>'s concern.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ListenUrlAttributeTests
{
	private const string MemberName = nameof(ProxyOptions.ListenUrl);

	// --- 1. Accepted: interface-specific addresses ---

	/// <summary>
	/// Provides listen addresses that bind a specific, operator-intended interface and must be accepted:
	/// loopback, IPv4/IPv6 literals, the bind-all IP literals, and the explicit Kestrel wildcards.
	/// </summary>
	public static TheoryData<string, string> AcceptedAddresses => new()
	{
		{ "loopback name", "http://localhost:11434" },
		{ "loopback IPv4 literal", "http://127.0.0.1:8080" },
		{ "bind-all IPv4 literal", "http://0.0.0.0:11434" },
		{ "specific IPv4 literal", "http://192.168.1.5:11434" },
		{ "loopback IPv6 literal", "http://[::1]:11434" },
		{ "bind-all IPv6 literal", "http://[::]:11434" },
		{ "asterisk wildcard", "http://*:11434" },
		{ "plus wildcard", "http://+:11434" },
		{ "https scheme", "https://localhost:11435" }
	};

	/// <summary>
	/// Verifies that an address naming a specific interface (loopback, IP literal, or explicit wildcard) is
	/// accepted, so documented bind targets keep working without reconfiguration.
	/// </summary>
	/// <param name="scenario">A human-readable label for the address form under test.</param>
	/// <param name="listenUrl">The listen URL expected to validate.</param>
	[Theory]
	[MemberData(nameof(AcceptedAddresses))]
	public void IsValid_WhenInterfaceSpecificAddress_ReturnsSuccess(string scenario, string listenUrl)
	{
		_ = scenario; // documents the case in test output; not otherwise asserted.

		// Arrange + Act
		ValidationResult? result = Validate(listenUrl);

		// Assert: ValidationResult.Success is null, so a passing value yields a null result.
		Assert.Null(result);
		Assert.Equal(ValidationResult.Success, result);
	}

	/// <summary>
	/// Verifies that a <see langword="null"/> value passes, deferring the presence check to the companion
	/// <see cref="RequiredAttribute"/> rather than double-reporting a missing value.
	/// </summary>
	[Fact]
	public void IsValid_WhenNull_ReturnsSuccess()
	{
		// Arrange + Act
		ValidationResult? result = Validate(null);

		// Assert
		Assert.Null(result);
	}

	// --- 2. Rejected: unresolved DNS host names ---

	/// <summary>
	/// Verifies that a DNS host name (which Kestrel would silently expand to "bind every interface") is
	/// rejected and the failure points at the <c>ListenUrl</c> member.
	/// </summary>
	/// <param name="listenUrl">The DNS-host listen URL expected to fail.</param>
	[Theory]
	[InlineData("http://my-server:11434")]
	[InlineData("http://example.com:11434")]
	[InlineData("http://proxy.internal.lan:11435")]
	public void IsValid_WhenDnsHostName_ReturnsFailure(string listenUrl)
	{
		// Arrange + Act
		ValidationResult? result = Validate(listenUrl);

		// Assert
		Assert.NotNull(result);
		Assert.Equal([MemberName], result.MemberNames);
	}

	/// <summary>
	/// Verifies the exact failure message for a DNS host, since it is a custom, domain-specific message that
	/// names the offending host and the safe alternatives an operator can use.
	/// </summary>
	[Fact]
	public void IsValid_WhenDnsHostName_ReportsHostAndAlternativesInMessage()
	{
		// Arrange
		const string listenUrl = "http://my-server:11434";
		string host = BindingAddress.Parse(listenUrl).Host;
		string expected =
			$"The {MemberName} field '{listenUrl}' uses the DNS host name '{host}', which Kestrel does not " +
			"resolve — it would bind every network interface instead. Use 'localhost', an IP literal (for " +
			"example 127.0.0.1), or an explicit wildcard (0.0.0.0, [::], * or +) to state the intended interface.";

		// Act
		ValidationResult? result = Validate(listenUrl);

		// Assert
		Assert.NotNull(result);
		Assert.Equal(expected, result.ErrorMessage);
	}

	// --- 3. Rejected: wrong scheme ---

	/// <summary>
	/// Verifies that a non-http/https scheme is rejected, since Kestrel serves the proxy and admin surfaces
	/// over http/https only.
	/// </summary>
	/// <param name="listenUrl">The wrong-scheme listen URL expected to fail.</param>
	[Theory]
	[InlineData("ftp://localhost:11434")]
	[InlineData("tcp://127.0.0.1:11434")]
	public void IsValid_WhenSchemeNotHttp_ReturnsFailure(string listenUrl)
	{
		// Arrange + Act
		ValidationResult? result = Validate(listenUrl);

		// Assert
		Assert.NotNull(result);
		Assert.Equal([MemberName], result.MemberNames);
	}

	// --- 4. Rejected: malformed or blank values ---

	/// <summary>
	/// Verifies that a malformed, blank, or whitespace value is rejected, so a non-URL never slips through to
	/// the bind stage.
	/// </summary>
	/// <param name="listenUrl">The malformed or blank listen URL expected to fail.</param>
	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("not-a-url")]
	[InlineData("localhost:11434")]
	public void IsValid_WhenMalformedOrBlank_ReturnsFailure(string listenUrl)
	{
		// Arrange + Act
		ValidationResult? result = Validate(listenUrl);

		// Assert
		Assert.NotNull(result);
		Assert.Equal([MemberName], result.MemberNames);
	}

	// --- Helpers ---

	/// <summary>
	/// Runs <see cref="ListenUrlAttribute"/> against <paramref name="value"/> using a context whose member is
	/// <c>ListenUrl</c>, mirroring how the options pipeline validates the real property.
	/// </summary>
	/// <param name="value">The value to validate.</param>
	/// <returns>
	/// <see langword="null"/> (<see cref="ValidationResult.Success"/>) when the value passes; otherwise the
	/// failure result.
	/// </returns>
	private static ValidationResult? Validate(object? value)
	{
		ListenUrlAttribute attribute = new();
		ValidationContext context = new(new object()) { MemberName = MemberName, DisplayName = MemberName };
		return attribute.GetValidationResult(value, context);
	}
}
