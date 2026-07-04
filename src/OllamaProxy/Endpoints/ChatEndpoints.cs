// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Net;

using OllamaProxy.Contracts.Ollama;
using OllamaProxy.Core;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Endpoints;

/// <summary>
/// Maps and handles the Ollama <c>POST /api/chat</c> endpoint. The handler routes the requested model
/// to its backend via the <see cref="IModelRouter"/>, selects the provider adapter via the
/// <see cref="IProviderResolver"/>, and then either streams newline-delimited chunks or returns the
/// single aggregated response depending on the request's <c>stream</c> flag. Routing, capability, and
/// translation concerns live in the core and provider layers; this type only orchestrates them and
/// shapes the HTTP response.
/// </summary>
static partial class ChatEndpoints
{
	/// <summary>
	/// Maps the <c>POST /api/chat</c> route onto the application's endpoint table.
	/// </summary>
	/// <param name="endpoints">The endpoint route builder to register the route with.</param>
	/// <returns>The same <paramref name="endpoints"/> instance for chaining.</returns>
	public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder endpoints)
	{
		ArgumentNullException.ThrowIfNull(endpoints);

		endpoints.MapPost("/api/chat", HandleChatAsync);
		return endpoints;
	}

	/// <summary>
	/// Handles a single <c>/api/chat</c> request: resolves the model, selects the adapter, and streams
	/// or aggregates the upstream response. A failed resolution yields a <c>404</c>; an upstream
	/// provider failure is mapped to its corresponding status, both in the Ollama error shape.
	/// </summary>
	/// <param name="context">The current HTTP context, used for response writing and cancellation.</param>
	/// <param name="request">The deserialized inbound Ollama chat request.</param>
	/// <param name="router">Resolves the model name to its backend and upstream identifier.</param>
	/// <param name="providerResolver">Selects the provider adapter for the resolved backend.</param>
	/// <param name="logger">Records routing decisions and upstream failures.</param>
	/// <returns>A task that completes when the response has been fully written.</returns>
	private static async Task HandleChatAsync(
		HttpContext             context,
		OllamaChatRequest?      request,
		IModelRouter            router,
		IProviderResolver       providerResolver,
		ILogger<ChatRequestLog> logger)
	{
		CancellationToken cancellationToken = context.RequestAborted;

		if (request is null || string.IsNullOrWhiteSpace(request.Model))
		{
			await OllamaHttp
				.WriteErrorAsync(context, HttpStatusCode.BadRequest, "A model name is required.", cancellationToken)
				.ConfigureAwait(false);
			return;
		}

		if (!EndpointRouting.TryResolveBackend(
			    router,
			    providerResolver,
			    request.Model,
			    out RegisteredModel? model,
			    out ResolvedBackend? resolved))
		{
			await OllamaHttp
				.WriteErrorAsync(
					context,
					HttpStatusCode.NotFound,
					$"Model '{request.Model}' was not found.",
					cancellationToken)
				.ConfigureAwait(false);
			return;
		}

		if (!EndpointRouting.TryValidateContextWindow(request.Options?.NumCtx, model, out string? contextError))
		{
			await OllamaHttp
				.WriteErrorAsync(context, HttpStatusCode.BadRequest, contextError, cancellationToken)
				.ConfigureAwait(false);
			return;
		}

		// The Ollama API treats a missing stream flag as streaming, so the proxy applies the same
		// default to stay drop-in compatible with clients that omit it.
		bool stream = request.Stream ?? true;

		try
		{
			if (stream)
				await StreamAsync(context, resolved, model, request, cancellationToken).ConfigureAwait(false);
			else
				await CompleteAsync(context, resolved, model, request, cancellationToken).ConfigureAwait(false);
		}
		catch (ProviderException exception)
		{
			LogUpstreamFailure(logger, model.BackendName, request.Model, exception);

			await OllamaHttp
				.WriteErrorAsync(context, OllamaHttp.MapProviderStatus(exception), exception.Message, cancellationToken)
				.ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Streams the upstream chat completion to the client as newline-delimited JSON, writing the
	/// response headers before the first chunk so the client begins receiving tokens immediately.
	/// </summary>
	/// <param name="context">The HTTP context whose response body receives the chunks.</param>
	/// <param name="resolved">The adapter and backend context servicing the request.</param>
	/// <param name="model">The resolved model carrying the upstream identifier.</param>
	/// <param name="request">The inbound chat request to forward.</param>
	/// <param name="cancellationToken">A token tied to the client connection.</param>
	/// <returns>A task that completes when the full stream has been written.</returns>
	private static async Task StreamAsync(
		HttpContext       context,
		ResolvedBackend   resolved,
		RegisteredModel   model,
		OllamaChatRequest request,
		CancellationToken cancellationToken)
	{
		context.Response.ContentType = OllamaHttp.NdjsonContentType;

		IAsyncEnumerable<OllamaChatResponse> chunks = resolved.Adapter.StreamChatAsync(
			resolved.Context,
			model.UpstreamModel,
			request,
			model.ReasoningEffort,
			cancellationToken);

		await foreach (OllamaChatResponse chunk in chunks.ConfigureAwait(false))
		{
			await OllamaHttp.WriteJsonLineAsync(context, chunk, cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Forwards the chat completion and writes the single aggregated Ollama response as JSON.
	/// </summary>
	/// <param name="context">The HTTP context whose response receives the JSON body.</param>
	/// <param name="resolved">The adapter and backend context servicing the request.</param>
	/// <param name="model">The resolved model carrying the upstream identifier.</param>
	/// <param name="request">The inbound chat request to forward.</param>
	/// <param name="cancellationToken">A token tied to the client connection.</param>
	/// <returns>A task that completes when the response has been written.</returns>
	private static async Task CompleteAsync(
		HttpContext       context,
		ResolvedBackend   resolved,
		RegisteredModel   model,
		OllamaChatRequest request,
		CancellationToken cancellationToken)
	{
		OllamaChatResponse response = await resolved.Adapter
			                              .CompleteChatAsync(
				                              resolved.Context,
				                              model.UpstreamModel,
				                              request,
				                              model.ReasoningEffort,
				                              cancellationToken)
			                              .ConfigureAwait(false);

		await context.Response
			.WriteAsJsonAsync(response, OllamaJson.Options, cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>
	/// The pre-compiled warning logged when an upstream backend fails while servicing a chat request.
	/// Defined via <see cref="LoggerMessageAttribute"/> so the message template is cached once instead
	/// of being parsed on every failure (CA1848); this path runs per request.
	/// </summary>
	[LoggerMessage(Level = LogLevel.Warning, Message = "Upstream backend {Backend} failed for model {Model}.")]
	private static partial void LogUpstreamFailure(
		ILogger   logger,
		string    backend,
		string    model,
		Exception exception);

	/// <summary>
	/// A marker type that names the <see cref="ILogger{TCategoryName}"/> category for chat-request
	/// handling. It carries no members; its sole purpose is to give the endpoint's log entries a
	/// stable, discoverable category rather than logging under a framework-generated name.
	/// </summary>
	internal sealed class ChatRequestLog;
}
