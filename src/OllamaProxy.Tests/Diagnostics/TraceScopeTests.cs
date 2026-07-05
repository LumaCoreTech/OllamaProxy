// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text;

using OllamaProxy.Diagnostics;

namespace OllamaProxy.Tests.Diagnostics;

/// <summary>
/// Tests for <see cref="TraceScope"/>, the recording choke point: which stages get sanitized, and how bodies are
/// bounded.
/// </summary>
/// <remarks>
/// <see cref="TraceScope"/> is where every recorded body passes through two gates — attachment redaction (request
/// stages only, toggleable) and truncation (toggleable via a nullable cap) — before landing in the trace. The
/// sections below follow a body from "which stages are touched" to "how far it is cut":
/// <list type="number">
///     <item>
///         <description>
///         Construction: null trace / clock and a non-positive cap are rejected; null is the valid "unbounded"
///         choice (WhenTraceNull, WhenMaxBodyBytesNotPositive, WhenMaxBodyBytesNull).
///         </description>
///     </item>
///     <item>
///         <description>
///         Redaction scope: request stages are sanitized, response stages are not, and the toggle disables it
///         entirely (InboundRequest/BackendRequest redacted, BackendResponse/Outbound kept,
///         WhenRedactionDisabled).
///         </description>
///     </item>
///     <item>
///         <description>
///         Order: redaction runs before truncation, so a tiny cap measures the placeholder, not the blob it
///         replaced (WhenRedactedThenTruncated).
///         </description>
///     </item>
///     <item>
///         <description>
///         Truncation: a null cap keeps the whole body; a cap larger than the body keeps it; a smaller cap cuts
///         it and flags truncation on a UTF-8 boundary (WhenNoCap, WhenWithinCap, WhenExceedsCap,
///         WhenCutSplitsCodePoint).
///         </description>
///     </item>
///     <item>
///         <description>
///         Body-less: reasoning and notes record no body and never flag truncation (RecordReasoning, Note).
///         </description>
///     </item>
/// </list>
/// </remarks>
[Trait("Category", "Unit")]
public sealed class TraceScopeTests
{
	/// <summary>A JSON body carrying one bare base64 image inside an <c>images</c> array.</summary>
	private const string ImageBody = """{"images":["AAAAAAAAAAAAAAAA"]}""";

	/// <summary>The marker the sanitizer substitutes for <see cref="ImageBody"/>'s 16-char payload (~12 bytes).</summary>
	private const string ImageMarkerBody = """{"images":["[image omitted: ~12 B]"]}""";

	/// <summary>
	/// Creates a trace and a scope recording into it, so a test can drive the scope and then read back the
	/// entries the trace accumulated.
	/// </summary>
	/// <param name="maxBodyBytes">The per-body cap, or <see langword="null"/> for unbounded capture.</param>
	/// <param name="redactAttachments">Whether request-stage attachments are redacted.</param>
	/// <returns>The backing trace and the scope that records into it.</returns>
	private static (RequestTrace Trace, TraceScope Scope) CreateScope(
		int? maxBodyBytes      = null,
		bool redactAttachments = true)
	{
		RequestTrace trace = new("corr-1", DateTimeOffset.UnixEpoch, "POST", "/api/chat");
		TraceScope scope = new(trace, maxBodyBytes, redactAttachments, TimeProvider.System);
		return (trace, scope);
	}

	/// <summary>
	/// An empty redacted-header map for the transport-stage helper.
	/// </summary>
	private static readonly IReadOnlyDictionary<string, string> EmptyDetail =
		new Dictionary<string, string>(StringComparer.Ordinal);

	// --- 1. Construction ---

