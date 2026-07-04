// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Globalization;
using System.Text;

using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

using OllamaProxy.Configuration;

namespace OllamaProxy.Diagnostics;

/// <summary>
/// The ASP.NET Core middleware that bookends a traced request: it captures the inbound request (method,
/// path, redacted headers, body), opens the ambient <see cref="ITraceScope"/> the endpoint and provider
/// layers annotate, captures the outbound response by teeing the response body, and finally hands the
/// completed <see cref="RequestTrace"/> to the sink. It is a no-op when tracing is disabled, so the
/// pipeline pays only a single boolean check per request in the common (untraced) case.
/// </summary>
sealed class RequestTracingMiddleware : IMiddleware
{
	/// <summary>
	/// Header names whose values are replaced with a redaction marker before being recorded. Credentials
	/// must never be written to a diagnostic file that may be shared when reporting an issue.
	/// </summary>
	private static readonly HashSet<string> RedactedHeaders = new(StringComparer.OrdinalIgnoreCase)
	{
		HeaderNames.Authorization,
		HeaderNames.Cookie,
		HeaderNames.SetCookie,
		"api-key",
		"x-api-key"
	};

	private const string RedactionMarker = "(redacted)";

	private readonly IRequestTraceAccessor mAccessor;
	private readonly IRequestTraceSink     mSink;
	private readonly TimeProvider          mTimeProvider;
	private readonly bool                  mEnabled;
	private readonly int?                  mMaxBodyBytes;
	private readonly bool                  mRedactAttachments;

	/// <summary>
	/// Initializes a new instance of the <see cref="RequestTracingMiddleware"/> class.
	/// </summary>
	/// <param name="accessor">Publishes the ambient trace scope for the provider layer to reach.</param>
	/// <param name="sink">Persists the completed trace.</param>
	/// <param name="timeProvider">The clock used for the trace's timestamps.</param>
	/// <param name="options">The proxy options carrying the tracing switch and body byte budget.</param>
	/// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
	public RequestTracingMiddleware(
		IRequestTraceAccessor  accessor,
		IRequestTraceSink      sink,
		TimeProvider           timeProvider,
		IOptions<ProxyOptions> options)
	{
		ArgumentNullException.ThrowIfNull(accessor);
		ArgumentNullException.ThrowIfNull(sink);
		ArgumentNullException.ThrowIfNull(timeProvider);
		ArgumentNullException.ThrowIfNull(options);

		mAccessor = accessor;
		mSink = sink;
		mTimeProvider = timeProvider;

		RequestTracingOptions tracing = options.Value.RequestTracing;
		mEnabled = tracing.Enabled;
		mMaxBodyBytes = tracing.MaxBodyBytes;
		mRedactAttachments = tracing.RedactAttachments;
	}

	/// <inheritdoc/>
	public async Task InvokeAsync(HttpContext context, RequestDelegate next)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(next);

		if (!mEnabled)
		{
			await next(context).ConfigureAwait(false);
			return;
		}

