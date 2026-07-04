// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.ComponentModel.DataAnnotations;

namespace OllamaProxy.Configuration;

/// <summary>
/// Controls active capability probing. This is the optional stage-2 detection step that issues tiny
/// throwaway requests to confirm whether a backend model honors a capability. A completion probe sends a
/// one-token chat completion; a tool probe adds a dummy function; a vision probe attaches a small
/// placeholder image; an embedding probe posts a short input to the embeddings endpoint. Each probe costs
/// an upstream round trip per model, so probing is consulted only when backend metadata is inconclusive.
/// All probes default to <see langword="true"/> and can be turned off independently for backends where the
/// extra round trip is unwelcome. Transient failures (HTTP 429, server errors, transport faults) are
/// retried with exponential backoff (see <see cref="MaxProbeRetries"/> and
/// <see cref="RetryBaseDelaySeconds"/>) so a momentary backend hiccup during discovery does not mislabel a
/// model; a backend that answers an HTTP 429 with a <c>Retry-After</c> header is obeyed verbatim and the
/// wait is shared across the backend's concurrent probes, so a rate limit slows the whole scan in step
/// rather than being re-hit by every sibling. A timeout, by contrast, is not retried. A slow model is given
/// one adequate attempt bounded by <see cref="TimeoutSeconds"/> (startup) or the larger
/// <see cref="InteractiveTimeoutSeconds"/> (the on-demand admin probe).
/// </summary>
public sealed class CapabilityProbingOptions : IValidatableObject
{
	/// <summary>
	/// The smallest accepted probe timeout, in seconds.
	/// </summary>
	public const int MinimumTimeoutSeconds = 1;

	/// <summary>
	/// The largest accepted probe timeout, in seconds.
	/// </summary>
	public const int MaximumTimeoutSeconds = 120;

	/// <summary>
	/// The smallest accepted interactive probe timeout, in seconds.
	/// </summary>
	public const int MinimumInteractiveTimeoutSeconds = 1;

	/// <summary>
	/// The largest accepted interactive probe timeout, in seconds.
	/// </summary>
	public const int MaximumInteractiveTimeoutSeconds = 300;

	/// <summary>
	/// The smallest accepted value for <see cref="MaxConcurrentProbes"/>.
	/// </summary>
	public const int MinimumMaxConcurrentProbes = 1;

	/// <summary>
	/// The largest accepted value for <see cref="MaxConcurrentProbes"/>.
	/// </summary>
	public const int MaximumMaxConcurrentProbes = 64;

	/// <summary>
	/// The smallest accepted value for <see cref="MaxProbeRetries"/>.
	/// </summary>
	public const int MinimumMaxProbeRetries = 0;

	/// <summary>
	/// The largest accepted value for <see cref="MaxProbeRetries"/>.
	/// </summary>
	public const int MaximumMaxProbeRetries = 10;

	/// <summary>
	/// The smallest accepted value for <see cref="RetryBaseDelaySeconds"/>.
	/// </summary>
	public const int MinimumRetryBaseDelaySeconds = 0;

	/// <summary>
	/// The largest accepted value for <see cref="RetryBaseDelaySeconds"/>.
	/// </summary>
	public const int MaximumRetryBaseDelaySeconds = 30;

	/// <summary>
	/// Gets or sets a value indicating whether the completion-support probe runs.
	/// Defaults to <see langword="true"/>; set to <see langword="false"/> to skip the extra round trip
	/// on backends where completion support is already known or irrelevant. A conclusive negative result
	/// is what lets the proxy recognize an embedding-only model and stop advertising chat completion it
	/// cannot serve; an inconclusive result leaves completion on its conservative <see langword="true"/>
	/// baseline so a transient hiccup never hides a working chat model.
	/// </summary>
	public bool ProbeCompletion { get; set; } = true;

	/// <summary>
	/// Gets or sets a value indicating whether the tool-support probe runs.
	/// Defaults to <see langword="true"/>; set to <see langword="false"/> to skip the extra round trip
	/// on backends where tool support is already known or irrelevant.
	/// </summary>
	public bool ProbeTools { get; set; } = true;

	/// <summary>
	/// Gets or sets a value indicating whether the vision-support probe runs.
	/// Defaults to <see langword="true"/>; set to <see langword="false"/> to avoid sending a placeholder image
	/// to backends where it is unwelcome or vision support is already known.
	/// </summary>
	public bool ProbeVision { get; set; } = true;

