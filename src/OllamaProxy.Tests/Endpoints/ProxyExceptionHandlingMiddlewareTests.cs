// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using OllamaProxy.Endpoints;

namespace OllamaProxy.Tests.Endpoints;

// The pipeline safety net, from a clean pass-through to a protocol-shaped 500.
//
// The middleware wraps the endpoint pipeline and only matters when something unexpected escapes it. Every
// handler already maps its own ProviderException, so what reaches here is a genuine fault. These tests
// follow that arc:
//
//   1. Pass-through  : when the pipeline succeeds the middleware is invisible; the response is left exactly
//                      as the endpoint wrote it (WhenPipelineSucceeds).
//   2. Ollama surface: an unexpected exception on the /api surface becomes a 500 in the Ollama error body
//                      (WhenApiPipelineThrows). An unmatched path falls back to the same shape, which is the
//                      proxy's primary client contract (WhenUnknownPathThrows).
//   3. OpenAI surface: the same failure on the /v1 surface becomes a 500 in the OpenAI error envelope
//                      (WhenV1PipelineThrows).
//   4. Diagnostics   : every synthesized 500 is logged at Error together with the offending request
//                      (WhenPipelineThrows).
//   5. Client abort  : once the client has hung up the exception is expected teardown, so it propagates
//                      untouched instead of being masked as a 500 (WhenClientAborted).
//   6. Guards        : null context / next on invoke and a null logger on construction are rejected.
[Trait("Category", "Unit")]
public sealed class ProxyExceptionHandlingMiddlewareTests
{
	/// <summary>The English body message the middleware returns for an unexpected failure.</summary>
	private const string InternalErrorMessage = "The proxy encountered an internal error.";

	/// <summary>
	/// An <see cref="ILogger{T}"/> that records each entry's level, rendered message, and exception, so a
	/// test can assert both that the failure was logged and what was logged with it.
	/// </summary>
	private sealed class CapturingLogger : ILogger<ProxyExceptionHandlingMiddleware>
	{
		/// <summary>Gets the recorded log entries in the order they were written.</summary>
		public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

		/// <inheritdoc/>
		public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

		/// <inheritdoc/>
		public bool IsEnabled(LogLevel logLevel) => true;

		/// <inheritdoc/>
		public void Log<TState>(
			LogLevel                         logLevel,
			EventId                          eventId,
			TState                           state,
			Exception?                       exception,
			Func<TState, Exception?, string> formatter) =>
			Entries.Add((logLevel, formatter(state, exception), exception));

		private sealed class NullScope : IDisposable
		{
			public static readonly NullScope Instance = new();
			public                 void      Dispose() { }
		}
	}

	/// <summary>
	/// Builds an HTTP context with the given method and path and a capturable response body, the minimal
	/// shape the middleware exercises.
	/// </summary>
	/// <param name="method">The inbound request method.</param>
	/// <param name="path">The inbound request path, used both for dialect selection and logging.</param>
	/// <param name="responseBody">The stream the synthesized error is written into (the "client" side).</param>
	/// <returns>The configured context.</returns>
	private static DefaultHttpContext CreateContext(string method, string path, Stream responseBody)
	{
		DefaultHttpContext context = new()
		{
			Request =
			{
				Method = method,
				Path = path
			},
			Response =
			{
				Body = responseBody
			}
		};
		return context;
	}

	/// <summary>Reads the captured response body as UTF-8 text.</summary>
	/// <param name="responseBody">The stream the response was written into.</param>
	/// <returns>The decoded body text.</returns>
	private static string ReadBody(MemoryStream responseBody) => Encoding.UTF8.GetString(responseBody.ToArray());

	// --- 1. Pass-through: the middleware is invisible on success ---

	/// <summary>
	/// Verifies that <see cref="ProxyExceptionHandlingMiddleware.InvokeAsync"/> leaves the response exactly
	/// as the endpoint wrote it when the pipeline completes normally, so the safety net adds no cost or
	/// change to the success path.
	/// </summary>
	[Fact]
	public async Task InvokeAsync_WhenPipelineSucceeds_LeavesResponseUntouched()
	{
		// Arrange
		ProxyExceptionHandlingMiddleware middleware = new(new CapturingLogger());
		using MemoryStream responseBody = new();
		DefaultHttpContext context = CreateContext("POST", "/api/chat", responseBody);
		RequestDelegate next = ctx =>
		{
			ctx.Response.StatusCode = StatusCodes.Status200OK;
			return ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("OK")).AsTask();
		};

