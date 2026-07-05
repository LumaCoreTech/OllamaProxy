// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Options;

using OllamaProxy.Configuration;
using OllamaProxy.Providers.Abstractions;
using OllamaProxy.Providers.Http;

namespace OllamaProxy.Providers.OpenAiProtocol;

/// <summary>
/// <see cref="ICapabilityProber"/> implementation for OpenAI-compatible backends. Each probe posts a
/// minimal throwaway request that exercises a single capability (a one-token completion, a dummy
/// function for tools, a small placeholder image for vision, a tiny input for embeddings) and reads the
/// capability from the HTTP status: a 2xx confirms it, while a non-auth 4xx (anything but 401/403)
/// declines it. Transient failures (429, 5xx, transport faults) are retried with exponential backoff;
/// timeouts and auth failures (401/403) are not, since neither a slower retry nor a second identical
/// attempt would change the outcome. When every attempt is exhausted the probe is inconclusive
/// (<see langword="null"/>), so the caller keeps its conservative default rather than advertising a
/// capability that may not be present.
/// </summary>
/// <remarks>
///     <para>
///     <b>HTTP-status interpretation policy.</b> The prober uses HTTP status codes as the capability
///     signal without inspecting the response body. The edge cases below produce known, accepted
///     imprecision:
///     </para>
///     <list type="bullet">
///         <item>
///         A non-auth 4xx (400, 404, 422, etc.) is read as the backend declining the capability through
///         this route. The exact cause (a missing model, a missing endpoint, or a rejected payload) does
///         not matter: the capability is not usable here, so it is reported as unsupported. That can be a
///         false negative if the backend rejects the probe payload itself; operators who hit one should
///         pin the capability explicitly in the <c>Models</c> registry.
///         </item>
///         <item>
///         401 and 403 are treated as inconclusive rather than "capability absent" because authentication
///         failures say nothing about whether the model supports the probed feature.
///         </item>
///         <item>
///         3xx redirects are not handled: Ollama does not emit them, and redirect-based setups (auth
///         gateways, OAuth proxies) indicate an infrastructure misconfiguration rather than a capability
///         signal.
///         </item>
///         <item>
///         A 2xx is read as the backend honoring the capability, but a backend that does not understand
///         the capability-specific field may <em>silently ignore</em> it and still answer 2xx, a false
///         positive (e.g. dropping an unknown <c>tools</c> array, or treating an <c>image_url</c> part as
///         plain text). The prober does not inspect the body to refute this, because a one-token probe
///         carries no reliable cross-backend proof the field took effect: status-based probing confirms the
///         request was <em>accepted</em>, not that the feature was <em>exercised</em>. Operators should pin
///         the correct value in the <c>Models</c> registry, which overrides probing.
///         </item>
///     </list>
///     <para>
///     <b>Timeout architecture.</b> The prober enforces its own per-attempt deadline by linking a timeout
///     token with the caller's cancellation token. The deadline is context-sensitive: a <em>committed</em>
///     backend (startup discovery) uses <c>probing.TimeoutSeconds</c>, while a <em>draft</em> backend (the
///     operator-triggered admin probe, identified by <see cref="BackendContext.Draft"/> being
///     non-<see langword="null"/>) uses the larger <c>probing.InteractiveTimeoutSeconds</c>, because a
///     person is waiting for a conclusive answer and accepts the latency, including a model's cold-load
///     time. A timeout is <em>not</em> retried: a model too slow to answer within the window will
///     not answer a second identical attempt any faster, so the whole budget funds one adequate attempt. The
///     <see cref="System.Net.Http.HttpClient"/> supplied by <c>IBackendHttpClientProvider</c> is configured
///     with an <em>infinite</em> client-level timeout, so the prober is the sole authority over the deadline.
///     The catch for <see cref="System.Threading.Tasks.TaskCanceledException"/> remains a defensive fallback:
///     should a future provider hand out a client with a finite timeout shorter than the probe timeout, it
///     surfaces as the same <see langword="null"/> (inconclusive) outcome with less precise diagnostics.
///     </para>
/// </remarks>
// All logging here runs once per model during startup discovery, so the LoggerMessage delegate
// ceremony (CA1848) and the lazy-evaluation guard (CA1873) buy nothing. The log arguments
// (model id, backend name) are already-materialized strings; the message template is a short
// constant concatenation with no interpolation to defer.
[SuppressMessage(
	"Performance",
	"CA1848:Use the LoggerMessage delegates",
	Justification = "Startup-only discovery logging; the LoggerMessage delegate ceremony is not worth it here.")]
