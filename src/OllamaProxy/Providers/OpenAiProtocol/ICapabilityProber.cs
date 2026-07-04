// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Providers.OpenAiProtocol;

/// <summary>
/// Stage 2 of capability detection: actively probes a backend to confirm a model's capabilities by
/// sending minimal throwaway requests and interpreting the outcome. A completion probe sends a one-token
/// chat completion; a tool probe advertises a dummy function; a vision probe attaches a small placeholder
/// image; an embedding probe posts a tiny input to the embeddings endpoint. A successful response (or a
/// content-level rejection) tells us whether the backend honors the capability for that model, whereas
/// transport failures and timeouts are retried with backoff and, if still unresolved, reported as
/// inconclusive so the caller retains the conservative default rather than mislabel the model. Each probe
/// costs an upstream round trip, so probing is gated behind configuration and invoked only when cheaper
/// metadata signals are inconclusive.
/// </summary>
interface ICapabilityProber
{
	/// <summary>
	/// Probes whether the specified model serves chat completion on the given backend.
	/// </summary>
	/// <param name="backend">The backend hosting the model.</param>
	/// <param name="modelId">The upstream model identifier to probe.</param>
	/// <param name="cancellationToken">A token to cancel the operation.</param>
	/// <returns>
	/// <see langword="true"/> when the probe confirms completion support, <see langword="false"/> when
	/// it is confirmed absent (for example an embedding-only model that rejects the completion request),
	/// or <see langword="null"/> when the probe was inconclusive (for example a timeout or transport
	/// error that persisted across retries), in which case the caller retains the conservative
	/// completion-capable default.
	/// </returns>
	/// <exception cref="ArgumentNullException"><paramref name="backend"/> is <see langword="null"/>.</exception>
	/// <exception cref="ArgumentException">
	/// <paramref name="modelId"/> is empty or consists only of white-space characters.
	/// </exception>
	Task<bool?> ProbeCompletionSupportAsync(
		BackendContext    backend,
		string            modelId,
		CancellationToken cancellationToken);

	/// <summary>
	/// Probes whether the specified model accepts tool definitions on the given backend.
	/// </summary>
	/// <param name="backend">The backend hosting the model.</param>
	/// <param name="modelId">The upstream model identifier to probe.</param>
	/// <param name="cancellationToken">A token to cancel the operation.</param>
	/// <returns>
	/// <see langword="true"/> when the probe confirms tool support, <see langword="false"/> when it is
	/// confirmed absent, or <see langword="null"/> when the probe was inconclusive (for example a
	/// timeout or transport error that persisted across retries), in which case the caller retains the
	/// conservative default.
	/// </returns>
	/// <exception cref="ArgumentNullException"><paramref name="backend"/> is <see langword="null"/>.</exception>
	/// <exception cref="ArgumentException">
	/// <paramref name="modelId"/> is empty or consists only of white-space characters.
	/// </exception>
	Task<bool?> ProbeToolSupportAsync(BackendContext backend, string modelId, CancellationToken cancellationToken);

	/// <summary>
	/// Probes whether the specified model accepts image input on the given backend.
	/// </summary>
	/// <param name="backend">The backend hosting the model.</param>
	/// <param name="modelId">The upstream model identifier to probe.</param>
	/// <param name="cancellationToken">A token to cancel the operation.</param>
	/// <returns>
	/// <see langword="true"/> when the probe confirms vision support, <see langword="false"/> when it is
	/// confirmed absent, or <see langword="null"/> when the probe was inconclusive (for example a
	/// timeout or transport error that persisted across retries), in which case the caller retains the
	/// conservative default.
	/// </returns>
	/// <exception cref="ArgumentNullException"><paramref name="backend"/> is <see langword="null"/>.</exception>
	/// <exception cref="ArgumentException">
	/// <paramref name="modelId"/> is empty or consists only of white-space characters.
	/// </exception>
	Task<bool?> ProbeVisionSupportAsync(BackendContext backend, string modelId, CancellationToken cancellationToken);

	/// <summary>
	/// Probes whether the specified model produces embedding vectors on the given backend.
	/// </summary>
	/// <param name="backend">The backend hosting the model.</param>
	/// <param name="modelId">The upstream model identifier to probe.</param>
	/// <param name="cancellationToken">A token to cancel the operation.</param>
	/// <returns>
	/// <see langword="true"/> when the probe confirms embedding support, <see langword="false"/> when it
	/// is confirmed absent, or <see langword="null"/> when the probe was inconclusive (for example a
	/// timeout or transport error that persisted across retries), in which case the caller retains the
	/// conservative default.
	/// </returns>
	/// <exception cref="ArgumentNullException"><paramref name="backend"/> is <see langword="null"/>.</exception>
	/// <exception cref="ArgumentException">
	/// <paramref name="modelId"/> is empty or consists only of white-space characters.
	/// </exception>
	Task<bool?> ProbeEmbeddingSupportAsync(BackendContext backend, string modelId, CancellationToken cancellationToken);
}
