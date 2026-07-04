// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Admin.Config;

/// <summary>
/// The outcome of applying a proxy configuration change: persisting it and recycling the inner host onto
/// it. There are three observable results. The change was applied and is now live; the change was rejected
/// by the recycle's dry-run validation and rolled back, so the previous configuration is still live and on
/// disk; or the change could not be written at all and was never applied. In every non-success case the
/// running proxy keeps serving its previous configuration uninterrupted.
/// </summary>
/// <param name="Outcome">Which of the three terminal states the apply reached.</param>
/// <param name="Errors">
/// The reasons the change was rejected when <paramref name="Outcome"/> is
/// <see cref="ApplyOutcome.ValidationRejected"/>, or a single write-failure message when it is
/// <see cref="ApplyOutcome.WriteFailed"/>; empty on success.
/// </param>
public sealed record ApplyResult(ApplyOutcome Outcome, IReadOnlyList<string> Errors)
{
	/// <summary>
	/// Gets a value indicating whether the configuration was persisted and is now the live configuration of a
	/// freshly recycled inner host.
	/// </summary>
	public bool Success => Outcome == ApplyOutcome.Applied;

	/// <summary>
	/// A shared successful result carrying no errors.
	/// </summary>
	public static ApplyResult Applied { get; } = new(ApplyOutcome.Applied, []);

	/// <summary>
	/// Creates a result for a change that was written but then rejected by the recycle's dry-run validation and
	/// rolled back, leaving the previous configuration live and on disk.
	/// </summary>
	/// <param name="errors">The validation reasons the candidate configuration was rejected.</param>
	/// <returns>A <see cref="ApplyResult"/> with <see cref="ApplyOutcome.ValidationRejected"/>.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="errors"/> is <see langword="null"/>.</exception>
	public static ApplyResult ValidationRejected(IReadOnlyList<string> errors)
	{
		ArgumentNullException.ThrowIfNull(errors);

		return new ApplyResult(ApplyOutcome.ValidationRejected, errors);
	}

	/// <summary>
	/// Creates a result for a change that could not be persisted, so the recycle was never attempted and the
	/// previous configuration remains live and unchanged on disk.
	/// </summary>
	/// <param name="error">A message describing why the configuration could not be written.</param>
	/// <returns>A <see cref="ApplyResult"/> with <see cref="ApplyOutcome.WriteFailed"/>.</returns>
	/// <exception cref="ArgumentException"><paramref name="error"/> is <see langword="null"/>, empty, or white-space.</exception>
	public static ApplyResult WriteFailed(string error)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(error);

		return new ApplyResult(ApplyOutcome.WriteFailed, [error]);
	}
}
