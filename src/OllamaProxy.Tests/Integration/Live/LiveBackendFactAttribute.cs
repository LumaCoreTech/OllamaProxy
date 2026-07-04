// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Tests.Integration.Live;

/// <summary>
/// A <see cref="FactAttribute"/> that gates a live test on the presence of one or more environment
/// variables. When any named variable is absent or blank, the inherited <see cref="FactAttribute.Skip"/>
/// is set so xUnit reports the test as <em>Skipped</em> (never a false <em>Pass</em>); when all are present
/// the test runs normally. This is the xUnit 2.x-native way to make a test conditionally skip — version 2.9
/// has no <c>Assert.Skip</c> — and it keeps the live suite safe to leave in the default test run: a machine
/// without credentials simply skips it.
/// </summary>
/// <param name="requiredEnvironmentVariables">
/// The environment variable names the test requires (for example the backend API key and the model id under
/// test). The first one that is absent or blank determines the skip reason.
/// </param>
[AttributeUsage(AttributeTargets.Method)]
sealed class LiveBackendFactAttribute(params string[] requiredEnvironmentVariables) : FactAttribute
{
	/// <summary>
	/// The skip reason resolved once at attribute construction, surfaced to xUnit through
	/// <see cref="FactAttribute.Skip"/>. Computed eagerly so the discovery phase already reflects the
	/// machine's environment.
	/// </summary>
	public override string? Skip { get; set; } =
		LiveEnvironment.ResolveSkipReason(requiredEnvironmentVariables);
}
