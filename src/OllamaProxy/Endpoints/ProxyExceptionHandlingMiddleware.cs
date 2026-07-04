// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Net;

namespace OllamaProxy.Endpoints;

/// <summary>
/// The pipeline safety net that turns an unexpected exception into a protocol-shaped error response.
/// Every endpoint handler already catches <see cref="Providers.Abstractions.ProviderException"/> and maps
/// it to the right status, so only genuinely unexpected failures reach this middleware (a proxy bug, a
/// faulting dependency). It writes the failure in the dialect the client is speaking: an Ollama
/// <c>{ "error": ... }</c> body for the <c>/api</c> surface, and an OpenAI
/// <c>{ "error": { "message", "type" } }</c> envelope for the <c>/v1</c> surface. Without this net an
/// unexpected exception would surface as the host's default response, which is the wrong wire shape for
/// both client families.
/// </summary>
/// <remarks>
/// The middleware is wired after <see cref="Diagnostics.RequestTracingMiddleware"/>, so a synthesized
/// error is still recorded by an active trace. When the response has already started, the protocol writers
/// no-op (the headers are on the wire and cannot be rewritten). The failure is logged in every case.
/// </remarks>
sealed partial class ProxyExceptionHandlingMiddleware : IMiddleware
{
	/// <summary>
	/// The English body message returned for an unexpected failure. It deliberately reveals no internals.
	/// </summary>
	private const string InternalErrorMessage = "The proxy encountered an internal error.";

	/// <summary>
	/// The OpenAI error <c>type</c> for a server-side fault, matching OpenAI's own 500 convention.
	/// </summary>
	private const string OpenAiErrorType = "server_error";

	private readonly ILogger<ProxyExceptionHandlingMiddleware> mLogger;

	/// <summary>
	/// Initializes a new instance of the <see cref="ProxyExceptionHandlingMiddleware"/> class.
	/// </summary>
	/// <param name="logger">Records the unexpected failure together with the request that triggered it.</param>
	/// <exception cref="ArgumentNullException"><paramref name="logger"/> is <see langword="null"/>.</exception>
	public ProxyExceptionHandlingMiddleware(ILogger<ProxyExceptionHandlingMiddleware> logger)
	{
		ArgumentNullException.ThrowIfNull(logger);
		mLogger = logger;
	}

	/// <inheritdoc/>
	public async Task InvokeAsync(HttpContext context, RequestDelegate next)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(next);

		try
		{
			await next(context).ConfigureAwait(false);
		}
		catch (Exception exception) when (!context.RequestAborted.IsCancellationRequested)
		{
			// A cancelled RequestAborted means the client hung up: there is no one left to answer, and the
			// exception is expected teardown rather than a fault. The filter excludes that case. Everything
			// else is an unexpected failure we translate into a protocol-shaped 500.
			await WriteUnexpectedErrorAsync(context, exception).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Logs the unexpected failure and writes a <c>500</c> in the dialect matching the request's surface.
	/// </summary>
	/// <param name="context">The HTTP context whose response receives the synthesized error.</param>
	/// <param name="exception">The unexpected exception that escaped the endpoint handler.</param>
	/// <returns>A task that completes once the error is written (or skipped because the response started).</returns>
	private Task WriteUnexpectedErrorAsync(HttpContext context, Exception exception)
	{
		HttpRequest request = context.Request;
		LogUnhandledException(
			mLogger,
			request.Method,
			request.Path.ToString(),
			(int)HttpStatusCode.InternalServerError,
			exception);

		CancellationToken cancellationToken = context.RequestAborted;

		// The /v1 surface speaks OpenAI; everything else (the /api surface and any unmatched path) gets the
		// Ollama-native shape, which is also the proxy's primary client contract.
		if (request.Path.StartsWithSegments("/v1"))
		{
			return OpenAiHttp
				.WriteErrorAsync(
					context,
					HttpStatusCode.InternalServerError,
					InternalErrorMessage,
					OpenAiErrorType,
					cancellationToken);
		}
		else
		{
			return OllamaHttp
				.WriteErrorAsync(context, HttpStatusCode.InternalServerError, InternalErrorMessage, cancellationToken);
		}
	}

	/// <summary>
	/// The pre-compiled error logged when an unexpected exception escapes the endpoint pipeline. Defined via
	/// <see cref="LoggerMessageAttribute"/> so the template is cached once instead of being parsed on every
	/// failure (CA1848).
	/// </summary>
	/// <param name="logger">The logger that records the failure.</param>
	/// <param name="method">The HTTP method of the failed request.</param>
	/// <param name="path">The path of the failed request.</param>
	/// <param name="statusCode">The synthesized status code returned to the client.</param>
	/// <param name="exception">The unexpected exception being reported.</param>
	[LoggerMessage(
		Level = LogLevel.Error,
		Message = "Unhandled exception while processing {Method} {Path}; returning a synthesized {StatusCode}.")]
	private static partial void LogUnhandledException(
		ILogger   logger,
		string    method,
		string    path,
		int       statusCode,
		Exception exception);
}
