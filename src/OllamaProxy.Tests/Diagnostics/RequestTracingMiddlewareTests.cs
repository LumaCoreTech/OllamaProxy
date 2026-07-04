// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

using OllamaProxy.Configuration;
using OllamaProxy.Diagnostics;

namespace OllamaProxy.Tests.Diagnostics;

// The traced request, end to end: from the disabled no-op to a fully recorded, redacted flow.
//
// The middleware bookends one request — capture inbound, publish the ambient scope, tee the response,
// persist the trace — and must do all of it without changing what the client sees. These tests follow
// that arc:
//
//   1. Disabled       : tracing off is a pure pass-through; next runs and nothing is persisted
//                       (WhenTracingDisabled).
//   2. Happy path     : an enabled trace records the inbound request and the outbound response, with the
//                       inbound attachment redacted by default (WhenTracingEnabled). When the response
//                       outgrows the byte budget the capture's truncation verdict survives into the
//                       persisted entry (WhenOutboundBodyExceedsBudget).
//   3. Redaction wiring: the RedactAttachments toggle reaches the scope — off keeps the blob verbatim
//                       (WhenRedactionDisabled).
//   4. Transparency   : the response still reaches the client and the request body is rewound for the
//                       endpoint that follows (ForwardsResponse, RewindsRequestBody).
//   5. Secrets        : credential headers are redacted in the recorded detail (RedactsAuthorizationHeader).
//   6. Scope lifecycle: the scope is ambient during the pipeline and cleared afterwards, even when the
//                       pipeline throws — and the trace is persisted regardless (PublishesScope,
//                       WhenPipelineThrows).
//   7. Guards         : null context/next on invoke and null dependencies on construction are rejected.
[Trait("Category", "Unit")]
public sealed class RequestTracingMiddlewareTests
{
	/// <summary>A JSON request body carrying one bare base64 image inside an <c>images</c> array.</summary>
	private const string ImageBody = """{"images":["AAAAAAAAAAAAAAAA"]}""";

	/// <summary>The marker the sanitizer substitutes for <see cref="ImageBody"/>'s 16-char payload (~12 bytes).</summary>
	private const string ImageMarkerBody = """{"images":["[image omitted: ~12 B]"]}""";

	/// <summary>
	/// A trace sink that records the trace it was handed and how many times it was called, so a test can
	/// assert both that persistence happened and what was persisted.
	/// </summary>
	private sealed class RecordingSink : IRequestTraceSink
	{
		/// <summary>Gets the most recently written trace, or <see langword="null"/> when none was written.</summary>
		public RequestTrace? Written { get; private set; }

		/// <summary>Gets the number of times <see cref="WriteAsync"/> was invoked.</summary>
		public int WriteCount { get; private set; }

		/// <inheritdoc/>
		public Task WriteAsync(RequestTrace trace, CancellationToken cancellationToken)
		{
			Written = trace;
			WriteCount++;
			return Task.CompletedTask;
		}
	}

	/// <summary>
	/// Builds proxy options with the tracing switch and the two attachment/size knobs under test.
	/// </summary>
	/// <param name="enabled">Whether tracing is active.</param>
	/// <param name="maxBodyBytes">The per-body cap, or <see langword="null"/> for unbounded capture.</param>
	/// <param name="redactAttachments">Whether request-stage attachments are redacted.</param>
	/// <returns>The wrapped options.</returns>
	private static IOptions<ProxyOptions> CreateOptions(
		bool enabled,
		int? maxBodyBytes      = null,
		bool redactAttachments = true)
	{
		ProxyOptions proxy = new()
		{
			RequestTracing =
			{
				Enabled = enabled,
				MaxBodyBytes = maxBodyBytes,
				RedactAttachments = redactAttachments
			}
		};
		return Options.Create(proxy);
	}

