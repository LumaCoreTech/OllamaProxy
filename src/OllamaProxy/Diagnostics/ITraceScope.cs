// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Diagnostics;

/// <summary>
/// The narrow surface the endpoint and provider layers use to annotate the current request's trace
/// without taking a hard dependency on the concrete <see cref="RequestTrace"/> or on whether tracing is
/// even active. Recording is always safe: when tracing is off the ambient scope is a no-op, so callers can
/// record provenance unconditionally and pay nothing when disabled.
/// </summary>
interface ITraceScope
{
	/// <summary>
	/// Gets a value indicating whether this scope records anything. Callers can short-circuit the
	/// construction of an expensive detail payload by checking this first; recording on a disabled
	/// scope is otherwise a harmless no-op.
	/// </summary>
	bool IsEnabled { get; }

	/// <summary>
	/// Records the reasoning-effort decision: the effort that was resolved, where it came from, the
	/// backend default that was in play, and the concrete wire field stamped onto the upstream payload.
	/// This is the provenance answer to "why did the proxy send (or not send) a reasoning directive?".
	/// </summary>
	/// <param name="resolvedEffort">The resolved effort, or <see langword="null"/> when unspecified.</param>
	/// <param name="source">A short token identifying the decision source (request, backend default, unspecified).</param>
	/// <param name="backendDefault">The backend's configured default effort, or <see langword="null"/>.</param>
	/// <param name="wireField">The wire field written to the payload, or <see langword="null"/> when none was sent.</param>
	void RecordReasoning(
		string? resolvedEffort,
		string  source,
		string? backendDefault,
		string? wireField);

	/// <summary>
	/// Records the translated request the proxy is about to send upstream to the backend.
	/// </summary>
	/// <param name="backendName">The logical backend the request targets.</param>
	/// <param name="path">The backend-relative request path.</param>
	/// <param name="body">The serialized backend request body.</param>
	void RecordBackendRequest(string backendName, string path, string body);

	/// <summary>
	/// Records the backend's reasoning (chain-of-thought) text, aggregated from the streamed
	/// <c>reasoning_content</c> deltas. Kept distinct from <see cref="RecordBackendResponse"/> so the
	/// reasoning trail and the visible answer each get their own capture budget.
	/// </summary>
	/// <param name="backendName">The logical backend that produced the reasoning.</param>
	/// <param name="reasoning">The aggregated reasoning text assembled from the streamed deltas.</param>
	void RecordBackendReasoning(string backendName, string reasoning);

	/// <summary>
	/// Records the response the backend returned, aggregated to a single text for streaming responses.
	/// </summary>
	/// <param name="backendName">The logical backend that produced the response.</param>
	/// <param name="body">The backend response body, aggregated when the response was streamed.</param>
	void RecordBackendResponse(string backendName, string body);

	/// <summary>
	/// Records a free-form annotation that does not map to a single transport stage.
	/// </summary>
	/// <param name="summary">The note text.</param>
	void Note(string summary);
}