[SuppressMessage(
	"Performance",
	"CA1873:Avoid potentially expensive logging",
	Justification = "Startup-only discovery logging with already-materialized arguments.")]
sealed class OpenAiCapabilityProber : ICapabilityProber
{
	private const string ChatCompletionsPath = "chat/completions";
	private const string EmbeddingsPath      = "embeddings";

	// The ceiling on a honored Retry-After. A backend's throttling response is obeyed verbatim up to this
	// bound, but no further: a misconfigured or hostile backend that answers an HTTP 429 with "Retry-After:
	// 3600" must not be able to stall discovery for an hour, so the wait is clamped. The probe still ends
	// inconclusive after its retry budget, so the cap only bounds how long a single wait can be, never the
	// correctness of the outcome.
	private static readonly TimeSpan MaxHonoredRetryAfter = TimeSpan.FromSeconds(60);

	// The probe prompt. It asks the model to stay silent so a probe generates as little output as
	// possible without relying on a token-cap parameter: max_tokens is deprecated (OpenAI reasoning
	// models reject it with HTTP 400, which the prober would misread as "capability absent"), and its
	// replacement max_completion_tokens is not yet universal across OpenAI-compatible backends. Omitting
	// the cap sidesteps that incompatibility on every backend; the chat probes instead stream (see
	// StreamFlag) so they confirm on the response headers and never wait for the full generation.
	private const string SilentProbePrompt = "Respond with nothing.";

	// A small feature-rich 96x96 JPEG used to exercise the vision input path. The decisive constraint here
	// was empirically verified to be image CONTENT, not image size: some backends run a content/feature
	// check on the probe image and reject one that is too plain ("Supplied image did not pass validation
	// checks."), which surfaces as a body-rejecting 4xx the prober reads as "vision unsupported". So a flat,
	// monochrome placeholder made vision-capable models probe as vision-less.
	//
	// Measured live against several real Venice vision models (venice-uncensored-1-2, qwen-3-7-plus,
	// qwen3-5-35b-a3b, google-gemma-3-27b-it): a plain single-colour / simple-geometry PNG was rejected with
	// imgRejected=true at 64x64 AND at 256x256 AND at 512x512 (proving size was never the gate) while this
	// busier image (diagonal colour gradient plus several distinct coloured shapes) passed validation on every
	// one of those models. An earlier "64x64 is the minimum size" reading was a false generalisation from a
	// single model that happened to accept a plain square; do not reintroduce a flat probe image.
	//
	// At ≈4.8 KB base64 the image is still tiny enough to keep the probe cheap.
	private const string PlaceholderImageDataUri =
		"data:image/jpeg;base64,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";

	private readonly IBackendHttpClientProvider      mHttpClientProvider;
	private readonly IOptionsMonitor<ProxyOptions>   mOptions;
	private readonly TimeProvider                    mTimeProvider;
	private readonly ILogger<OpenAiCapabilityProber> mLogger;

	// The shared per-backend rate-limit cooldown. Keyed by backend name (case-insensitive, matching how
	// backends are resolved everywhere else), each entry is the UTC instant before which that backend's
	// probes should not issue another request. This prober is a singleton shared across every backend and
	// every concurrent model probe, so a single model's HTTP 429 publishes its Retry-After here and the
	// SIBLING probes of the same backend (the ones the MaxConcurrentProbes fan-out runs in parallel) wait
	// it out at the top of their own retry loop, instead of each one independently re-hitting the same limit.
	// Keying by backend keeps an unrelated backend (a local Ollama) unthrottled when one provider rate-limits.
	private readonly ConcurrentDictionary<string, DateTimeOffset> mBackendCooldowns =
		new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Initializes a new instance of the <see cref="OpenAiCapabilityProber"/> class.
	/// </summary>
	/// <param name="httpClientProvider">Supplies the pre-configured per-backend HTTP client.</param>
	/// <param name="options">Provides the current proxy options, including per-backend probing settings.</param>
	/// <param name="timeProvider">The clock used to bound each attempt's timeout and pace retry backoff.</param>
	/// <param name="logger">The logger used to record inconclusive probe outcomes.</param>
	/// <exception cref="ArgumentNullException">
	/// <paramref name="httpClientProvider"/>, <paramref name="options"/>, <paramref name="timeProvider"/>
	/// or <paramref name="logger"/> is <see langword="null"/>.
	/// </exception>
	public OpenAiCapabilityProber(
		IBackendHttpClientProvider      httpClientProvider,
		IOptionsMonitor<ProxyOptions>   options,
		TimeProvider                    timeProvider,
		ILogger<OpenAiCapabilityProber> logger)
	{
		ArgumentNullException.ThrowIfNull(httpClientProvider);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(timeProvider);
		ArgumentNullException.ThrowIfNull(logger);

		mHttpClientProvider = httpClientProvider;
		mOptions = options;
		mTimeProvider = timeProvider;
		mLogger = logger;
	}