	/// <summary>
	/// Builds an HTTP context with a buffered request body and a capturable response body, the minimal
	/// shape the middleware exercises.
	/// </summary>
	/// <param name="requestBody">The inbound request body text.</param>
	/// <param name="responseBody">The stream the response is written into (the "client" side).</param>
	/// <returns>The configured context.</returns>
	private static DefaultHttpContext CreateContext(string requestBody, Stream responseBody)
	{
		DefaultHttpContext context = new()
		{
			TraceIdentifier = "corr-test",
			Request =
			{
				Method = "POST",
				Path = "/api/chat",
				Body = new MemoryStream(Encoding.UTF8.GetBytes(requestBody))
			},
			Response =
			{
				Body = responseBody
			}
		};
		return context;
	}

	/// <summary>
	/// Finds the single entry for a given stage in a recorded trace.
	/// </summary>
	/// <param name="trace">The trace to search.</param>
	/// <param name="stage">The stage to locate.</param>
	/// <returns>The matching entry.</returns>
	private static TraceEntry EntryFor(RequestTrace trace, TraceStage stage) =>
		trace.Entries.Single(entry => entry.Stage == stage);

	// --- 1. Disabled: pure pass-through ---

	/// <summary>
	/// Verifies that <see cref="RequestTracingMiddleware.InvokeAsync"/> runs the pipeline and persists
	/// nothing when tracing is disabled, so the untraced path carries no diagnostic cost.
	/// </summary>
	[Fact]
	public async Task InvokeAsync_WhenTracingDisabled_CallsNextWithoutWritingTrace()
	{
		// Arrange
		RecordingSink sink = new();
		RequestTracingMiddleware middleware =
			new(new RequestTraceAccessor(), sink, TimeProvider.System, CreateOptions(enabled: false));
		using MemoryStream responseBody = new();
		DefaultHttpContext context = CreateContext("{}", responseBody);
		bool nextCalled = false;
		RequestDelegate next = _ =>
		{
			nextCalled = true;
			return Task.CompletedTask;
		};

		// Act
		await middleware.InvokeAsync(context, next);

		// Assert: the pipeline ran but nothing was traced.
		Assert.True(nextCalled);
		Assert.Equal(0, sink.WriteCount);
		Assert.Null(sink.Written);
	}

	// --- 2. Happy path: inbound + outbound recorded ---

	/// <summary>
	/// Verifies that <see cref="RequestTracingMiddleware.InvokeAsync"/> records both the inbound request
	/// (with its attachment redacted by default) and the outbound response when tracing is enabled.
	/// </summary>
	[Fact]
	public async Task InvokeAsync_WhenTracingEnabled_RecordsInboundAndOutbound()
	{
		// Arrange: unbounded capture, redaction on (the defaults), with an image in the request body.
		RecordingSink sink = new();
		RequestTracingMiddleware middleware =
			new(new RequestTraceAccessor(), sink, TimeProvider.System, CreateOptions(enabled: true));
		using MemoryStream responseBody = new();
		DefaultHttpContext context = CreateContext(ImageBody, responseBody);
		RequestDelegate next = ctx => ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("RESPONSE")).AsTask();

		// Act
		await middleware.InvokeAsync(context, next);

