// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Net;

using OllamaProxy.Contracts.Ollama;
using OllamaProxy.Core;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Endpoints;

/// <summary>
/// Maps and handles the Ollama <c>POST /api/generate</c> endpoint. The single-prompt completion API
/// is expressed in terms of chat: the prompt and optional system prompt are wrapped into chat
/// messages, forwarded through the same adapter as <c>/api/chat</c>, and the resulting chat chunks are
/// projected back into the generate response shape (whose text lives in <c>response</c> rather than a
/// nested <c>message</c>). Reusing the chat pipeline keeps a single translation path for both APIs.
/// </summary>
static partial class GenerateEndpoints
{
	/// <summary>
	/// Maps the <c>POST /api/generate</c> route onto the application's endpoint table.
	/// </summary>
	/// <param name="endpoints">The endpoint route builder to register the route with.</param>
	/// <returns>The same <paramref name="endpoints"/> instance for chaining.</returns>
	public static IEndpointRouteBuilder MapGenerateEndpoints(this IEndpointRouteBuilder endpoints)
	{
		ArgumentNullException.ThrowIfNull(endpoints);

		endpoints.MapPost("/api/generate", HandleGenerateAsync);
		return endpoints;
	}

	/// <summary>
	/// Handles a single <c>/api/generate</c> request by translating it to a chat request, routing it,
	/// and streaming or aggregating the response in the generate shape. Resolution and upstream
	/// failures surface as Ollama-shaped errors, mirroring <see cref="ChatEndpoints"/>.
	/// </summary>
	/// <param name="context">The current HTTP context, used for response writing and cancellation.</param>
	/// <param name="request">The deserialized inbound Ollama generate request.</param>
	/// <param name="router">Resolves the model name to its backend and upstream identifier.</param>
	/// <param name="providerResolver">Selects the provider adapter for the resolved backend.</param>
	/// <param name="logger">Records routing decisions and upstream failures.</param>
	/// <returns>A task that completes when the response has been fully written.</returns>
	private static async Task HandleGenerateAsync(
		HttpContext                 context,
		OllamaGenerateRequest?      request,
		IModelRouter                router,
		IProviderResolver           providerResolver,
		ILogger<GenerateRequestLog> logger)
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

		OllamaChatRequest chatRequest = BuildChatRequest(request);
		bool stream = request.Stream ?? true;

