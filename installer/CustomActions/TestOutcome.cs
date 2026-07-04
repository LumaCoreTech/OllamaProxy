// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.CustomActions;

/// <summary>
/// The result of a backend connectivity probe: whether it passed and the message to surface.
/// </summary>
readonly struct TestOutcome
{
	/// <summary>
	/// Initializes a new instance of the <see cref="TestOutcome"/> struct.
	/// </summary>
	/// <param name="ok">Whether the probe succeeded.</param>
	/// <param name="message">The operator-facing message.</param>
	public TestOutcome(bool ok, string message)
	{
		Ok = ok;
		Message = message;
	}

	/// <summary>
	/// Gets a value indicating whether the probe succeeded.
	/// </summary>
	public bool Ok { get; }

	/// <summary>
	/// Gets the operator-facing message describing the outcome.
	/// </summary>
	public string Message { get; }
}