	/// <inheritdoc/>
	public Task<bool?> ProbeCompletionSupportAsync(
		BackendContext    backend,
		string            modelId,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(backend);
		ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

		return ProbeWithRetryAsync(
			backend,
			modelId,
			capability: "completion",
			ChatCompletionsPath,
			BuildCompletionProbePayload(modelId),
			cancellationToken);
	}

	/// <inheritdoc/>
	public Task<bool?> ProbeToolSupportAsync(
		BackendContext    backend,
		string            modelId,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(backend);
		ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

		return ProbeWithRetryAsync(
			backend,
			modelId,
			capability: "tool",
			ChatCompletionsPath,
			BuildToolProbePayload(modelId),
			cancellationToken);
	}

	/// <inheritdoc/>
	public Task<bool?> ProbeVisionSupportAsync(
		BackendContext    backend,
		string            modelId,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(backend);
		ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

		return ProbeWithRetryAsync(
			backend,
			modelId,
			capability: "vision",
			ChatCompletionsPath,
			BuildVisionProbePayload(modelId),
			cancellationToken);
	}

	/// <inheritdoc/>
	public Task<bool?> ProbeEmbeddingSupportAsync(
		BackendContext    backend,
		string            modelId,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(backend);
		ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

		return ProbeWithRetryAsync(
			backend,
			modelId,
			capability: "embedding",
			EmbeddingsPath,
			BuildEmbeddingProbePayload(modelId),
			cancellationToken);
	}

	/// <summary>
	/// Runs a probe to completion, retrying transient failures with exponential backoff until a
	/// conclusive result is obtained, a permanent failure is seen, or the retry budget is exhausted.
	/// </summary>
	/// <param name="backend">The backend hosting the model.</param>
	/// <param name="modelId">The upstream model identifier being probed.</param>
	/// <param name="capability">A short label naming the probed capability, used only in diagnostics.</param>
	/// <param name="requestPath">The relative endpoint path to post the probe to.</param>
	/// <param name="payload">The probe request body to send on every attempt.</param>
	/// <param name="cancellationToken">A token to cancel the operation.</param>
	/// <returns>
	/// <see langword="true"/> or <see langword="false"/> for a conclusive outcome, or
	/// <see langword="null"/> when the probe stays inconclusive after the final attempt.
	/// </returns>
	private async Task<bool?> ProbeWithRetryAsync(
		BackendContext    backend,
		string            modelId,
		string            capability,
		string            requestPath,
		JsonObject        payload,
		CancellationToken cancellationToken)
	{
		CapabilityProbingOptions probing = GetProbingOptions(backend);
		int maxAttempts = probing.MaxProbeRetries + 1;

		// A draft backend identifies an operator-triggered admin probe, which is awaited interactively and so
		// gets the larger interactive timeout; a committed backend is non-interactive startup discovery and
		// uses the shorter startup timeout. Resolving it once here keeps every attempt of this probe on the
		// same deadline.
		int timeoutSeconds = backend.Draft is not null
			                     ? probing.InteractiveTimeoutSeconds
			                     : probing.TimeoutSeconds;

		for (int attempt = 1;; attempt++)
		{
			// Before every request (the first included) wait out any cooldown a sibling probe of this same
			// backend published after it hit a rate limit. This is what turns the MaxConcurrentProbes fan-out
			// from a thundering herd that each re-hits the limit into a fleet that paces together: one model's
			// HTTP 429 Retry-After is felt by all the others, not just the probe that earned it.
			await WaitOutBackendCooldownAsync(backend.Name, capability, modelId, cancellationToken)
				.ConfigureAwait(false);

			ProbeAttempt outcome = await ProbeOnceAsync(
					                       backend,
					                       modelId,
					                       capability,
					                       requestPath,
					                       payload,
					                       timeoutSeconds,
					                       cancellationToken)
				                       .ConfigureAwait(false);

			if (outcome.Result is { } conclusive) return conclusive;

			// A permanent failure or an exhausted retry budget ends the probe as inconclusive. Surface WHY at a
			// visible level here: the per-attempt detail above is Debug-only, so without this the operator sees
			// only the provider's "stayed inconclusive" consequence with no cause. The reason (an HTTP status, a
			// timeout, or a transport fault) is exactly what distinguishes a rate-limited backend (429) from a
			// dead model (5xx), a too-short timeout, or a network fault, and therefore which knob to turn.
			if (!outcome.Retryable || attempt >= maxAttempts)
			{
				mLogger.LogInformation(
					"{Capability}-support probe for model {Model} on backend {Backend} gave up after {Attempts} " +
					"attempt(s); last reason: {Reason}.",
					capability,
					modelId,
					backend.Name,
					attempt,
					outcome.Reason ?? "unknown");

				return null;
			}

			// Decide how long to wait before the next attempt. A server-sent Retry-After is authoritative and
			// shared: publish it so this backend's other concurrent probes pace to it too, and let the top of
			// the next loop iteration wait it out for this probe. Absent that explicit instruction, fall back to
			// a private exponential backoff that paces only this probe: a blind guess has no business throttling
			// the siblings the way a real rate-limit signal does.
			if (outcome.RetryAfter is { } serverCooldown)
			{
				PublishBackendCooldown(backend.Name, serverCooldown);
			}
			else
			{
				TimeSpan delay = ComputeBackoff(probing.RetryBaseDelaySeconds, attempt);
				if (delay > TimeSpan.Zero)
					await Task.Delay(delay, mTimeProvider, cancellationToken).ConfigureAwait(false);
			}
		}
	}