		// Assert: the trace was persisted once and carries the redacted inbound and the verbatim outbound.
		Assert.Equal(1, sink.WriteCount);
		var trace = Assert.IsType<RequestTrace>(sink.Written);
		Assert.Equal(ImageMarkerBody, EntryFor(trace, TraceStage.InboundRequest).Body);
		Assert.Equal("RESPONSE", EntryFor(trace, TraceStage.OutboundResponse).Body);
	}

	/// <summary>
	/// Verifies that <see cref="RequestTracingMiddleware.InvokeAsync"/> keeps the outbound entry flagged
	/// truncated when the capturing stream cut the body at the byte budget, even though the shortened text
	/// now exactly fits that budget and the scope's own re-measure would clear the flag. This is the edge
	/// the upstream-truncation handoff exists for: the capture is the authority on whether bytes were
	/// dropped, and that verdict must survive into the persisted entry.
	/// </summary>
	[Fact]
	public async Task InvokeAsync_WhenOutboundBodyExceedsBudget_FlagsEntryTruncated()
	{
		// Arrange: a 4-byte budget against an 8-byte response. The capture keeps "RESP" and flags truncation;
		// the scope then re-measures those 4 bytes against the same 4-byte cap and, on its own, would NOT
		// flag them — so only the forwarded capture verdict keeps the entry marked truncated.
		RecordingSink sink = new();
		RequestTracingMiddleware middleware = new(
			new RequestTraceAccessor(),
			sink,
			TimeProvider.System,
			CreateOptions(enabled: true, maxBodyBytes: 4));
		using MemoryStream responseBody = new();
		DefaultHttpContext context = CreateContext("{}", responseBody);
		RequestDelegate next = ctx => ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("RESPONSE")).AsTask();

		// Act
		await middleware.InvokeAsync(context, next);

		// Assert: the entry holds the 4-byte prefix and stays flagged truncated, with the summary echoing it.
		var trace = Assert.IsType<RequestTrace>(sink.Written);
		TraceEntry outbound = EntryFor(trace, TraceStage.OutboundResponse);
		Assert.Equal("RESP", outbound.Body);
		Assert.True(outbound.Truncated);
		Assert.Equal("HTTP 200 (body truncated)", outbound.Summary);
	}

	// --- 3. Redaction wiring ---

	/// <summary>
	/// Verifies that <see cref="RequestTracingMiddleware.InvokeAsync"/> wires the RedactAttachments toggle
	/// through to the scope: when redaction is off, the inbound image is recorded verbatim.
	/// </summary>
	[Fact]
	public async Task InvokeAsync_WhenRedactionDisabled_RecordsInboundImageVerbatim()
	{
		// Arrange: redaction explicitly disabled (the opt-out raw-capture path).
		RecordingSink sink = new();
		RequestTracingMiddleware middleware = new(
			new RequestTraceAccessor(),
			sink,
			TimeProvider.System,
			CreateOptions(enabled: true, redactAttachments: false));
		using MemoryStream responseBody = new();
		DefaultHttpContext context = CreateContext(ImageBody, responseBody);

		// Act
		await middleware.InvokeAsync(context, _ => Task.CompletedTask);

		// Assert: the raw image survives because the toggle reached the scope.
		var trace = Assert.IsType<RequestTrace>(sink.Written);
		Assert.Equal(ImageBody, EntryFor(trace, TraceStage.InboundRequest).Body);
	}

	// --- 4. Transparency: client and downstream unaffected ---

	/// <summary>
	/// Verifies that <see cref="RequestTracingMiddleware.InvokeAsync"/> still delivers the response bytes
	/// to the client stream, since teeing the body for the trace must never withhold it.
	/// </summary>
	[Fact]
	public async Task InvokeAsync_WhenTracingEnabled_ForwardsResponseToClient()
	{
		// Arrange
		RecordingSink sink = new();
		RequestTracingMiddleware middleware =
			new(new RequestTraceAccessor(), sink, TimeProvider.System, CreateOptions(enabled: true));
		using MemoryStream responseBody = new();
		DefaultHttpContext context = CreateContext("{}", responseBody);
		RequestDelegate next = ctx => ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("RESPONSE")).AsTask();

		// Act
		await middleware.InvokeAsync(context, next);

		// Assert: the underlying client stream received the full response.
		Assert.Equal("RESPONSE", Encoding.UTF8.GetString(responseBody.ToArray()));
	}

	/// <summary>
	/// Verifies that <see cref="RequestTracingMiddleware.InvokeAsync"/> rewinds the buffered request body
	/// after capturing it, so the downstream endpoint reads the body in full.
	/// </summary>
	[Fact]
	public async Task InvokeAsync_WhenTracingEnabled_RewindsRequestBodyForDownstream()
	{
		// Arrange
		RecordingSink sink = new();
		RequestTracingMiddleware middleware =
			new(new RequestTraceAccessor(), sink, TimeProvider.System, CreateOptions(enabled: true));
		using MemoryStream responseBody = new();
		const string requestBody = """{"model":"llama3"}""";
		DefaultHttpContext context = CreateContext(requestBody, responseBody);
		string? downstreamRead = null;
		RequestDelegate next = async ctx =>
		{
			// The middleware already reset Position to 0; downstream reads from the start, leaving the
			// stream open so the pipeline can continue using it.
			using StreamReader reader = new(ctx.Request.Body, Encoding.UTF8, false, leaveOpen: true);
			downstreamRead = await reader.ReadToEndAsync().ConfigureAwait(false);
		};

		// Act
		await middleware.InvokeAsync(context, next);

		// Assert: the endpoint saw the complete, rewound body.
		Assert.Equal(requestBody, downstreamRead);
	}

	// --- 5. Secrets ---

	/// <summary>
	/// Verifies that <see cref="RequestTracingMiddleware.InvokeAsync"/> redacts a credential-bearing
	/// header in the recorded inbound detail, so a shared trace file never leaks a token.
	/// </summary>
	[Fact]
	public async Task InvokeAsync_WhenAuthorizationHeaderPresent_RedactsItInTrace()
	{
		// Arrange
		RecordingSink sink = new();
		RequestTracingMiddleware middleware =
			new(new RequestTraceAccessor(), sink, TimeProvider.System, CreateOptions(enabled: true));
		using MemoryStream responseBody = new();
		DefaultHttpContext context = CreateContext("{}", responseBody);
		context.Request.Headers[HeaderNames.Authorization] = "Bearer super-secret-token";

		// Act
		await middleware.InvokeAsync(context, _ => Task.CompletedTask);

		// Assert: the secret value is replaced, never recorded.
		var trace = Assert.IsType<RequestTrace>(sink.Written);
		TraceEntry inbound = EntryFor(trace, TraceStage.InboundRequest);
		Assert.Equal("(redacted)", inbound.Detail![HeaderNames.Authorization]);
	}

	// --- 6. Scope lifecycle ---

	/// <summary>
	/// Verifies that <see cref="RequestTracingMiddleware.InvokeAsync"/> publishes the active scope as the
	/// ambient scope during the pipeline and clears it afterwards, so the provider layer can annotate the
	/// trace and the scope never leaks into the next flow on a pooled thread.
	/// </summary>
	[Fact]
	public async Task InvokeAsync_WhenTracingEnabled_PublishesScopeDuringPipelineAndClearsAfter()
	{
		// Arrange
		RequestTraceAccessor accessor = new();
		RequestTracingMiddleware middleware =
			new(accessor, new RecordingSink(), TimeProvider.System, CreateOptions(enabled: true));
		using MemoryStream responseBody = new();
		DefaultHttpContext context = CreateContext("{}", responseBody);
		ITraceScope? duringPipeline = null;
		RequestDelegate next = _ =>
		{
			duringPipeline = accessor.Current;
			return Task.CompletedTask;
		};

		// Act
		await middleware.InvokeAsync(context, next);

		// Assert: an enabled scope was ambient during the pipeline, and the no-op default is restored after.
		Assert.NotNull(duringPipeline);
		Assert.True(duringPipeline!.IsEnabled);
		Assert.Same(NullTraceScope.Instance, accessor.Current);
	}

	/// <summary>
	/// Verifies that <see cref="RequestTracingMiddleware.InvokeAsync"/> still persists the trace and clears
	/// the ambient scope when the pipeline throws, so a failing request is captured rather than lost.
	/// </summary>
	[Fact]
	public async Task InvokeAsync_WhenPipelineThrows_StillWritesTraceAndClearsScope()
	{
		// Arrange
		RequestTraceAccessor accessor = new();
		RecordingSink sink = new();
		RequestTracingMiddleware middleware =
			new(accessor, sink, TimeProvider.System, CreateOptions(enabled: true));
		using MemoryStream responseBody = new();
		DefaultHttpContext context = CreateContext("{}", responseBody);
		RequestDelegate next = _ => throw new InvalidOperationException("pipeline boom");

		// Act: the exception propagates, but the finally block must still trace and clean up.
		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context, next));

		// Assert
		Assert.Equal("pipeline boom", ex.Message);
		Assert.Equal(1, sink.WriteCount);
		Assert.Same(NullTraceScope.Instance, accessor.Current);
	}

	// --- 7. Guards ---

	/// <summary>
	/// Verifies that <see cref="RequestTracingMiddleware.InvokeAsync"/> rejects a <see langword="null"/>
	/// context.
	/// </summary>
	[Fact]
	public async Task InvokeAsync_WhenContextNull_ThrowsArgumentNullException()
	{
		// Arrange
		RequestTracingMiddleware middleware =
			new(new RequestTraceAccessor(), new RecordingSink(), TimeProvider.System, CreateOptions(enabled: true));

		// Act + Assert
		var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
			         middleware.InvokeAsync(null!, _ => Task.CompletedTask));
		Assert.Equal("context", ex.ParamName);
	}

	/// <summary>
	/// Verifies that <see cref="RequestTracingMiddleware.InvokeAsync"/> rejects a <see langword="null"/>
	/// next delegate.
	/// </summary>
	[Fact]
	public async Task InvokeAsync_WhenNextNull_ThrowsArgumentNullException()
	{
		// Arrange
		RequestTracingMiddleware middleware =
			new(new RequestTraceAccessor(), new RecordingSink(), TimeProvider.System, CreateOptions(enabled: true));
		using MemoryStream responseBody = new();
		DefaultHttpContext context = CreateContext("{}", responseBody);

		// Act + Assert
		var ex = await Assert.ThrowsAsync<ArgumentNullException>(() => middleware.InvokeAsync(context, null!));
		Assert.Equal("next", ex.ParamName);
	}

	/// <summary>
	/// Verifies that the <see cref="RequestTracingMiddleware"/> constructor rejects a
	/// <see langword="null"/> accessor.
	/// </summary>
	[Fact]
	public void Constructor_WhenAccessorNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var ex = Assert.Throws<ArgumentNullException>(() => new RequestTracingMiddleware(
			null!,
			new RecordingSink(),
			TimeProvider.System,
			CreateOptions(enabled: true)));
		Assert.Equal("accessor", ex.ParamName);
	}

	/// <summary>
	/// Verifies that the <see cref="RequestTracingMiddleware"/> constructor rejects a
	/// <see langword="null"/> sink.
	/// </summary>
	[Fact]
	public void Constructor_WhenSinkNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var ex = Assert.Throws<ArgumentNullException>(() => new RequestTracingMiddleware(
			new RequestTraceAccessor(),
			null!,
			TimeProvider.System,
			CreateOptions(enabled: true)));
		Assert.Equal("sink", ex.ParamName);
	}

	/// <summary>
	/// Verifies that the <see cref="RequestTracingMiddleware"/> constructor rejects a
	/// <see langword="null"/> time provider.
	/// </summary>
	[Fact]
	public void Constructor_WhenTimeProviderNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var ex = Assert.Throws<ArgumentNullException>(() => new RequestTracingMiddleware(
			new RequestTraceAccessor(),
			new RecordingSink(),
			null!,
			CreateOptions(enabled: true)));
		Assert.Equal("timeProvider", ex.ParamName);
	}

	/// <summary>
	/// Verifies that the <see cref="RequestTracingMiddleware"/> constructor rejects <see langword="null"/>
	/// options.
	/// </summary>
	[Fact]
	public void Constructor_WhenOptionsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var ex = Assert.Throws<ArgumentNullException>(() => new RequestTracingMiddleware(
			new RequestTraceAccessor(),
			new RecordingSink(),
			TimeProvider.System,
			null!));
		Assert.Equal("options", ex.ParamName);
	}
}
