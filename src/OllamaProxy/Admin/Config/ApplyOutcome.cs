// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Admin.Config;

/// <summary>
/// The terminal state an <see cref="ApplyResult"/> reached when a configuration change was applied.
/// Declared <see langword="public"/> so the admin UI's result banner can take it as a parameter type without
/// having to wrap or split the value across multiple <c>[Parameter]</c>s.
/// </summary>
public enum ApplyOutcome
{
	/// <summary>
	/// The configuration was written and the inner host was recycled onto it; it is now live.
	/// </summary>
	Applied,

	/// <summary>
	/// The configuration was written but the recycle's dry-run validation rejected it, so it was rolled
	/// back. The previous configuration is still live and is what remains on disk.
	/// </summary>
	ValidationRejected,

	/// <summary>
	/// The configuration could not be written, so no recycle was attempted. The previous configuration
	/// remains live and unchanged on disk.
	/// </summary>
	WriteFailed
}
