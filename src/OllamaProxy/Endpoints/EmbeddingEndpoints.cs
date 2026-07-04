// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Net;
using System.Text.Json.Nodes;

using OllamaProxy.Contracts.Ollama;
using OllamaProxy.Core;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Endpoints;

/// <summary>
/// Maps and handles the Ollama embeddings endpoints: the current <c>POST /api/embed</c> (batch) and
/// the legacy <c>POST /api/embeddings</c> (single prompt). Both route the model through the
/// <see cref="IModelRouter"/> and delegate to the provider adapter's embeddings call, which targets
/// the backend's OpenAI-compatible <c>/embeddings</c> surface. The legacy endpoint is expressed in
/// terms of the modern one: its single prompt is wrapped as a one-element batch and the first vector
/// is unwrapped, so a single translation path serves both.
/// </summary>
static partial class EmbeddingEndpoints
{
	/// <summary>
	/// Maps the <c>/api/embed</c> and <c>/api/embeddings</c> routes onto the application's endpoint table.
	/// </summary>
	/// <param name="endpoints">The endpoint route builder to register the routes with.</param>
	/// <returns>The same <paramref name="endpoints"/> instance for chaining.</returns>
	public static IEndpointRouteBuilder MapEmbeddingEndpoints(this IEndpointRouteBuilder endpoints)
	{
		ArgumentNullException.ThrowIfNull(endpoints);

		endpoints.MapPost("/api/embed", HandleEmbedAsync);
		endpoints.MapPost("/api/embeddings", HandleLegacyEmbeddingsAsync);
		return endpoints;
	}

	/// <summary>
	/// Handles <c>POST /api/embed</c> by resolving the model and forwarding the batch embeddings request.
	/// </summary>
	/// <param name="context">The current HTTP context, used for response writing and cancellation.</param>
	/// <param name="request">The deserialized inbound embed request.</param>
	/// <param name="router">Resolves the model name to its backend and upstream identifier.</param>
	/// <param name="providerResolver">Selects the provider adapter for the resolved backend.</param>
	/// <param name="logger">Records routing decisions and upstream failures.</param>
	/// <returns>A task that completes when the response has been written.</returns>
	private static async Task HandleEmbedAsync(
		HttpContext                  context,
		OllamaEmbedRequest?          request,
		IModelRouter                 router,
		IProviderResolver            providerResolver,
		ILogger<EmbeddingRequestLog> logger)
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

		try
		{
			OllamaEmbedResponse response = await resolved.Adapter
				                               .CreateEmbeddingsAsync(
					                               resolved.Context,
					                               model.UpstreamModel,
					                               request,
					                               cancellationToken)
				                               .ConfigureAwait(false);

			await context.Response
				.WriteAsJsonAsync(response, OllamaJson.Options, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (ProviderException exception)
		{
			await WriteProviderErrorAsync(
					context,
					logger,
					model.BackendName,
					request.Model,
					exception,
					cancellationToken)
				.ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Handles the legacy <c>POST /api/embeddings</c> by wrapping its single prompt as a one-element
	/// batch, forwarding it through the same adapter, and projecting the first vector into the legacy
	/// single-embedding response. An upstream that returns no vectors yields an empty embedding.
	/// </summary>
	/// <param name="context">The current HTTP context, used for response writing and cancellation.</param>
	/// <param name="request">The deserialized inbound legacy embeddings request.</param>
	/// <param name="router">Resolves the model name to its backend and upstream identifier.</param>
	/// <param name="providerResolver">Selects the provider adapter for the resolved backend.</param>
	/// <param name="logger">Records routing decisions and upstream failures.</param>
	/// <returns>A task that completes when the response has been written.</returns>
	private static async Task HandleLegacyEmbeddingsAsync(
		HttpContext                    context,
		OllamaLegacyEmbeddingsRequest? request,
		IModelRouter                   router,
		IProviderResolver              providerResolver,
		ILogger<EmbeddingRequestLog>   logger)
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

		OllamaEmbedRequest embedRequest = new(
			Model: request.Model,
			Input: JsonValue.Create(request.Prompt),
			Options: request.Options,
			KeepAlive: request.KeepAlive);

		try
		{
			OllamaEmbedResponse response = await resolved.Adapter
				                               .CreateEmbeddingsAsync(
					                               resolved.Context,
					                               model.UpstreamModel,
					                               embedRequest,
					                               cancellationToken)
				                               .ConfigureAwait(false);

			IReadOnlyList<float> first = response.Embeddings.Count > 0 ? response.Embeddings[0] : [];

			await context.Response
				.WriteAsJsonAsync(new OllamaLegacyEmbeddingsResponse(first), OllamaJson.Options, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (ProviderException exception)
		{
			await WriteProviderErrorAsync(
					context,
					logger,
					model.BackendName,
					request.Model,
					exception,
					cancellationToken)
				.ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Logs an upstream embeddings failure and writes the mapped Ollama error response.
	/// </summary>
	/// <param name="context">The HTTP context the error is written to.</param>
	/// <param name="logger">The logger recording the failure.</param>
	/// <param name="backendName">The backend that failed.</param>
	/// <param name="clientModel">The client-facing model name involved.</param>
	/// <param name="exception">The provider failure to surface.</param>
	/// <param name="cancellationToken">A token to cancel writing the response.</param>
	/// <returns>A task that completes when the error has been written.</returns>
	private static Task WriteProviderErrorAsync(
		HttpContext       context,
		ILogger           logger,
		string            backendName,
		string            clientModel,
		ProviderException exception,
		CancellationToken cancellationToken)
	{
		LogUpstreamFailure(logger, backendName, clientModel, exception);

		return OllamaHttp.WriteErrorAsync(
			context,
			OllamaHttp.MapProviderStatus(exception),
			exception.Message,
			cancellationToken);
	}

	/// <summary>
	/// The pre-compiled warning logged when an upstream backend fails while servicing an embeddings
	/// request. Defined via <see cref="LoggerMessageAttribute"/> so the template is cached once
	/// instead of being parsed on every failure (CA1848); this path runs per request.
	/// </summary>
	[LoggerMessage(
		Level = LogLevel.Warning,
		Message = "Upstream backend {Backend} failed for embeddings model {Model}.")]
	private static partial void LogUpstreamFailure(
		ILogger   logger,
		string    backend,
		string    model,
		Exception exception);

	/// <summary>
	/// A marker type that names the <see cref="ILogger{TCategoryName}"/> category for embeddings
	/// handling, giving the endpoint's log entries a stable category rather than a generated name.
	/// </summary>
	internal sealed class EmbeddingRequestLog;
}