	/// <summary>
	/// Waits out the backend's shared rate-limit cooldown before the next request, if one a sibling probe
	/// published is still active. A non-zero wait is logged at debug level so the paced behavior is visible
	/// during diagnosis; a zero wait (the common case) returns immediately with no allocation beyond the
	/// dictionary lookup.
	/// </summary>
	/// <param name="backendName">The backend whose shared cooldown is honored.</param>
	/// <param name="capability">The probed capability, for the diagnostic log.</param>
	/// <param name="modelId">The probed model, for the diagnostic log.</param>
	/// <param name="cancellationToken">A token to cancel the wait.</param>
	/// <returns>A task that completes once the cooldown has elapsed.</returns>
	private Task WaitOutBackendCooldownAsync(
		string            backendName,
		string            capability,
		string            modelId,
		CancellationToken cancellationToken)
	{
		TimeSpan cooldown = GetRemainingBackendCooldown(backendName);
		if (cooldown <= TimeSpan.Zero) return Task.CompletedTask;

		mLogger.LogDebug(
			"{Capability}-support probe for model {Model} on backend {Backend} is waiting {Seconds:0.#}s for a " +
			"shared rate-limit cooldown a sibling probe published before retrying.",
			capability,
			modelId,
			backendName,
			cooldown.TotalSeconds);

		return Task.Delay(cooldown, mTimeProvider, cancellationToken);
	}

	/// <summary>
	/// Computes the exponential backoff delay before the next retry: <c>base * 2^(attempt-1)</c>.
	/// </summary>
	/// <param name="baseDelaySeconds">The configured base delay in seconds; <c>0</c> retries immediately.</param>
	/// <param name="attempt">The 1-based number of the attempt that just failed.</param>
	/// <returns>The delay to wait before the next attempt.</returns>
	private static TimeSpan ComputeBackoff(int baseDelaySeconds, int attempt)
	{
		if (baseDelaySeconds <= 0) return TimeSpan.Zero;

		return TimeSpan.FromSeconds(baseDelaySeconds * Math.Pow(2, attempt - 1));
	}

	/// <summary>
	/// Publishes a backend-requested cooldown to the shared store so the backend's other concurrent probes
	/// observe it. The new deadline is the later of any deadline already recorded and the one this attempt's
	/// <c>Retry-After</c> implies, so a longer outstanding cooldown is never shortened by a racing probe.
	/// </summary>
	/// <param name="backendName">The backend whose probes share the cooldown.</param>
	/// <param name="retryAfter">The cooldown the backend asked for; already clamped to the honored ceiling.</param>
	private void PublishBackendCooldown(string backendName, TimeSpan retryAfter)
	{
		DateTimeOffset until = mTimeProvider.GetUtcNow() + retryAfter;
		mBackendCooldowns.AddOrUpdate(
			backendName,
			until,
			(_, existing) => existing > until ? existing : until);
	}

