// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json.Serialization;

namespace OllamaProxy.Diagnostics;

/// <summary>
/// A single recorded step within a <see cref="RequestTrace"/>. Each entry pins the <see cref="Stage"/>
/// it belongs to, the wall-clock <see cref="TimestampUtc"/> it was recorded at, a short human-readable
/// <see cref="Summary"/>, and the optional structured <see cref="Detail"/> (headers, body text,
/// provenance fields). The body text, when present, is already redacted and truncated by the
/// recorder; <see cref="Truncated"/> flags whether it was cut at the configured byte limit.
/// </summary>
/// <param name="Stage">The flow stage this entry describes.</param>
/// <param name="TimestampUtc">The instant the entry was recorded, in UTC.</param>
/// <param name="Summary">A short, human-readable description of the entry.</param>
/// <param name="Detail">The structured detail map (headers, provenance fields), or <see langword="null"/>.</param>
/// <param name="Body">The captured body text, already redacted and truncated, or <see langword="null"/>.</param>
/// <param name="Truncated">Whether <paramref name="Body"/> was truncated at the configured byte limit.</param>
sealed record TraceEntry(
	[property: JsonConverter(typeof(JsonStringEnumConverter<TraceStage>))]
	TraceStage Stage,
	DateTimeOffset                       TimestampUtc,
	string                               Summary,
	IReadOnlyDictionary<string, string>? Detail = null,
	[property: JsonConverter(typeof(TraceBodyJsonConverter))]
	string? Body = null,
	bool Truncated = false);

/// <summary>
/// Accumulates the ordered <see cref="TraceEntry"/> steps of a single request-response flow. A trace
/// is created by the tracing middleware at the start of a request, populated as the request travels
/// through the endpoint and provider layers (the provider layer reaches it through the ambient
/// <see cref="IRequestTraceAccessor"/>), and handed to the sink once the response completes. The type
/// is thread-safe: appends are guarded so the singleton provider layer and the request pipeline can
/// record into the same trace without racing.
/// </summary>
sealed class RequestTrace
{
	private readonly List<TraceEntry> mEntries = [];
	private readonly Lock             mGate    = new();

	/// <summary>
	/// Initializes a new instance of the <see cref="RequestTrace"/> class.
	/// </summary>
	/// <param name="correlationId">The unique identifier correlating this trace with its flow.</param>
	/// <param name="startedUtc">The instant the flow began, in UTC.</param>
	/// <param name="method">The inbound HTTP method.</param>
	/// <param name="path">The inbound request path, without the query string.</param>
	/// <exception cref="ArgumentNullException">
	/// <paramref name="correlationId"/>, <paramref name="method"/>, or <paramref name="path"/> is
	/// <see langword="null"/>.
	/// </exception>
	public RequestTrace(
		string         correlationId,
		DateTimeOffset startedUtc,
		string         method,
		string         path)
	{
		ArgumentNullException.ThrowIfNull(correlationId);
		ArgumentNullException.ThrowIfNull(method);
		ArgumentNullException.ThrowIfNull(path);

		CorrelationId = correlationId;
		StartedUtc = startedUtc;
		Method = method;
		Path = path;
	}

	/// <summary>Gets the unique identifier correlating this trace with its request-response flow.</summary>
	public string CorrelationId { get; }

	/// <summary>Gets the instant the flow began, in UTC.</summary>
	public DateTimeOffset StartedUtc { get; }

	/// <summary>Gets the inbound HTTP method.</summary>
	public string Method { get; }

	/// <summary>Gets the inbound request path, without the query string.</summary>
	public string Path { get; }

	/// <summary>
	/// Gets a snapshot of the recorded entries in the order they were added. The returned array is an
	/// isolated copy, so it is safe to enumerate while further entries are being appended.
	/// </summary>
	public IReadOnlyList<TraceEntry> Entries
	{
		get
		{
			lock (mGate) return mEntries.ToArray();
		}
	}

	/// <summary>
	/// Appends an entry to the trace. Thread-safe: concurrent appends from the request pipeline and the
	/// provider layer are serialized so no entry is lost or interleaved partially.
	/// </summary>
	/// <param name="entry">The entry to append.</param>
	/// <exception cref="ArgumentNullException"><paramref name="entry"/> is <see langword="null"/>.</exception>
	public void Add(TraceEntry entry)
	{
		ArgumentNullException.ThrowIfNull(entry);

		lock (mGate) mEntries.Add(entry);
	}
}
