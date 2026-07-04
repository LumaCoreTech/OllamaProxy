// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.ComponentModel.DataAnnotations;

namespace OllamaProxy.Configuration;

/// <summary>
/// Controls the server-side reasoning-details round-trip cache. Declared <see langword="public"/> so the
/// admin UI's component parameters can accept it without forcing every parent to expose internals.
/// </summary>
public sealed class ReasoningDetailsCacheOptions : IValidatableObject
{
	/// <summary>
	/// The smallest accepted value for <see cref="SlidingExpirationSeconds"/>.
	/// </summary>
	public const int MinimumSlidingExpirationSeconds = 1;

	/// <summary>
	/// The largest accepted value for <see cref="SlidingExpirationSeconds"/>.
	/// </summary>
	public const int MaximumSlidingExpirationSeconds = 3600;

	/// <summary>
	/// The smallest accepted value for <see cref="MaxEntries"/>.
	/// </summary>
	public const int MinimumMaxEntries = 1;

	/// <summary>
	/// The largest accepted value for <see cref="MaxEntries"/>.
	/// </summary>
	public const int MaximumMaxEntries = 65536;

	/// <summary>
	/// Gets or sets a value indicating whether the reasoning-details round-trip is active. Defaults to
	/// <see langword="true"/>. When <see langword="false"/>, the proxy neither captures nor re-attaches
	/// <c>reasoning_details</c>: the cache is never populated and the field is left to the backend's own
	/// defaults. Turning it off is the escape hatch for a backend where carrying the blob is unwelcome,
	/// and costs only the reasoning continuity the feature would have preserved.
	/// </summary>
	public bool Enabled { get; set; } = true;

	/// <summary>
	/// Gets or sets how long, in seconds, a captured <c>reasoning_details</c> blob is retained after it was
	/// last read or written. The expiration <em>slides</em>: each capture or re-attach of an entry renews
	/// its lifetime, so a conversation still replaying its earlier turns keeps those blobs warm while a
	/// conversation that has gone quiet (its tool chain finished, or it was abandoned) lets them expire.
	/// Defaults to <c>300</c> (five minutes). That is comfortably longer than the gap between a tool call and
	/// its result in an interactive or agentic loop, yet short enough that an abandoned conversation does not
	/// pin memory. Must be between <see cref="MinimumSlidingExpirationSeconds"/> and
	/// <see cref="MaximumSlidingExpirationSeconds"/>.
	/// </summary>
	public int SlidingExpirationSeconds { get; set; } = 300;

	/// <summary>
	/// Gets or sets the maximum number of <c>reasoning_details</c> blobs retained at once. The cache is a
	/// bounded safety net, not durable storage: when it is full the least-recently-used entry is evicted to
	/// admit a new one, so a burst of distinct tool-calling conversations can never grow memory without
	/// limit. Defaults to <c>1024</c>. That is enough to cover many concurrent conversations while keeping the
	/// worst-case footprint small (each blob is typically a few kilobytes). Must be between
	/// <see cref="MinimumMaxEntries"/> and <see cref="MaximumMaxEntries"/>.
	/// </summary>
	public int MaxEntries { get; set; } = 1024;

	/// <inheritdoc/>
	public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
	{
		// When the round-trip is disabled the cache is never built, so its sizing knobs are irrelevant and
		// left unchecked. This mirrors how the probing options skip their timeout/retry checks when no probe runs.
		if (!Enabled) yield break;

		if (SlidingExpirationSeconds is < MinimumSlidingExpirationSeconds or > MaximumSlidingExpirationSeconds)
		{
			yield return new ValidationResult(
				$"Reasoning-details cache sliding expiration must be between {MinimumSlidingExpirationSeconds} " +
				$"and {MaximumSlidingExpirationSeconds} seconds.",
				[nameof(SlidingExpirationSeconds)]);
		}

		if (MaxEntries is < MinimumMaxEntries or > MaximumMaxEntries)
		{
			yield return new ValidationResult(
				$"Reasoning-details cache maximum entries must be between {MinimumMaxEntries} and " +
				$"{MaximumMaxEntries}.",
				[nameof(MaxEntries)]);
		}
	}
}
