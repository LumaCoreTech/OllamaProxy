// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Diagnostics;

/// <summary>
/// The serialization shape written to a trace file. It is a stable, flat projection of a
/// <see cref="RequestTrace"/> (the top-level flow metadata plus its ordered entries) kept separate
/// from the in-memory accumulator so the on-disk format does not depend on the accumulator's internal
/// members (its synchronization gate, mutable list) and can evolve independently.
/// </summary>
/// <param name="CorrelationId">The identifier correlating the trace with its flow.</param>
/// <param name="StartedUtc">The instant the flow began, in UTC.</param>
/// <param name="Method">The inbound HTTP method.</param>
/// <param name="Path">The inbound request path, without the query string.</param>
/// <param name="Entries">The ordered stage entries recorded during the flow.</param>
sealed record TraceDocument(
	string                    CorrelationId,
	DateTimeOffset            StartedUtc,
	string                    Method,
	string                    Path,
	IReadOnlyList<TraceEntry> Entries)
{
	/// <summary>
	/// Projects a completed <see cref="RequestTrace"/> onto its serializable document shape, snapshotting
	/// the entries so the result is independent of any further (post-completion) appends.
	/// </summary>
	/// <param name="trace">The completed trace to project.</param>
	/// <returns>The serializable document.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="trace"/> is <see langword="null"/>.</exception>
	public static TraceDocument From(RequestTrace trace)
	{
		ArgumentNullException.ThrowIfNull(trace);

		return new TraceDocument(
			trace.CorrelationId,
			trace.StartedUtc,
			trace.Method,
			trace.Path,
			trace.Entries);
	}
}