		// Act
		await middleware.InvokeAsync(context, next);

		// Assert: the endpoint's status and body survive verbatim; the middleware wrote nothing of its own.
		Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
		Assert.Equal("OK", ReadBody(responseBody));
	}

	// --- 2. Ollama surface: unexpected failure becomes an Ollama-shaped 500 ---

	/// <summary>
	/// Verifies that <see cref="ProxyExceptionHandlingMiddleware.InvokeAsync"/> turns an unexpected exception
	/// on the <c>/api</c> surface into a <c>500</c> carrying the Ollama single-string error body.
	/// </summary>
	[Fact]
	public async Task InvokeAsync_WhenApiPipelineThrows_WritesOllamaError()
	{
		// Arrange
		ProxyExceptionHandlingMiddleware middleware = new(new CapturingLogger());
		using MemoryStream responseBody = new();
		DefaultHttpContext context = CreateContext("POST", "/api/chat", responseBody);
		RequestDelegate next = _ => throw new InvalidOperationException("boom");

		// Act
		await middleware.InvokeAsync(context, next);

		// Assert: a 500 with the Ollama { "error": "..." } shape and the generic, internals-free message.
		Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
		JsonNode root = JsonNode.Parse(ReadBody(responseBody))!;
		Assert.Equal(InternalErrorMessage, (string?)root["error"]);
	}

	/// <summary>
	/// Verifies that <see cref="ProxyExceptionHandlingMiddleware.InvokeAsync"/> falls back to the Ollama
	/// error shape for a path that matches neither surface, since the Ollama dialect is the proxy's primary
	/// client contract.
	/// </summary>
	[Fact]
	public async Task InvokeAsync_WhenUnknownPathThrows_WritesOllamaError()
	{
		// Arrange
		ProxyExceptionHandlingMiddleware middleware = new(new CapturingLogger());
		using MemoryStream responseBody = new();
		DefaultHttpContext context = CreateContext("GET", "/totally/unknown", responseBody);
		RequestDelegate next = _ => throw new InvalidOperationException("boom");

		// Act
		await middleware.InvokeAsync(context, next);

		// Assert: the unmatched path still gets the Ollama shape (a single "error" string), not the envelope.
		Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
		JsonNode root = JsonNode.Parse(ReadBody(responseBody))!;
		Assert.Equal(InternalErrorMessage, (string?)root["error"]);
	}

	// --- 3. OpenAI surface: unexpected failure becomes an OpenAI-shaped 500 ---

	/// <summary>
	/// Verifies that <see cref="ProxyExceptionHandlingMiddleware.InvokeAsync"/> turns an unexpected exception
	/// on the <c>/v1</c> surface into a <c>500</c> carrying the OpenAI <c>{ "error": { "message", "type" } }</c>
	/// envelope with the server-side error type.
	/// </summary>
	[Fact]
	public async Task InvokeAsync_WhenV1PipelineThrows_WritesOpenAiError()
	{
		// Arrange
		ProxyExceptionHandlingMiddleware middleware = new(new CapturingLogger());
		using MemoryStream responseBody = new();
		DefaultHttpContext context = CreateContext("POST", "/v1/chat/completions", responseBody);
		RequestDelegate next = _ => throw new InvalidOperationException("boom");

		// Act
		await middleware.InvokeAsync(context, next);

		// Assert: a 500 with the OpenAI envelope, the generic message, and the "server_error" type.
		Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
		JsonNode error = JsonNode.Parse(ReadBody(responseBody))!["error"]!;
		Assert.Equal(InternalErrorMessage, (string?)error["message"]);
		Assert.Equal("server_error", (string?)error["type"]);
	}

	// --- 4. Diagnostics: the failure is logged at Error ---

	/// <summary>
	/// Verifies that <see cref="ProxyExceptionHandlingMiddleware.InvokeAsync"/> logs the unexpected failure
	/// once at <see cref="LogLevel.Error"/>, naming the offending request and carrying the original exception
	/// so operators can correlate the synthesized 500 with its cause.
	/// </summary>
	[Fact]
	public async Task InvokeAsync_WhenPipelineThrows_LogsErrorWithRequestDetail()
	{
		// Arrange
		CapturingLogger logger = new();
		ProxyExceptionHandlingMiddleware middleware = new(logger);
		using MemoryStream responseBody = new();
		DefaultHttpContext context = CreateContext("POST", "/api/chat", responseBody);
		InvalidOperationException boom = new("kaboom");
		RequestDelegate next = _ => throw boom;

		// Act
		await middleware.InvokeAsync(context, next);

		// Assert: exactly one Error entry naming the request, carrying the very exception that escaped.
		(LogLevel level, string message, Exception? exception) = Assert.Single(logger.Entries);
		Assert.Equal(LogLevel.Error, level);
		Assert.Equal(
			"Unhandled exception while processing POST /api/chat; returning a synthesized 500.",
			message);
		Assert.Same(boom, exception);
	}

	// --- 5. Client abort: the exception is expected teardown, not a fault ---

	/// <summary>
	/// Verifies that <see cref="ProxyExceptionHandlingMiddleware.InvokeAsync"/> lets an exception propagate
	/// untouched when the client has already aborted, rather than masking the disconnect as a server fault.
	/// The exception filter excludes the cancelled case, so nothing is written and the original exception
	/// surfaces.
	/// </summary>
	[Fact]
	public async Task InvokeAsync_WhenClientAborted_PropagatesWithoutWriting()
	{
		// Arrange: a context whose RequestAborted is already cancelled (the client hung up mid-flight).
		ProxyExceptionHandlingMiddleware middleware = new(new CapturingLogger());
		using MemoryStream responseBody = new();
		DefaultHttpContext context = CreateContext("POST", "/api/chat", responseBody);
		context.RequestAborted = new CancellationToken(canceled: true);
		RequestDelegate next = _ => throw new InvalidOperationException("aborted mid-flight");

		// Act + Assert: the exception is not swallowed into a 500; it propagates as the expected teardown.
		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context, next));
		Assert.Equal("aborted mid-flight", ex.Message);

		// Assert (negative): no error body was synthesized for a client that is no longer listening.
		Assert.Equal(0, responseBody.Length);
	}

	// --- 6. Guards ---

	/// <summary>
	/// Verifies that <see cref="ProxyExceptionHandlingMiddleware.InvokeAsync"/> rejects a
	/// <see langword="null"/> context.
	/// </summary>
	[Fact]
	public async Task InvokeAsync_WhenContextNull_ThrowsArgumentNullException()
	{
		// Arrange
		ProxyExceptionHandlingMiddleware middleware = new(new CapturingLogger());

		// Act + Assert
		var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
			         middleware.InvokeAsync(null!, _ => Task.CompletedTask));
		Assert.Equal("context", ex.ParamName);
	}

	/// <summary>
	/// Verifies that <see cref="ProxyExceptionHandlingMiddleware.InvokeAsync"/> rejects a
	/// <see langword="null"/> next delegate.
	/// </summary>
	[Fact]
	public async Task InvokeAsync_WhenNextNull_ThrowsArgumentNullException()
	{
		// Arrange
		ProxyExceptionHandlingMiddleware middleware = new(new CapturingLogger());
		using MemoryStream responseBody = new();
		DefaultHttpContext context = CreateContext("POST", "/api/chat", responseBody);

		// Act + Assert
		var ex = await Assert.ThrowsAsync<ArgumentNullException>(() => middleware.InvokeAsync(context, null!));
		Assert.Equal("next", ex.ParamName);
	}

	/// <summary>
	/// Verifies that the <see cref="ProxyExceptionHandlingMiddleware"/> constructor rejects a
	/// <see langword="null"/> logger.
	/// </summary>
	[Fact]
	public void Constructor_WhenLoggerNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var ex = Assert.Throws<ArgumentNullException>(() => new ProxyExceptionHandlingMiddleware(null!));
		Assert.Equal("logger", ex.ParamName);
	}
}
