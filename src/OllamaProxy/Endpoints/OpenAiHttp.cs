// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Endpoints;

/// <summary>
/// Shared HTTP helpers for the inbound OpenAI-compatible <c>/v1</c> endpoints: emitting an
/// OpenAI-shaped error envelope, writing Server-Sent-Events frames, and translating a provider failure
/// into the matching status. These mirror <see cref="OllamaHttp"/> but speak the OpenAI wire format,
/// so OpenAI-native clients (such as the OpenAI SDK used by GitHub Copilot) receive errors and streams
/// in exactly the shape they expect.
/// </summary>
static class OpenAiHttp
{
	/// <summary>The content type for an OpenAI Server-Sent-Events stream.</summary>
	public const string EventStreamContentType = "text/event-stream";

	/// <summary>The SSE frame prefix carrying a data payload.</summary>
	private const string DataPrefix = "data: ";

	/// <summary>The sentinel payload that terminates an OpenAI SSE stream.</summary>
	private const string DoneSentinel = "[DONE]";

	/// <summary>The blank-line terminator that separates SSE frames.</summary>
	private const string FrameSeparator = "\n\n";

	/// <summary>
	/// Writes an OpenAI-shaped error envelope (<c>{ "error": { "message": ..., "type": ... } }</c>)
	/// with the given status code. The body is only written when the response has not already begun, so
	/// a failure surfaced mid-stream does not attempt to rewrite headers already on the wire.
	/// </summary>
	/// <param name="context">The HTTP context to write the error to.</param>
	/// <param name="statusCode">The HTTP status code to report.</param>
	/// <param name="message">The English error message placed in the body.</param>
	/// <param name="type">The OpenAI error type discriminator.</param>
	/// <param name="cancellationToken">A token to cancel writing the response.</param>
	/// <returns>A task that completes when the error has been written (or skipped).</returns>
	public static Task WriteErrorAsync(
		HttpContext       context,
		HttpStatusCode    statusCode,
		string            message,
		string            type,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);

		if (context.Response.HasStarted) return Task.CompletedTask;

		context.Response.StatusCode = (int)statusCode;
		context.Response.ContentType = "application/json";

		JsonObject error = new()
		{
			["message"] = message,
			["type"] = type
		};

		JsonObject envelope = new() { ["error"] = error };

		return context.Response.WriteAsync(envelope.ToJsonString(), Encoding.UTF8, cancellationToken);
	}

	/// <summary>
	/// Writes a single SSE <c>data:</c> frame carrying the supplied JSON payload, then flushes so the
	/// client receives the event immediately rather than at the end of the response.
	/// </summary>
	/// <param name="context">The HTTP context whose response body receives the frame.</param>
	/// <param name="payload">The raw JSON text to place in the frame.</param>
	/// <param name="cancellationToken">A token to cancel writing the frame.</param>
	/// <returns>A task that completes when the frame has been written and flushed.</returns>
	public static async Task WriteSseFrameAsync(
		HttpContext       context,
		string            payload,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);

		await context.Response
			.WriteAsync(string.Concat(DataPrefix, payload, FrameSeparator), Encoding.UTF8, cancellationToken)
			.ConfigureAwait(false);
		await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Writes the terminating <c>data: [DONE]</c> SSE frame that signals the end of an OpenAI stream.
	/// </summary>
	/// <param name="context">The HTTP context whose response body receives the frame.</param>
	/// <param name="cancellationToken">A token to cancel writing the frame.</param>
	/// <returns>A task that completes when the sentinel has been written and flushed.</returns>
	public static Task WriteSseDoneAsync(HttpContext context, CancellationToken cancellationToken) =>
		WriteSseFrameAsync(context, DoneSentinel, cancellationToken);

	/// <summary>
	/// Maps a <see cref="ProviderException"/> to the HTTP status the proxy returns to the client,
	/// using the same rule as the Ollama surface: a genuine client error (4xx) is passed through;
	/// anything else is normalized to <see cref="HttpStatusCode.BadGateway"/>.
	/// </summary>
	/// <param name="exception">The provider failure to translate.</param>
	/// <returns>The HTTP status code to report to the client.</returns>
	public static HttpStatusCode MapProviderStatus(ProviderException exception)
	{
		ArgumentNullException.ThrowIfNull(exception);

		int code = (int)exception.StatusCode;

		return code is >= 400 and < 500
			       ? exception.StatusCode
			       : HttpStatusCode.BadGateway;
	}
}
