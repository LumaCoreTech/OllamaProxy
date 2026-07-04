// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Tests.Integration.Live;

/// <summary>
/// A <see cref="TheoryAttribute"/> counterpart to <see cref="LiveBackendFactAttribute"/> that gates a
/// data-driven live test on the presence of one or more environment variables. When any named variable is
/// absent or blank, the inherited <see cref="TheoryAttribute.Skip"/> is set so xUnit reports the theory as
/// <em>Skipped</em> rather than a false <em>Pass</em>; when all are present the theory runs across its data
/// rows normally.
/// </summary>
/// <param name="requiredEnvironmentVariables">
/// The environment variable names the theory requires. The first one that is absent or blank determines the
/// skip reason.
/// </param>
[AttributeUsage(AttributeTargets.Method)]
sealed class LiveBackendTheoryAttribute(params string[] requiredEnvironmentVariables) : TheoryAttribute
{
	/// <summary>
	/// The skip reason resolved once at attribute construction, surfaced to xUnit through
	/// <see cref="TheoryAttribute.Skip"/>. Computed eagerly so the discovery phase already reflects the
	/// machine's environment.
	/// </summary>
	public override string? Skip { get; set; } =
		LiveEnvironment.ResolveSkipReason(requiredEnvironmentVariables);
}