	/// <summary>
	/// Verifies that <see cref="TraceScope"/> rejects a <see langword="null"/> trace, since it has nothing
	/// to record into without one.
	/// </summary>
	[Fact]
	public void Constructor_WhenTraceNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var ex = Assert.Throws<ArgumentNullException>(() => new TraceScope(
			null!,
			maxBodyBytes: null,
			redactAttachments: true,
			TimeProvider.System));
		Assert.Equal("trace", ex.ParamName);
	}

	/// <summary>
	/// Verifies that <see cref="TraceScope"/> rejects a <see langword="null"/> time provider, since every
	/// recorded entry is timestamped from it.
	/// </summary>
	[Fact]
	public void Constructor_WhenTimeProviderNull_ThrowsArgumentNullException()
	{
		// Arrange
		RequestTrace trace = new("corr-1", DateTimeOffset.UnixEpoch, "POST", "/api/chat");

		// Act + Assert
		var ex = Assert.Throws<ArgumentNullException>(() => new TraceScope(
			trace,
			maxBodyBytes: null,
			redactAttachments: true,
			timeProvider: null!));
		Assert.Equal("timeProvider", ex.ParamName);
	}

	/// <summary>
	/// Verifies that <see cref="TraceScope"/> rejects a present, non-positive body cap, the same contract
	/// as <see cref="CapturingStream"/>: zero or negative is invalid, only <see langword="null"/> means
	/// "unbounded".
	/// </summary>
	/// <param name="maxBodyBytes">The invalid cap to construct with.</param>
	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	public void Constructor_WhenMaxBodyBytesNotPositive_ThrowsArgumentOutOfRangeException(int maxBodyBytes)
	{
		// Arrange
		RequestTrace trace = new("corr-1", DateTimeOffset.UnixEpoch, "POST", "/api/chat");

		// Act + Assert
		var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new TraceScope(
			trace,
			maxBodyBytes,
			redactAttachments: true,
			TimeProvider.System));
		Assert.Equal("maxBodyBytes", ex.ParamName);
	}

	/// <summary>
	/// Verifies that <see cref="TraceScope"/> accepts a <see langword="null"/> body cap, the opt-in
	/// "capture in full" setting.
	/// </summary>
	[Fact]
	public void Constructor_WhenMaxBodyBytesNull_DoesNotThrow()
	{
		// Arrange
		RequestTrace trace = new("corr-1", DateTimeOffset.UnixEpoch, "POST", "/api/chat");

		// Act
		TraceScope scope = new(trace, maxBodyBytes: null, redactAttachments: true, TimeProvider.System);

		// Assert: a well-formed scope reports itself enabled.
		Assert.True(scope.IsEnabled);
	}

	// --- 2. Redaction scope ---

	/// <summary>
	/// Verifies that <see cref="TraceScope.RecordTransport"/> redacts an inline image on the inbound
	/// request stage, since that is where the client's multimodal payload first enters the trace.
	/// </summary>
	[Fact]
	public void RecordTransport_WhenInboundRequestHasImage_RedactsBody()
	{
		// Arrange
		(RequestTrace trace, TraceScope scope) = CreateScope();

		// Act
		scope.RecordTransport(TraceStage.InboundRequest, "in", EmptyDetail, ImageBody);

		// Assert
		TraceEntry entry = Assert.Single(trace.Entries);
		Assert.Equal(ImageMarkerBody, entry.Body);
	}

	/// <summary>
	/// Verifies that <see cref="TraceScope.RecordBackendRequest"/> redacts an inline image on the backend
	/// request stage, since the translated upstream request carries the same payload as a data URL.
	/// </summary>
	[Fact]
	public void RecordBackendRequest_WhenBodyHasImage_RedactsBody()
	{
		// Arrange
		(RequestTrace trace, TraceScope scope) = CreateScope();

		// Act
		scope.RecordBackendRequest("ollama", "chat/completions", ImageBody);

		// Assert
		TraceEntry entry = Assert.Single(trace.Entries);
		Assert.Equal(ImageMarkerBody, entry.Body);
	}

	/// <summary>
	/// Verifies that <see cref="TraceScope.RecordBackendResponse"/> leaves a body untouched even when it
	/// resembles an attachment, since responses never carry inline uploads and the scan is confined to the
	/// request stages.
	/// </summary>
	[Fact]
	public void RecordBackendResponse_WhenBodyResemblesImage_KeepsBodyVerbatim()
	{
		// Arrange
		(RequestTrace trace, TraceScope scope) = CreateScope();

		// Act
		scope.RecordBackendResponse("ollama", ImageBody);

		// Assert: response stages are out of redaction scope, so the body is stored as-is.
		TraceEntry entry = Assert.Single(trace.Entries);
		Assert.Equal(ImageBody, entry.Body);
	}

	/// <summary>
	/// Verifies that <see cref="TraceScope.RecordTransport"/> leaves an outbound response body untouched
	/// even when it resembles an attachment, since redaction is confined to the inbound and backend
	/// request stages.
	/// </summary>
	[Fact]
	public void RecordTransport_WhenOutboundResponseResemblesImage_KeepsBodyVerbatim()
	{
		// Arrange
		(RequestTrace trace, TraceScope scope) = CreateScope();

		// Act
		scope.RecordTransport(TraceStage.OutboundResponse, "out", EmptyDetail, ImageBody);

		// Assert
		TraceEntry entry = Assert.Single(trace.Entries);
		Assert.Equal(ImageBody, entry.Body);
	}

	/// <summary>
	/// Verifies that <see cref="TraceScope.RecordBackendRequest"/> keeps an inline image verbatim when
	/// attachment redaction is disabled, the opt-out path that captures the raw payload.
	/// </summary>
	[Fact]
	public void RecordBackendRequest_WhenRedactionDisabled_KeepsBodyVerbatim()
	{
		// Arrange
		(RequestTrace trace, TraceScope scope) = CreateScope(redactAttachments: false);

		// Act
		scope.RecordBackendRequest("ollama", "chat/completions", ImageBody);

		// Assert: with redaction off, even a request stage stores the raw image bytes.
		TraceEntry entry = Assert.Single(trace.Entries);
		Assert.Equal(ImageBody, entry.Body);
	}

	// --- 3. Order: redact before truncate ---

	/// <summary>
	/// Verifies that <see cref="TraceScope.RecordBackendRequest"/> redacts the attachment <em>before</em>
	/// applying the byte cap, so a cap far smaller than the raw blob still yields the intact placeholder
	/// rather than a half-cut base64 string.
	/// </summary>
	[Fact]
	public void RecordBackendRequest_WhenRedactedThenTruncated_MeasuresPlaceholderNotBlob()
	{
		// Arrange: a 36-byte placeholder under a 64-byte cap survives whole; the raw 28-byte blob body it
		// replaced is irrelevant because redaction runs first.
		(RequestTrace trace, TraceScope scope) = CreateScope(maxBodyBytes: 64);

		// Act
		scope.RecordBackendRequest("ollama", "chat/completions", ImageBody);

		// Assert: the placeholder is intact and not flagged truncated.
		TraceEntry entry = Assert.Single(trace.Entries);
		Assert.Equal(ImageMarkerBody, entry.Body);
		Assert.False(entry.Truncated);
	}

	// --- 4. Truncation ---

	/// <summary>
	/// Verifies that <see cref="TraceScope.RecordBackendResponse"/> captures a body in full when no cap is
	/// configured, the unbounded-by-default behavior.
	/// </summary>
	[Fact]
	public void RecordBackendResponse_WhenNoCap_CapturesWholeBody()
	{
		// Arrange: a body far larger than any historical default cap, with no cap set.
		(RequestTrace trace, TraceScope scope) = CreateScope(maxBodyBytes: null);
		string body = new('y', 20_000);

		// Act
		scope.RecordBackendResponse("ollama", body);

		// Assert
		TraceEntry entry = Assert.Single(trace.Entries);
		Assert.Equal(body, entry.Body);
		Assert.False(entry.Truncated);
	}

	/// <summary>
	/// Verifies that <see cref="TraceScope.RecordBackendResponse"/> keeps a body whose byte length is at
	/// or under the cap intact and unflagged.
	/// </summary>
	[Fact]
	public void RecordBackendResponse_WhenWithinCap_CapturesWholeBody()
	{
		// Arrange: 5 ASCII bytes under an 8-byte cap.
		(RequestTrace trace, TraceScope scope) = CreateScope(maxBodyBytes: 8);

		// Act
		scope.RecordBackendResponse("ollama", "hello");

		// Assert
		TraceEntry entry = Assert.Single(trace.Entries);
		Assert.Equal("hello", entry.Body);
		Assert.False(entry.Truncated);
	}

	/// <summary>
	/// Verifies that <see cref="TraceScope.RecordBackendResponse"/> cuts a body that exceeds the cap to the
	/// budgeted prefix and flags the entry as truncated.
	/// </summary>
	[Fact]
	public void RecordBackendResponse_WhenExceedsCap_TruncatesAndFlags()
	{
		// Arrange: 11 ASCII bytes under a 4-byte cap.
		(RequestTrace trace, TraceScope scope) = CreateScope(maxBodyBytes: 4);

		// Act
		scope.RecordBackendResponse("ollama", "hello world");

		// Assert
		TraceEntry entry = Assert.Single(trace.Entries);
		Assert.Equal("hell", entry.Body);
		Assert.True(entry.Truncated);
	}

	/// <summary>
	/// Verifies that <see cref="TraceScope.RecordBackendResponse"/> backs the cut off a multi-byte UTF-8
	/// code point so the captured text never ends in a broken character, even though that means emitting
	/// fewer bytes than the cap allows.
	/// </summary>
	[Fact]
	public void RecordBackendResponse_WhenCutSplitsCodePoint_BacksOffToBoundary()
	{
		// Arrange: "€" is 3 UTF-8 bytes (E2 82 AC). A 5-byte cap would land mid-second-euro, so the cut
		// must back off to 3 bytes, keeping exactly one whole "€".
		(RequestTrace trace, TraceScope scope) = CreateScope(maxBodyBytes: 5);
		string body = "€€";

		// Act
		scope.RecordBackendResponse("ollama", body);

		// Assert: one intact euro sign survives, and truncation is flagged.
		TraceEntry entry = Assert.Single(trace.Entries);
		Assert.Equal("€", entry.Body);
		Assert.Equal(3, Encoding.UTF8.GetByteCount(entry.Body!));
		Assert.True(entry.Truncated);
	}

	// --- 5. Body-less entries ---

	/// <summary>
	/// Verifies that <see cref="TraceScope.RecordReasoning"/> records a body-less entry carrying the
	/// resolved effort and its provenance in the detail map, with no body to truncate.
	/// </summary>
	[Fact]
	public void RecordReasoning_WhenCalled_RecordsDetailWithoutBody()
	{
		// Arrange
		(RequestTrace trace, TraceScope scope) = CreateScope();

		// Act
		scope.RecordReasoning("high", "request", "medium", "reasoning_effort");

		// Assert: the entry carries provenance detail but no captured body.
		TraceEntry entry = Assert.Single(trace.Entries);
		Assert.Equal(TraceStage.ReasoningResolution, entry.Stage);
		Assert.Null(entry.Body);
		Assert.False(entry.Truncated);
		Assert.Equal("high", entry.Detail!["resolvedEffort"]);
		Assert.Equal("request", entry.Detail!["source"]);
	}

	/// <summary>
	/// Verifies that <see cref="TraceScope.Note"/> records a free-form annotation with no body and no
	/// detail map, since a note is a bare human-readable marker.
	/// </summary>
	[Fact]
	public void Note_WhenCalled_RecordsSummaryOnly()
	{
		// Arrange
		(RequestTrace trace, TraceScope scope) = CreateScope();

		// Act
		scope.Note("backend selected: ollama");

		// Assert
		TraceEntry entry = Assert.Single(trace.Entries);
		Assert.Equal(TraceStage.Note, entry.Stage);
		Assert.Equal("backend selected: ollama", entry.Summary);
		Assert.Null(entry.Body);
		Assert.Null(entry.Detail);
	}
}