	/// <summary>
	/// Returns how long the caller must wait before issuing the next request to the given backend, honoring
	/// a cooldown a sibling probe published after hitting a rate limit. Returns <see cref="TimeSpan.Zero"/>
	/// when no cooldown is active. An elapsed cooldown is left in place rather than removed: it is harmless
	/// (a past deadline yields zero) and pruning it would race the concurrent probes still reading it.
	/// </summary>
	/// <param name="backendName">The backend whose cooldown is consulted.</param>
	/// <returns>The remaining cooldown, or <see cref="TimeSpan.Zero"/> when none is active.</returns>
	private TimeSpan GetRemainingBackendCooldown(string backendName)
	{
		if (!mBackendCooldowns.TryGetValue(backendName, out DateTimeOffset until)) return TimeSpan.Zero;

		TimeSpan remaining = until - mTimeProvider.GetUtcNow();
		return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
	}

	/// <summary>
	/// Indicates whether the given backend currently has an active shared rate-limit cooldown — one a sibling
	/// probe published after hitting a rate limit whose deadline has not yet passed. Wraps
	/// <see cref="GetRemainingBackendCooldown"/> and reads the same store the retry loop consults, so it
	/// reflects exactly what <see cref="WaitOutBackendCooldownAsync"/> would honor at this instant.
	/// </summary>
	/// <param name="backendName">The backend whose shared cooldown is checked.</param>
	/// <returns>
	/// <see langword="true"/> when a cooldown for the backend is still in the future; otherwise
	/// <see langword="false"/> (none was ever published, or it has already elapsed).
	/// </returns>
	/// <remarks>
	/// Exposed as <see langword="internal"/> purely as a test-observability hook: the shared-cooldown test
	/// awaits the actual publication of a cooldown through this method instead of sleeping for a fixed span,
	/// removing the scheduling race between a sibling probe earning an HTTP 429 and the innocent sibling
	/// reading the resulting cooldown. It exposes no state a caller could mutate.
	/// </remarks>
	internal bool HasActiveBackendCooldown(string backendName) =>
		GetRemainingBackendCooldown(backendName) > TimeSpan.Zero;

	/// <summary>
	/// Reads a throttling response's <c>Retry-After</c> header into a positive, ceiling-clamped wait. The
	/// header comes in two RFC 9110 shapes: a delta in seconds (<c>Retry-After: 30</c>) or an HTTP date
	/// (<c>Retry-After: Wed, 21 Oct 2025 07:28:00 GMT</c>). <see cref="System.Net.Http.Headers"/> surfaces
	/// them as a delta or a date respectively, and a date is converted to a delta against the current
	/// clock. A missing, unparseable, zero, or negative value (an already-past date) yields
	/// <see langword="null"/> so the caller falls back to its computed exponential backoff. A value beyond
	/// <see cref="MaxHonoredRetryAfter"/> is clamped so a backend cannot stall discovery indefinitely.
	/// </summary>
	/// <param name="response">The throttling or server-error response whose header is read.</param>
	/// <returns>The clamped positive cooldown, or <see langword="null"/> when none is usable.</returns>
	private TimeSpan? ReadRetryAfter(HttpResponseMessage response)
	{
		RetryConditionHeaderValue? header = response.Headers.RetryAfter;
		if (header is null) return null;

		TimeSpan? wait = header.Delta;
		if (wait is null && header.Date is { } date) wait = date - mTimeProvider.GetUtcNow();

		// A non-positive wait (a zero delta or an already-elapsed date) carries no useful instruction, so the
		// computed backoff takes over; a positive wait is clamped to the honored ceiling.
		if (wait is not { } value || value <= TimeSpan.Zero) return null;

		return value <= MaxHonoredRetryAfter ? value : MaxHonoredRetryAfter;
	}

