// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Diagnostics;

using OllamaProxy.Admin.Config;

namespace OllamaProxy.Admin.Ui.Components.Backends;

/// <summary>
/// Translates an <see cref="ApplyOutcome"/> into the copy the <see cref="ApplyResultBanner"/> renders after a
/// sync attempt: the CSS modifier token that colors the banner and the operator-facing headline.
/// </summary>
/// <remarks>
/// The mapping logic lives here rather than in the component's code-behind so it can be unit-tested as a pure
/// function without rendering the component. <see cref="ApplyResultBanner"/> is a thin renderer over the strings
/// this presenter returns.
/// </remarks>
static class ApplyResultBannerPresenter
{
	/// <summary>
	/// Maps an apply outcome to its result-banner CSS modifier. The tokens (<c>applied</c>, <c>rejected</c>,
	/// <c>failed</c>) pair with the <c>.apply-result-*</c> rules in the scoped stylesheet.
	/// </summary>
	/// <param name="outcome">The apply outcome.</param>
	/// <returns>The lower-case CSS modifier token for the outcome.</returns>
	/// <exception cref="UnreachableException">
	/// <paramref name="outcome"/> is not a defined <see cref="ApplyOutcome"/> value.
	/// </exception>
	public static string CssModifier(ApplyOutcome outcome) => outcome switch
	{
		ApplyOutcome.Applied            => "applied",
		ApplyOutcome.ValidationRejected => "rejected",
		ApplyOutcome.WriteFailed        => "failed",
		var _                           => throw new UnreachableException($"Unhandled apply outcome '{outcome}'.")
	};

	/// <summary>
	/// Maps an apply outcome to its result-banner headline, telling the operator whether the change went live
	/// or, for both non-success outcomes, that the previous configuration is still live.
	/// </summary>
	/// <param name="outcome">The apply outcome.</param>
	/// <returns>The headline for the outcome.</returns>
	/// <exception cref="UnreachableException">
	/// <paramref name="outcome"/> is not a defined <see cref="ApplyOutcome"/> value.
	/// </exception>
	public static string Headline(ApplyOutcome outcome) => outcome switch
	{
		ApplyOutcome.Applied =>
			"Changes applied. The proxy is now running the updated configuration.",
		ApplyOutcome.ValidationRejected =>
			"The change was rejected and rolled back. The previous configuration is still live.",
		ApplyOutcome.WriteFailed =>
			"The configuration could not be written. The previous configuration is still live.",
		var _ => throw new UnreachableException($"Unhandled apply outcome '{outcome}'.")
	};
}
