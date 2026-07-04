// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text;

namespace OllamaProxy.Diagnostics;

/// <summary>
/// The active <see cref="ITraceScope"/> that writes into a concrete <see cref="RequestTrace"/>. It is
/// created by the tracing middleware for the lifetime of one traced request and published as the
/// ambient scope so the endpoint and provider layers can annotate it. Inline attachments in the request
/// bodies it records are replaced with metadata placeholders (when enabled) and bodies are truncated to
/// a configured byte budget (when one is set), bounding the size of any single trace file regardless of
/// how long a streamed response runs.
/// </summary>
sealed class TraceScope : ITraceScope
{
	private readonly RequestTrace mTrace;
	private readonly int?         mMaxBodyBytes;
	private readonly bool         mRedactAttachments;
	private readonly TimeProvider mTimeProvider;

	/// <summary>
	/// Initializes a new instance of the <see cref="TraceScope"/> class.
	/// </summary>
	/// <param name="trace">The trace this scope records into.</param>
	/// <param name="maxBodyBytes">
	/// The per-body capture limit, in bytes, or <see langword="null"/> to capture bodies in full.
	/// </param>
	/// <param name="redactAttachments">
	/// Whether inline attachments in the recorded request bodies are replaced with metadata placeholders.
	/// </param>
	/// <param name="timeProvider">The clock used to timestamp recorded entries.</param>
	/// <exception cref="ArgumentNullException">
	/// <paramref name="trace"/> or <paramref name="timeProvider"/> is
	/// <see langword="null"/>.
	/// </exception>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="maxBodyBytes"/> is not greater than zero.</exception>
	public TraceScope(
		RequestTrace trace,
		int?         maxBodyBytes,
		bool         redactAttachments,
		TimeProvider timeProvider)
	{
		ArgumentNullException.ThrowIfNull(trace);
		ArgumentNullException.ThrowIfNull(timeProvider);

		// Pass the public parameter name explicitly: CallerArgumentExpression would otherwise capture the
		// local "limit", leaking an internal name into ParamName instead of the documented "maxBodyBytes".
		if (maxBodyBytes is { } limit) ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit, nameof(maxBodyBytes));