	/// <summary>
	/// Posts the supplied probe payload once, under a per-attempt timeout, and interprets the response
	/// into a conclusive result or a retryable / permanent inconclusive outcome.
	/// </summary>
	/// <param name="backend">The backend hosting the model.</param>
	/// <param name="modelId">The upstream model identifier being probed.</param>
	/// <param name="capability">A short label naming the probed capability, used only in diagnostics.</param>
	/// <param name="requestPath">The relative endpoint path to post the probe to.</param>
	/// <param name="payload">The probe request body to send.</param>
	/// <param name="timeoutSeconds">The per-attempt timeout in seconds.</param>
	/// <param name="cancellationToken">The caller's cancellation token.</param>
	/// <returns>The single-attempt outcome describing conclusiveness and, if inconclusive, retryability.</returns>
	private async Task<ProbeAttempt> ProbeOnceAsync(
		BackendContext    backend,
		string            modelId,
		string            capability,
		string            requestPath,
		JsonObject        payload,
		int               timeoutSeconds,
		CancellationToken cancellationToken)
	{
		using CancellationTokenSource timeoutSource = new(
			TimeSpan.FromSeconds(timeoutSeconds),
			mTimeProvider);

		using var linkedSource =
			CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

		try
		{
			using HttpClient client = mHttpClientProvider.CreateClient(backend);
			using HttpRequestMessage request = new(HttpMethod.Post, requestPath);
			request.Content = JsonContent.Create(payload, options: OpenAiSerialization.Options);

			using HttpResponseMessage response = await client
				                                     .SendAsync(
					                                     request,
					                                     HttpCompletionOption.ResponseHeadersRead,
					                                     linkedSource.Token)
				                                     .ConfigureAwait(false);

			if (response.IsSuccessStatusCode) return ProbeAttempt.Conclusive(true);

			// Throttling and server-side errors are transient: the model's capability is unchanged, the
			// backend just could not answer right now, so the attempt is retryable. A throttling response
			// often carries a Retry-After telling us exactly how long to wait; honor it (clamped) and surface
			// it so the retry loop (and the backend's sibling probes) pace to the backend's own ask instead
			// of a blind exponential guess.
			if (response.StatusCode is HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
			{
				TimeSpan? retryAfter = ReadRetryAfter(response);

				mLogger.LogDebug(
					"{Capability}-support probe for model {Model} on backend {Backend} hit a transient status " +
					"{Status}{RetryAfter}; will retry if attempts remain.",
					capability,
					modelId,
					backend.Name,
					(int)response.StatusCode,
					retryAfter is { } wait
						? $" (server asked to retry after {wait.TotalSeconds:0.#}s)"
						: string.Empty);

				return ProbeAttempt.Inconclusive(
					retryable: true,
					reason: $"transient HTTP {(int)response.StatusCode}",
					retryAfter: retryAfter);
			}

			// Auth failures say nothing about capability presence; they are permanent for this discovery run
			// and therefore inconclusive rather than a measured negative.
			if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
			{
				mLogger.LogDebug(
					"{Capability}-support probe for model {Model} on backend {Backend} was inconclusive " +
					"(permanent status {Status}); not retrying.",
					capability,
					modelId,
					backend.Name,
					(int)response.StatusCode);

				return ProbeAttempt.Inconclusive(
					retryable: false,
					reason: $"permanent HTTP {(int)response.StatusCode}");
			}

			// Any other non-auth 4xx (including 404 on a capability-specific endpoint) means the backend
			// declined the probed capability through this route. From the proxy operator's perspective the
			// exact cause (missing model vs. missing endpoint vs. rejected payload) is irrelevant: the capability
			// is not usable, so it is reported as a conclusive negative. The upstream rejection text is captured
			// and surfaced so the operator sees WHY a capability was withheld without first reproducing the run
			// under Debug; it is the symmetric counterpart to the inconclusive-probe warning the provider already
			// emits, and a conclusive false can be a correct negative, so it stays below Warning.
			string rejection = await ReadBodySnippetAsync(response, linkedSource.Token).ConfigureAwait(false);
			mLogger.LogInformation(
				"{Capability}-support probe for model {Model} on backend {Backend} was declined by a body-rejecting " +
				"status {Status}; treating the capability as unsupported. Upstream said: {Body}",
				capability,
				modelId,
				backend.Name,
				(int)response.StatusCode,
				rejection);

			return ProbeAttempt.Conclusive(false);
		}
		catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested &&
		                                         !cancellationToken.IsCancellationRequested)
		{
			// The attempt exceeded its own timeout. This is NOT retried: a model too slow to answer within the
			// per-attempt window will not answer a second identical attempt any faster, so retrying would only
			// burn the budget on more attempts that each expire before the model is ready, the very pattern
			// that made probes report inconclusive. The single adequate attempt is the whole budget; the probe
			// ends inconclusive here and the caller keeps the conservative default.
			mLogger.LogDebug(
				"{Capability}-support probe for model {Model} on backend {Backend} timed out; not retrying " +
				"(a timeout is not a transient fault). Raise the probe timeout if the model needs longer to load.",
				capability,
				modelId,
				backend.Name);

			return ProbeAttempt.Inconclusive(
				retryable: false,
				reason: $"timed out after {timeoutSeconds}s");
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// The caller cancelled discovery; surface it rather than masking it as a probe outcome.
			throw;
		}
		catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
		{
			// Transport failures (and the HttpClient's own timeout) are transient and therefore retryable.
			mLogger.LogDebug(
				exception,
				"{Capability}-support probe for model {Model} on backend {Backend} failed transiently; will " +
				"retry if attempts remain.",
				capability,
				modelId,
				backend.Name);

			return ProbeAttempt.Inconclusive(
				retryable: true,
				reason: $"transport fault: {exception.GetType().Name} ({exception.Message})");
		}
	}

