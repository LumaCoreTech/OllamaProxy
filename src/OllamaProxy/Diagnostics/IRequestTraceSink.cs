// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Diagnostics;

/// <summary>
/// Persists a completed <see cref="RequestTrace"/>. The tracing middleware hands each finished flow to
/// the sink, which is responsible for durably recording it and for enforcing any retention policy. The
/// contract is intentionally minimal so the storage strategy can change without touching the request
/// pipeline.
/// </summary>
interface IRequestTraceSink
{
	/// <summary>
	/// Writes a completed trace to durable storage, applying the configured retention policy.
	/// </summary>
	/// <param name="trace">The completed trace to persist.</param>
	/// <param name="cancellationToken">A token to cancel the operation.</param>
	/// <returns>A task that completes when the trace has been written.</returns>
	Task WriteAsync(RequestTrace trace, CancellationToken cancellationToken);
}
