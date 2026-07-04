// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Net;
using System.Text.Json;

using OllamaProxy.Contracts.Ollama;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Endpoints;

/// <summary>
/// Shared HTTP helpers for the Ollama-compatible endpoints: emitting an Ollama-shaped error body,
/// streaming newline-delimited JSON chunks, and translating a provider failure into the HTTP status
/// an Ollama client expects. Centralizing these keeps the individual endpoint handlers focused on
/// routing and translation rather than response plumbing, and guarantees every endpoint reports
/// failures in the same English, Ollama-native shape.
/// </summary>
static class OllamaHttp
{
	/// <summary>The content type Ollama uses for its newline-delimited JSON streams.</summary>
	public const string NdjsonContentType = "application/x-ndjson";

	/// <summary>The single byte separating objects in a newline-delimited JSON stream.</summary>
	private static readonly byte[] NewLine = "\n"u8.ToArray();

	/// <summary>
	/// Writes an Ollama-shaped <see cref="OllamaErrorResponse"/> with the given status code. The body
	/// is only written when the response has not already begun, so a failure surfaced mid-stream does
	/// not attempt to rewrite headers that are already on the wire.
	/// </summary>
	/// <param name="context">The HTTP context to write the error to.</param>
	/// <param name="statusCode">The HTTP status code to report.</param>
	/// <param name="message">The English error message placed in the body.</param>
	/// <param name="cancellationToken">A token to cancel writing the response.</param>
	/// <returns>A task that completes when the error has been written (or skipped).</returns>
	public static Task WriteErrorAsync(
		HttpContext       context,
		HttpStatusCode    statusCode,
		string            message,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);

		if (context.Response.HasStarted) return Task.CompletedTask;

		context.Response.StatusCode = (int)statusCode;
		context.Response.ContentType = "application/json";

		return context.Response.WriteAsJsonAsync(
			new OllamaErrorResponse(message),
			OllamaJson.Options,
			cancellationToken);
	}

	/// <summary>
	/// Serializes a single value as one line of a newline-delimited JSON stream and flushes it, so the
	/// client receives each chunk as soon as it is produced rather than at the end of the response.
	/// </summary>
	/// <typeparam name="T">The chunk type to serialize.</typeparam>
	/// <param name="context">The HTTP context whose response body receives the line.</param>
	/// <param name="value">The chunk to serialize.</param>
	/// <param name="cancellationToken">A token to cancel writing the chunk.</param>
	/// <returns>A task that completes when the line has been written and flushed.</returns>
	public static async Task WriteJsonLineAsync<T>(
		HttpContext       context,
		T                 value,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);

		byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, OllamaJson.Options);

		await context.Response.Body.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
		await context.Response.Body.WriteAsync(NewLine, cancellationToken).ConfigureAwait(false);
		await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Maps a <see cref="ProviderException"/> to the HTTP status the proxy returns to the client. The
	/// provider's own status is preserved when it is a meaningful client- or server-side code;
	/// otherwise the failure is reported as a <see cref="HttpStatusCode.BadGateway"/> because, from the
	/// client's perspective, the upstream backend (not the request) is at fault.
	/// </summary>
	/// <param name="exception">The provider failure to translate.</param>
	/// <returns>The HTTP status code to report to the client.</returns>
	public static HttpStatusCode MapProviderStatus(ProviderException exception)
	{
		ArgumentNullException.ThrowIfNull(exception);

		int code = (int)exception.StatusCode;

		// A genuine client error (e.g. 400/404) is passed through so the caller can correct its
		// request; anything else is normalized to 502 to signal an upstream problem.
		return code is >= 400 and < 500
			       ? exception.StatusCode
			       : HttpStatusCode.BadGateway;
	}
}