	/// <summary>
	/// Reads a short, length-capped snippet of a rejection response body for diagnostics, collapsing
	/// failures (including a read that trips the attempt timeout) into a placeholder rather than letting
	/// them mask the probe outcome. The snippet is best-effort logging, never a control-flow signal.
	/// </summary>
	/// <param name="response">The non-success response whose body explains the rejection.</param>
	/// <param name="cancellationToken">The attempt's linked cancellation token.</param>
	/// <returns>The trimmed body snippet, or a placeholder when it cannot be read.</returns>
	private static async Task<string> ReadBodySnippetAsync(
		HttpResponseMessage response,
		CancellationToken   cancellationToken)
	{
		const int maxSnippetLength = 500;

		try
		{
			string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			body = body.Trim();

			if (body.Length == 0) return "(empty body)";

			return body.Length <= maxSnippetLength
				       ? body
				       : string.Concat(body.AsSpan(0, maxSnippetLength), "… (truncated)");
		}
		catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
		{
			return "(body unavailable)";
		}
	}

	/// <summary>
	/// Resolves the probing options for the supplied backend context. A draft context carries its own
	/// inline probing options and is authoritative, so no committed entry exists to look up. A committed
	/// context is resolved by name, returning a default (probes enabled) when the backend is not found
	/// (a defensive case that should not occur for a discovered model).
	/// </summary>
	/// <param name="backend">The backend context whose probing options are resolved.</param>
	/// <returns>The backend's probing options, or a default instance.</returns>
	private CapabilityProbingOptions GetProbingOptions(BackendContext backend)
	{
		return backend.Draft?.Probing != null
			       ? backend.Draft.Probing
			       : mOptions.CurrentValue.Backends.TryGetValue(backend.Name, out BackendOptions? committed)
				       ? committed.Probing
				       : new CapabilityProbingOptions();
	}

	/// <summary>
	/// The outcome of a single probe attempt: either a conclusive tri-state result, or an inconclusive
	/// result tagged with whether retrying it could yield a different answer.
	/// </summary>
	/// <param name="Result">
	/// The conclusive capability result, or <see langword="null"/> when the attempt was inconclusive.
	/// </param>
	/// <param name="Retryable">
	/// When <paramref name="Result"/> is <see langword="null"/>, indicates whether the failure was
	/// transient and worth retrying; ignored for a conclusive result.
	/// </param>
	/// <param name="Reason">
	/// A short human-readable cause for an inconclusive attempt (an HTTP status, a timeout, or a transport
	/// fault), surfaced when the probe finally gives up so the operator can see why; <see langword="null"/>
	/// for a conclusive result.
	/// </param>
	/// <param name="RetryAfter">
	/// The cooldown the backend explicitly requested via a <c>Retry-After</c> header on a throttling
	/// response, or <see langword="null"/> when none was given (in which case the computed exponential
	/// backoff applies). When present it is honored verbatim and shared across the backend's concurrent
	/// probes, so a rate limit paces the whole scan rather than being re-hit by every sibling. Only
	/// meaningful for a retryable inconclusive attempt.
	/// </param>
	private readonly record struct ProbeAttempt(
		bool?     Result,
		bool      Retryable,
		string?   Reason     = null,
		TimeSpan? RetryAfter = null)
	{
		/// <summary>Creates a conclusive attempt carrying the determined capability value.</summary>
		/// <param name="value">The conclusive capability result.</param>
		/// <returns>A conclusive <see cref="ProbeAttempt"/>.</returns>
		public static ProbeAttempt Conclusive(bool value) => new(value, Retryable: false);

		/// <summary>Creates an inconclusive attempt tagged with its retryability and a diagnostic reason.</summary>
		/// <param name="retryable">Whether the transient failure is worth retrying.</param>
		/// <param name="reason">A short human-readable cause, surfaced if the probe ultimately gives up.</param>
		/// <param name="retryAfter">The backend-requested cooldown, when a <c>Retry-After</c> header was present.</param>
		/// <returns>An inconclusive <see cref="ProbeAttempt"/>.</returns>
		public static ProbeAttempt Inconclusive(bool retryable, string? reason = null, TimeSpan? retryAfter = null) =>
			new(Result: null, retryable, reason, retryAfter);
	}