	/// <summary>
	/// Gets or sets a value indicating whether the embedding-support probe runs.
	/// Defaults to <see langword="true"/>; set to <see langword="false"/> to skip the extra round trip
	/// on backends where embedding support is already known or irrelevant.
	/// </summary>
	public bool ProbeEmbeddings { get; set; } = true;

	/// <summary>
	/// Gets or sets the per-attempt timeout in seconds. A single probe attempt that does not complete in
	/// time is reported as inconclusive and detection falls through to the conservative default. A timeout
	/// is <em>not</em> retried. A slow model is given one adequate attempt rather than several short ones
	/// that each expire before the model can answer. So this value, not its product with
	/// <see cref="MaxProbeRetries"/>, is the whole budget a single probe waits. Used for non-interactive
	/// startup discovery; the on-demand admin probe uses the larger <see cref="InteractiveTimeoutSeconds"/>.
	/// </summary>
	public int TimeoutSeconds { get; set; } = 10;

	/// <summary>
	/// Gets or sets the per-attempt timeout in seconds for an <em>interactive</em>, operator-triggered probe
	/// (the admin surface's "probe capabilities" action), as opposed to the non-interactive startup discovery
	/// that uses <see cref="TimeoutSeconds"/>. It is larger by default because an interactive probe is awaited
	/// by a person who wants a conclusive answer and accepts the latency: a model that must cold-load before it
	/// can answer the probe often needs far more than the startup timeout, and that load is exactly what made
	/// the startup probes time out. Like <see cref="TimeoutSeconds"/>, a timeout here is not retried. The whole
	/// budget funds one adequate attempt. Must be between <see cref="MinimumInteractiveTimeoutSeconds"/> and
	/// <see cref="MaximumInteractiveTimeoutSeconds"/>.
	/// </summary>
	public int InteractiveTimeoutSeconds { get; set; } = 60;

	/// <summary>
	/// Gets or sets how many times a probe is retried after a <em>transient</em> failure (an HTTP 429, an
	/// HTTP 5xx, or a transport error) before the outcome is reported as inconclusive. A timeout is
	/// <em>not</em> a transient failure for this purpose and is never retried: a model too slow to answer in
	/// the per-attempt window will not become faster on a second identical attempt, so the budget is better
	/// spent on a single adequate timeout (see <see cref="TimeoutSeconds"/> and
	/// <see cref="InteractiveTimeoutSeconds"/>). Authentication failures (HTTP 401/403) are permanent but
	/// inconclusive because they say nothing about capability presence. Non-auth client errors (including
	/// HTTP 404) and conclusive results (2xx, or a body-rejecting 4xx) are never retried because retrying
	/// cannot change them. A value of <c>0</c> disables retries (a single attempt). Defaults to <c>3</c>
	/// (up to four attempts total) so a backend that keeps returning HTTP 429 has more chances to cool down
	/// before the probe is abandoned. Must be between <see cref="MinimumMaxProbeRetries"/> and
	/// <see cref="MaximumMaxProbeRetries"/>.
	/// </summary>
	public int MaxProbeRetries { get; set; } = 3;

	/// <summary>
	/// Gets or sets the base delay in seconds for the exponential backoff applied between retry
	/// attempts when the backend gives no explicit <c>Retry-After</c>: the delay before the <c>n</c>-th retry
	/// is <c>RetryBaseDelaySeconds * 2^(n-1)</c> (so <c>4s, 8s, 16s, …</c> for the default base of <c>4</c>).
	/// A server-sent <c>Retry-After</c> header on an HTTP 429 always wins over this computed delay, so the
	/// proxy waits exactly as long as the backend asked. The default of <c>4</c> is deliberately conservative:
	/// a rate limit is typically scoped to a per-minute window, and a backoff that starts too low merely
	/// re-hits the same wall, so the safer default trades a slower scan for a conclusive one. A value of
	/// <c>0</c> retries immediately with no delay. Only consulted when <see cref="MaxProbeRetries"/> is
	/// greater than zero. Must be between <see cref="MinimumRetryBaseDelaySeconds"/> and
	/// <see cref="MaximumRetryBaseDelaySeconds"/>.
	/// </summary>
	public int RetryBaseDelaySeconds { get; set; } = 4;