		try
		{
			if (stream)
			{
				await StreamAsync(context, resolved, model, request.Model, chatRequest, cancellationToken)
					.ConfigureAwait(false);
			}
			else
			{
				await CompleteAsync(context, resolved, model, request.Model, chatRequest, cancellationToken)
					.ConfigureAwait(false);
			}
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
	/// Wraps a generate request into an equivalent chat request: the optional system prompt becomes a
	/// leading <c>system</c> message and the prompt becomes a single <c>user</c> message carrying any
	/// images. The <c>stream</c>, <c>format</c>, and <c>options</c> fields pass through unchanged so the
	/// downstream translation behaves identically to a native chat call.
	/// </summary>
	/// <param name="request">The inbound generate request to wrap.</param>
	/// <returns>The equivalent chat request for the shared pipeline.</returns>
	private static OllamaChatRequest BuildChatRequest(OllamaGenerateRequest request)
	{
		List<OllamaChatMessage> messages = [];

		if (!string.IsNullOrEmpty(request.System)) messages.Add(new OllamaChatMessage("system", request.System));

		messages.Add(new OllamaChatMessage("user", request.Prompt ?? string.Empty, request.Images));

		return new OllamaChatRequest(
			request.Model,
			messages,
			Tools: null,
			Format: request.Format,
			Options: request.Options,
			Stream: request.Stream,
			Think: request.Think,
			KeepAlive: request.KeepAlive,
			Logprobs: request.Logprobs,
			TopLogprobs: request.TopLogprobs);
	}

	/// <summary>
	/// Streams the chat translation back to the client as generate-shaped, newline-delimited JSON.
	/// </summary>
	/// <param name="context">The HTTP context whose response body receives the chunks.</param>
	/// <param name="resolved">The adapter and backend context servicing the request.</param>
	/// <param name="model">The resolved model carrying the upstream identifier.</param>
	/// <param name="clientModel">The model name echoed back to the client.</param>
	/// <param name="chatRequest">The wrapped chat request to forward.</param>
	/// <param name="cancellationToken">A token tied to the client connection.</param>
	/// <returns>A task that completes when the full stream has been written.</returns>
	private static async Task StreamAsync(
		HttpContext       context,
		ResolvedBackend   resolved,
		RegisteredModel   model,
		string            clientModel,
		OllamaChatRequest chatRequest,
		CancellationToken cancellationToken)
	{
		context.Response.ContentType = OllamaHttp.NdjsonContentType;

		IAsyncEnumerable<OllamaChatResponse> chunks = resolved.Adapter.StreamChatAsync(
			resolved.Context,
			model.UpstreamModel,
			chatRequest,
			model.ReasoningEffort,
			cancellationToken);

		await foreach (OllamaChatResponse chunk in chunks.ConfigureAwait(false))
		{
			await OllamaHttp
				.WriteJsonLineAsync(context, ToGenerateResponse(chunk, clientModel), cancellationToken)
				.ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Forwards the wrapped chat request and writes the single aggregated generate response as JSON.
	/// </summary>
	/// <param name="context">The HTTP context whose response receives the JSON body.</param>
	/// <param name="resolved">The adapter and backend context servicing the request.</param>
	/// <param name="model">The resolved model carrying the upstream identifier.</param>
	/// <param name="clientModel">The model name echoed back to the client.</param>
	/// <param name="chatRequest">The wrapped chat request to forward.</param>
	/// <param name="cancellationToken">A token tied to the client connection.</param>
	/// <returns>A task that completes when the response has been written.</returns>
	private static async Task CompleteAsync(
		HttpContext       context,
		ResolvedBackend   resolved,
		RegisteredModel   model,
		string            clientModel,
		OllamaChatRequest chatRequest,
		CancellationToken cancellationToken)
	{
		OllamaChatResponse response = await resolved.Adapter
			                              .CompleteChatAsync(
				                              resolved.Context,
				                              model.UpstreamModel,
				                              chatRequest,
				                              model.ReasoningEffort,
				                              cancellationToken)
			                              .ConfigureAwait(false);

		await context.Response
			.WriteAsJsonAsync(ToGenerateResponse(response, clientModel), OllamaJson.Options, cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>
	/// Projects a chat response chunk onto the generate response shape, moving the assistant message
	/// content into <c>response</c>, carrying the reasoning text and token log-probabilities, and copying
	/// the terminal flags and timing/token accounting fields verbatim so the two APIs report identical
	/// metadata.
	/// </summary>
	/// <param name="chunk">The chat chunk to project.</param>
	/// <param name="clientModel">The model name echoed back to the client.</param>
	/// <returns>The equivalent generate response chunk.</returns>
	private static OllamaGenerateResponse ToGenerateResponse(OllamaChatResponse chunk, string clientModel) => new(
		clientModel,
		chunk.CreatedAt,
		chunk.Message.Content,
		chunk.Done,
		chunk.DoneReason,
		chunk.TotalDuration,
		chunk.LoadDuration,
		chunk.PromptEvalCount,
		chunk.PromptEvalDuration,
		chunk.EvalCount,
		chunk.EvalDuration,
		chunk.Message.Thinking,
		chunk.Logprobs);

	/// <summary>
	/// The pre-compiled warning logged when an upstream backend fails while servicing a generate
	/// request. Defined via <see cref="LoggerMessageAttribute"/> so the template is cached once
	/// instead of being parsed on every failure (CA1848); this path runs per request.
	/// </summary>
	[LoggerMessage(Level = LogLevel.Warning, Message = "Upstream backend {Backend} failed for model {Model}.")]
	private static partial void LogUpstreamFailure(
		ILogger   logger,
		string    backend,
		string    model,
		Exception exception);

	/// <summary>
	/// A marker type that names the <see cref="ILogger{TCategoryName}"/> category for generate-request
	/// handling, giving the endpoint's log entries a stable category rather than a generated name.
	/// </summary>
	internal sealed class GenerateRequestLog;
}