		mTrace = trace;
		mMaxBodyBytes = maxBodyBytes;
		mRedactAttachments = redactAttachments;
		mTimeProvider = timeProvider;
	}

	/// <inheritdoc/>
	public bool IsEnabled => true;

	/// <inheritdoc/>
	public void RecordReasoning(
		string? resolvedEffort,
		string  source,
		string? backendDefault,
		string? wireField)
	{
		ArgumentNullException.ThrowIfNull(source);

		Dictionary<string, string> detail = new(StringComparer.Ordinal)
		{
			["resolvedEffort"] = resolvedEffort ?? "(unspecified)",
			["source"] = source,
			["backendDefault"] = backendDefault ?? "(none)",
			["wireField"] = wireField ?? "(none sent)"
		};

		Add(
			TraceStage.ReasoningResolution,
			$"Reasoning resolved to '{resolvedEffort ?? "unspecified"}' from {source}.",
			detail);
	}

	/// <inheritdoc/>
	public void RecordBackendRequest(string backendName, string path, string body)
	{
		ArgumentNullException.ThrowIfNull(backendName);
		ArgumentNullException.ThrowIfNull(path);
		ArgumentNullException.ThrowIfNull(body);

		Dictionary<string, string> detail = new(StringComparer.Ordinal)
		{
			["backend"] = backendName,
			["path"] = path
		};

		AddWithBody(TraceStage.BackendRequest, $"POST {backendName}/{path}", detail, body);
	}

	/// <inheritdoc/>
	public void RecordBackendReasoning(string backendName, string reasoning)
	{
		ArgumentNullException.ThrowIfNull(backendName);
		ArgumentNullException.ThrowIfNull(reasoning);

		Dictionary<string, string> detail = new(StringComparer.Ordinal) { ["backend"] = backendName };

		AddWithBody(TraceStage.BackendReasoning, $"Reasoning from {backendName}", detail, reasoning);
	}

	/// <inheritdoc/>
	public void RecordBackendResponse(string backendName, string body)
	{
		ArgumentNullException.ThrowIfNull(backendName);
		ArgumentNullException.ThrowIfNull(body);

		Dictionary<string, string> detail = new(StringComparer.Ordinal) { ["backend"] = backendName };

		AddWithBody(TraceStage.BackendResponse, $"Response from {backendName}", detail, body);
	}

	/// <inheritdoc/>
	public void Note(string summary)
	{
		ArgumentNullException.ThrowIfNull(summary);

		Add(TraceStage.Note, summary, detail: null);
	}

	/// <summary>
	/// Records an inbound or outbound transport stage carrying redacted headers and a captured body.
	/// Used by the tracing middleware, which owns the HTTP edges of the flow.
	/// </summary>
	/// <param name="stage">
	/// The transport stage (<see cref="TraceStage.InboundRequest"/> or
	/// <see cref="TraceStage.OutboundResponse"/>).
	/// </param>
	/// <param name="summary">A short, human-readable description of the stage.</param>
	/// <param name="detail">The redacted header/metadata map.</param>
	/// <param name="body">The captured body text.</param>
	/// <param name="alreadyTruncated">
	/// Whether <paramref name="body"/> was already cut short before it reached the scope, set by the
	/// middleware when the response stream was teed under a byte budget that the capture hit. The recorded
	/// entry is flagged truncated when either this is <see langword="true"/> or the scope's own cap cuts
	/// the body, so an upstream cut is never silently lost just because the shortened text now fits.
	/// </param>
	public void RecordTransport(
		TraceStage                          stage,
		string                              summary,
		IReadOnlyDictionary<string, string> detail,
		string                              body,
		bool                                alreadyTruncated = false)
	{
		ArgumentNullException.ThrowIfNull(summary);
		ArgumentNullException.ThrowIfNull(detail);
		ArgumentNullException.ThrowIfNull(body);

		AddWithBody(stage, summary, detail, body, alreadyTruncated);
	}

	/// <summary>
	/// Adds an entry with a body, first replacing inline attachments with metadata placeholders on the
	/// request stages (when redaction is enabled), then truncating the body to the configured byte budget
	/// (when one is set) and flagging the entry when the limit was hit so a reader knows the captured text
	/// is incomplete.
	/// </summary>
	/// <param name="stage">The stage the entry belongs to.</param>
	/// <param name="summary">A short, human-readable description.</param>
	/// <param name="detail">The structured detail map.</param>
	/// <param name="body">The body text to capture (sanitized and truncated as needed).</param>
	/// <param name="alreadyTruncated">
	/// Whether the body was already cut short by an upstream capture before it reached the scope. Combined
	/// (logical OR) with the scope's own truncation so an entry stays flagged even when the already-cut
	/// body now fits under this scope's budget.
	/// </param>
	private void AddWithBody(
		TraceStage                          stage,
		string                              summary,
		IReadOnlyDictionary<string, string> detail,
		string                              body,
		bool                                alreadyTruncated = false)
	{
		// Attachments only ride on the request bodies (the inbound images[] array and its translated
		// backend data: URLs); responses never carry them, so the scan is confined to those two stages.
		// Redaction runs before truncation so a cap measures the small placeholder, never a half-cut blob.
		string sanitized = mRedactAttachments && stage is TraceStage.InboundRequest or TraceStage.BackendRequest
			                   ? TraceBodySanitizer.Redact(body)
			                   : body;

		(string captured, bool truncated) = Truncate(sanitized);

		mTrace.Add(
			new TraceEntry(stage, mTimeProvider.GetUtcNow(), summary, detail, captured, truncated || alreadyTruncated));
	}

	/// <summary>
	/// Adds a body-less entry timestamped from the scope's clock.
	/// </summary>
	/// <param name="stage">The stage the entry belongs to.</param>
	/// <param name="summary">A short, human-readable description.</param>
	/// <param name="detail">The structured detail map, or <see langword="null"/>.</param>
	private void Add(TraceStage stage, string summary, IReadOnlyDictionary<string, string>? detail) =>
		mTrace.Add(new TraceEntry(stage, mTimeProvider.GetUtcNow(), summary, detail));

	/// <summary>
	/// Truncates a body to at most <see cref="mMaxBodyBytes"/> UTF-8 bytes, cutting on a UTF-8 code-point
	/// boundary so the captured text stays valid, and reporting whether anything was dropped. When no cap
	/// is configured the body is returned in full.
	/// </summary>
	/// <param name="body">The body text to bound.</param>
	/// <returns>The captured (possibly truncated) text and whether it was truncated.</returns>
	private (string Text, bool Truncated) Truncate(string body)
	{
		if (mMaxBodyBytes is not { } limit) return (body, false);

		byte[] bytes = Encoding.UTF8.GetBytes(body);
		if (bytes.Length <= limit) return (body, false);

		// Back up off any UTF-8 continuation byte (0b10xxxxxx) so the cut never splits a multi-byte code
		// point, which would otherwise decode to a replacement character.
		int cut = limit;
		while (cut > 0 && (bytes[cut] & 0xC0) == 0x80) cut--;

		return (Encoding.UTF8.GetString(bytes.AsSpan(0, cut)), true);
	}
}