	/// <summary>
	/// Marks a chat-completion probe payload as streaming. This keeps probes fast: a non-streaming request
	/// buffers the <em>entire</em> generation before sending response headers, so the headers-only read in
	/// <see cref="ProbeOnceAsync"/> would block for the full reply to the "stay silent" prompt, easily past
	/// the per-attempt timeout on a slow or reasoning model. A streaming request returns its <c>200</c> headers
	/// the moment the stream opens (and any rejection still arrives as a pre-stream <c>4xx</c>), so the probe
	/// confirms support on time-to-first-byte and disposes the response without consuming the streamed body.
	/// Only the chat-completion probes need this; the embedding probe performs no generation.
	/// </summary>
	/// <param name="payload">The chat-completion probe body to mark as streaming.</param>
	/// <returns>The same payload instance with the streaming flag set, for fluent construction.</returns>
	private static JsonObject AddStreaming(JsonObject payload)
	{
		payload["stream"] = true;
		return payload;
	}

	/// <summary>
	/// Builds the minimal completion probe payload: a single user turn that asks the model to stay
	/// silent, carrying neither tools nor images so only the plain chat-completion path is exercised. No
	/// token-cap parameter is sent (see <see cref="SilentProbePrompt"/> for why) and the request streams
	/// so the probe confirms on the first frame rather than the full generation (see <see cref="AddStreaming"/>).
	/// </summary>
	/// <param name="modelId">The upstream model identifier to target.</param>
	/// <returns>The probe request body as a JSON object.</returns>
	private static JsonObject BuildCompletionProbePayload(string modelId) => AddStreaming(
		new JsonObject
		{
			["model"] = modelId,
			["messages"] = new JsonArray
			{
				new JsonObject
				{
					["role"] = "user",
					["content"] = SilentProbePrompt
				}
			}
		});

	/// <summary>
	/// Builds the minimal tool probe payload: a single user turn that asks the model to stay silent and
	/// one trivial function definition whose presence exercises the backend's <c>tools</c> handling. No
	/// token-cap parameter is sent (see <see cref="SilentProbePrompt"/> for why).
	/// </summary>
	/// <param name="modelId">The upstream model identifier to target.</param>
	/// <returns>The probe request body as a JSON object.</returns>
	private static JsonObject BuildToolProbePayload(string modelId) => AddStreaming(
		new JsonObject
		{
			["model"] = modelId,
			["messages"] = new JsonArray
			{
				new JsonObject
				{
					["role"] = "user",
					["content"] = SilentProbePrompt
				}
			},
			["tools"] = new JsonArray
			{
				new JsonObject
				{
					["type"] = "function",
					["function"] = new JsonObject
					{
						["name"] = "ping",
						["description"] = "A trivial probe function.",
						["parameters"] = new JsonObject
						{
							["type"] = "object",
							["properties"] = new JsonObject()
						}
					}
				}
			}
		});

	/// <summary>
	/// Builds the minimal vision probe payload: a single user turn whose content carries a small opaque
	/// placeholder image alongside a short text part that asks the model to stay silent, exercising the
	/// backend's image-input handling. No token-cap parameter is sent (see
	/// <see cref="SilentProbePrompt"/> for why).
	/// </summary>
	/// <param name="modelId">The upstream model identifier to target.</param>
	/// <returns>The probe request body as a JSON object.</returns>
	private static JsonObject BuildVisionProbePayload(string modelId) => AddStreaming(
		new JsonObject
		{
			["model"] = modelId,
			["messages"] = new JsonArray
			{
				new JsonObject
				{
					["role"] = "user",
					["content"] = new JsonArray
					{
						new JsonObject
						{
							["type"] = "text",
							["text"] = SilentProbePrompt
						},
						new JsonObject
						{
							["type"] = "image_url",
							["image_url"] = new JsonObject
							{
								["url"] = PlaceholderImageDataUri
							}
						}
					}
				}
			}
		});

	/// <summary>
	/// Builds the minimal embedding probe payload: a single short input string posted to the embeddings
	/// endpoint, whose acceptance exercises the backend's embedding-generation path.
	/// </summary>
	/// <param name="modelId">The upstream model identifier to target.</param>
	/// <returns>The probe request body as a JSON object.</returns>
	private static JsonObject BuildEmbeddingProbePayload(string modelId) => new()
	{
		["model"] = modelId,
		["input"] = "ping"
	};
}