		await TraceAsync(context, next).ConfigureAwait(false);
	}

	/// <summary>
	/// Runs the request under an active trace: records the inbound request, publishes the scope, tees the
	/// response body, runs the pipeline, then records the outbound response and persists the trace. The
	/// scope is always cleared and the trace always written, even when the pipeline throws, so a failing
	/// request is still captured and the ambient scope never leaks into the next flow on a pooled thread.
	/// </summary>
	/// <param name="context">The current HTTP context.</param>
	/// <param name="next">The next delegate in the pipeline.</param>
	/// <returns>A task that completes when the request and its trace have been processed.</returns>
	private async Task TraceAsync(HttpContext context, RequestDelegate next)
	{
		// The proxy does not consume query parameters anywhere: they play no role in routing, are not
		// forwarded to backends, and would be nothing but noise in the trace. They are also a potential
		// carrier for credentials a client appends carelessly to the URL, so leaving them out is a small
		// defense-in-depth win on top of being the honest thing to record.
		RequestTrace trace = new(
			context.TraceIdentifier,
			mTimeProvider.GetUtcNow(),
			context.Request.Method,
			context.Request.Path);

		TraceScope scope = new(trace, mMaxBodyBytes, mRedactAttachments, mTimeProvider);

		await RecordInboundAsync(context, scope).ConfigureAwait(false);

		Stream originalBody = context.Response.Body;
		CapturingStream capturing = new(originalBody, mMaxBodyBytes);
		context.Response.Body = capturing;

		mAccessor.Set(scope);
		try
		{
			await next(context).ConfigureAwait(false);
		}
		finally
		{
			mAccessor.Clear();
			context.Response.Body = originalBody;

			try
			{
				RecordOutbound(context, scope, capturing);

				// Best-effort persistence: the trace is written outside the request's critical path and any
				// sink failure is swallowed by the sink itself, so it can never affect the response. The
				// request's abort token is intentionally NOT forwarded here: the trace is a diagnostic for
				// the operator, and a client that disconnected mid-trace is exactly the case where the most
				// complete record is most valuable, so the file is always written to completion.
				await mSink.WriteAsync(trace, CancellationToken.None).ConfigureAwait(false);
			}
			finally
			{
				// Dispose the capturing wrapper once its buffer has been read (RecordOutbound) and persisted
				// (WriteAsync): it owns only the in-memory capture buffer (the inner response stream belongs
				// to the server and is intentionally left open) so disposal just releases that buffer.
				await capturing.DisposeAsync().ConfigureAwait(false);
			}
		}
	}

	/// <summary>
	/// Buffers and records the inbound request body and redacted headers, then rewinds the body stream so
	/// the downstream endpoint reads it unaffected.
	/// </summary>
	/// <param name="context">The current HTTP context.</param>
	/// <param name="scope">The active trace scope to record into.</param>
	/// <returns>A task that completes when the inbound request has been recorded.</returns>
	private static async Task RecordInboundAsync(HttpContext context, TraceScope scope)
	{
		context.Request.EnableBuffering();

		string body = await ReadBodyAsync(context).ConfigureAwait(false);

		Dictionary<string, string> headers = RedactHeaders(context.Request.Headers);

		scope.RecordTransport(
			TraceStage.InboundRequest,
			$"{context.Request.Method} {context.Request.Path}",
			headers,
			body);
	}

	/// <summary>
	/// Records the captured outbound response: its status code, redacted headers, and teed body.
	/// </summary>
	/// <param name="context">The current HTTP context.</param>
	/// <param name="scope">The active trace scope to record into.</param>
	/// <param name="capturing">The stream that teed the response body.</param>
	private static void RecordOutbound(HttpContext context, TraceScope scope, CapturingStream capturing)
	{
		Dictionary<string, string> headers = RedactHeaders(context.Response.Headers);
		headers["status"] = context.Response.StatusCode.ToString(CultureInfo.InvariantCulture);

		string body = capturing.GetCapturedText();
		string summary = $"HTTP {context.Response.StatusCode}" +
		                 (capturing.Truncated ? " (body truncated)" : string.Empty);

		// The capturing stream is the authority on whether the outbound body was cut: it teed the bytes
		// under the same budget and already knows. Forwarding its flag keeps the entry's Truncated marker
		// consistent with the "(body truncated)" summary; the scope's own re-measure would otherwise clear
		// it, since the already-shortened text now fits under the budget.
		scope.RecordTransport(TraceStage.OutboundResponse, summary, headers, body, capturing.Truncated);
	}

	/// <summary>
	/// Reads the (buffered) request body as text without consuming it, leaving the stream rewound for the
	/// endpoint that follows.
	/// </summary>
	/// <param name="context">The current HTTP context.</param>
	/// <returns>The request body text, or an empty string when there is none.</returns>
	private static async Task<string> ReadBodyAsync(HttpContext context)
	{
		context.Request.Body.Position = 0;

		using StreamReader reader = new(
			context.Request.Body,
			Encoding.UTF8,
			detectEncodingFromByteOrderMarks: false,
			leaveOpen: true);
		string body = await reader.ReadToEndAsync().ConfigureAwait(false);

		context.Request.Body.Position = 0;
		return body;
	}

	/// <summary>
	/// Projects HTTP headers into a recordable map, replacing the value of every credential-bearing
	/// header with <see cref="RedactionMarker"/> so secrets never reach the trace file.
	/// </summary>
	/// <param name="headers">The header collection to project.</param>
	/// <returns>The redacted header map.</returns>
	private static Dictionary<string, string> RedactHeaders(IHeaderDictionary headers)
	{
		Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);

		foreach ((string name, StringValues values) in headers)
		{
			result[name] = RedactedHeaders.Contains(name) ? RedactionMarker : values.ToString();
		}

		return result;
	}
}