	/// <summary>
	/// Gets or sets the maximum number of models probed concurrently within a single backend during
	/// startup discovery. Discovery resolves each model's capabilities independently, so raising this
	/// value shortens the cold start of a backend with many models (for example a provider reporting
	/// dozens of models) at the cost of more simultaneous in-flight probe requests. A single model's
	/// completion, tool, vision and embedding probes run sequentially within that model and are not governed
	/// by this limit, which caps how many <em>models</em> are probed in parallel. Defaults to <c>1</c>. A
	/// fully serialized scan is the safe choice against rate-limited backends, since concurrent probes are
	/// the surest way to trip an HTTP 429. Raise it for backends that tolerate parallelism (a local server, a
	/// generous quota) to speed discovery up; a shared per-backend cooldown still applies a backend's
	/// <c>Retry-After</c> across whatever concurrency is configured. Must be between
	/// <see cref="MinimumMaxConcurrentProbes"/> and <see cref="MaximumMaxConcurrentProbes"/>.
	/// </summary>
	public int MaxConcurrentProbes { get; set; } = 1;

	/// <inheritdoc/>
	public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
	{
		// MaxConcurrentProbes governs discovery fan-out regardless of which probes run, so it is always
		// validated, even when every probe is disabled and the timeout below is left unchecked.
		if (MaxConcurrentProbes is < MinimumMaxConcurrentProbes or > MaximumMaxConcurrentProbes)
		{
			yield return new ValidationResult(
				$"Maximum concurrent probes must be between {MinimumMaxConcurrentProbes} and " +
				$"{MaximumMaxConcurrentProbes}.",
				[nameof(MaxConcurrentProbes)]);
		}

		// When no probe runs, the timeout and retry settings are irrelevant and left unchecked.
		if (!ProbeCompletion && !ProbeTools && !ProbeVision && !ProbeEmbeddings) yield break;

		if (TimeoutSeconds is < MinimumTimeoutSeconds or > MaximumTimeoutSeconds)
		{
			yield return new ValidationResult(
				$"Probe timeout must be between {MinimumTimeoutSeconds} and {MaximumTimeoutSeconds} seconds.",
				[nameof(TimeoutSeconds)]);
		}

		if (InteractiveTimeoutSeconds is < MinimumInteractiveTimeoutSeconds or > MaximumInteractiveTimeoutSeconds)
		{
			yield return new ValidationResult(
				$"Interactive probe timeout must be between {MinimumInteractiveTimeoutSeconds} and " +
				$"{MaximumInteractiveTimeoutSeconds} seconds.",
				[nameof(InteractiveTimeoutSeconds)]);
		}

		if (MaxProbeRetries is < MinimumMaxProbeRetries or > MaximumMaxProbeRetries)
		{
			yield return new ValidationResult(
				$"Maximum probe retries must be between {MinimumMaxProbeRetries} and {MaximumMaxProbeRetries}.",
				[nameof(MaxProbeRetries)]);
		}

		if (RetryBaseDelaySeconds is < MinimumRetryBaseDelaySeconds or > MaximumRetryBaseDelaySeconds)
		{
			yield return new ValidationResult(
				$"Retry base delay must be between {MinimumRetryBaseDelaySeconds} and " +
				$"{MaximumRetryBaseDelaySeconds} seconds.",
				[nameof(RetryBaseDelaySeconds)]);
		}
	}

	/// <summary>
	/// Creates a deep copy of these probing settings. Every property is value-typed, so the copy shares no
	/// mutable state with this instance and the editor can toggle the copy without touching the live snapshot.
	/// </summary>
	/// <returns>A standalone copy carrying every probing setting.</returns>
	public CapabilityProbingOptions DeepClone() => new()
	{
		ProbeCompletion = ProbeCompletion,
		ProbeTools = ProbeTools,
		ProbeVision = ProbeVision,
		ProbeEmbeddings = ProbeEmbeddings,
		TimeoutSeconds = TimeoutSeconds,
		InteractiveTimeoutSeconds = InteractiveTimeoutSeconds,
		MaxProbeRetries = MaxProbeRetries,
		RetryBaseDelaySeconds = RetryBaseDelaySeconds,
		MaxConcurrentProbes = MaxConcurrentProbes
	};
}
