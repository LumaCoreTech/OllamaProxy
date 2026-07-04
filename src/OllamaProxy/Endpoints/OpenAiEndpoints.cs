// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using OllamaProxy.Configuration;
using OllamaProxy.Contracts.OpenAi;
using OllamaProxy.Core;
using OllamaProxy.Providers.Abstractions;
using OllamaProxy.Providers.OpenAiProtocol;

namespace OllamaProxy.Endpoints;

/// <summary>
/// Maps and handles the inbound OpenAI-compatible surface that real Ollama also exposes:
/// <c>GET /v1/models</c>, <c>POST /v1/chat/completions</c>, <c>POST /v1/completions</c>, and
/// <c>POST /v1/embeddings</c>. Clients that drive the proxy through the OpenAI protocol (notably the
/// OpenAI SDK used by GitHub Copilot Chat) target these routes rather than the native <c>/api</c>
/// surface. Because the proxy's upstream is itself OpenAI-compatible, these handlers forward the
/// request body verbatim through <see cref="IOpenAiForwarder"/>, rewriting only the <c>model</c>
/// field between the client-facing name and the resolved upstream identifier; this preserves provider
/// extensions and streamed tool-call deltas without a lossy round-trip through the Ollama contracts.
/// </summary>
static partial class OpenAiEndpoints
{
	private const string ChatCompletionsPath = "chat/completions";
	private const string CompletionsPath     = "completions";
	private const string EmbeddingsPath      = "embeddings";
	private const string ModelField          = "model";
	private const string StreamField         = "stream";

	/// <summary>
	/// Maps the <c>/v1/models</c>, <c>/v1/chat/completions</c>, <c>/v1/completions</c>, and
	/// <c>/v1/embeddings</c> routes onto the application's endpoint table.
	/// </summary>
	/// <param name="endpoints">The endpoint route builder to register the routes with.</param>
	/// <returns>The same <paramref name="endpoints"/> instance for chaining.</returns>
	public static IEndpointRouteBuilder MapOpenAiApi(this IEndpointRouteBuilder endpoints)
	{
		ArgumentNullException.ThrowIfNull(endpoints);

		endpoints.MapGet("/v1/models", HandleModels);

		endpoints.MapPost(
			"/v1/chat/completions",
			(
					HttpContext               context,
					IModelRouter              router,
					IProviderResolver         resolver,
					ILogger<OpenAiRequestLog> logger) =>
				HandleForwardAsync(context, router, resolver, logger, ChatCompletionsPath, allowStreaming: true));

		endpoints.MapPost(
			"/v1/completions",
			(
					HttpContext               context,
					IModelRouter              router,
					IProviderResolver         resolver,
					ILogger<OpenAiRequestLog> logger) =>
				HandleForwardAsync(context, router, resolver, logger, CompletionsPath, allowStreaming: true));

		endpoints.MapPost(
			"/v1/embeddings",
			(
					HttpContext               context,
					IModelRouter              router,
					IProviderResolver         resolver,
					ILogger<OpenAiRequestLog> logger) =>
				HandleForwardAsync(context, router, resolver, logger, EmbeddingsPath, allowStreaming: false));

		return endpoints;
	}

	/// <summary>
	/// Handles <c>GET /v1/models</c> by projecting every catalog entry into an OpenAI model-list entry.
	/// A single timestamp is captured up front so all entries report a consistent creation time.
	/// </summary>
	/// <param name="router">The catalog of client-facing models.</param>
	/// <param name="timeProvider">The clock used for the synthesized creation timestamp.</param>
	/// <returns>The OpenAI model-list response.</returns>
	private static OpenAiModelListResponse HandleModels(IModelRouter router, TimeProvider timeProvider)
	{
		long created = timeProvider.GetUtcNow().ToUnixTimeSeconds();

		IReadOnlyList<OpenAiModelListEntry> entries = router.GetModels()
			.Select(model => new OpenAiModelListEntry(model.Name, created, model.BackendName))
			.ToArray();

		return new OpenAiModelListResponse(entries);
	}

	/// <summary>
	/// Handles a forwarding POST request: reads the body, resolves and rewrites the model, selects the
	/// backend's OpenAI forwarder, and either streams the upstream Server-Sent-Events response or
	/// returns the single aggregated JSON object. A failed resolution yields a <c>404</c>; an upstream
	/// provider failure is mapped to its corresponding status, both in the OpenAI error envelope.
	/// </summary>
	/// <param name="context">The current HTTP context, used for body access, response writing, and cancellation.</param>
	/// <param name="router">Resolves the model name to its backend and upstream identifier.</param>
	/// <param name="providerResolver">Selects the provider adapter for the resolved backend.</param>
	/// <param name="logger">Records routing decisions and upstream failures.</param>
	/// <param name="upstreamPath">The backend-relative path to forward to.</param>
	/// <param name="allowStreaming">Whether the request may produce a streamed response.</param>
	/// <returns>A task that completes when the response has been fully written.</returns>
	private static async Task HandleForwardAsync(
		HttpContext               context,
		IModelRouter              router,
		IProviderResolver         providerResolver,
		ILogger<OpenAiRequestLog> logger,
		string                    upstreamPath,
		bool                      allowStreaming)
	{
		CancellationToken cancellationToken = context.RequestAborted;

		JsonObject? body = await ReadBodyAsync(context, cancellationToken).ConfigureAwait(false);

		if (body is null)
		{
			await OpenAiHttp
				.WriteErrorAsync(
					context,
					HttpStatusCode.BadRequest,
					"The request body must be a JSON object.",
					"invalid_request_error",
					cancellationToken)
				.ConfigureAwait(false);
			return;
		}

		string? requestedModel = ReadStringField(body, ModelField);

		if (string.IsNullOrWhiteSpace(requestedModel))
		{
			await OpenAiHttp
				.WriteErrorAsync(
					context,
					HttpStatusCode.BadRequest,
					"A model name is required.",
					"invalid_request_error",
					cancellationToken)
				.ConfigureAwait(false);
			return;
		}

		if (!EndpointRouting.TryResolveBackend(
			    router,
			    providerResolver,
			    requestedModel,
			    out RegisteredModel? model,
			    out ResolvedBackend? resolved))
		{
			await OpenAiHttp
				.WriteErrorAsync(
					context,
					HttpStatusCode.NotFound,
					$"Model '{requestedModel}' was not found.",
					"invalid_request_error",
					cancellationToken)
				.ConfigureAwait(false);
			return;
		}

		if (resolved.Adapter is not IOpenAiForwarder forwarder)
		{
			await OpenAiHttp
				.WriteErrorAsync(
					context,
					HttpStatusCode.BadGateway,
					$"Backend '{model.BackendName}' does not support the OpenAI protocol.",
					"api_error",
					cancellationToken)
				.ConfigureAwait(false);
			return;
		}

		// Rewrite the client-facing model name to the resolved upstream identifier; the response path
		// restores the client's name so the round-trip is transparent.
		body[ModelField] = model.UpstreamModel;

		bool stream = allowStreaming && ReadBoolField(body, StreamField);

		try
		{
			if (stream)
			{
				await StreamAsync(
						context,
						forwarder,
						resolved.Context,
						upstreamPath,
						body,
						requestedModel,
						model.ReasoningEffort,
						cancellationToken)
					.ConfigureAwait(false);
			}
			else
			{
				await CompleteAsync(
						context,
						forwarder,
						resolved.Context,
						upstreamPath,
						body,
						requestedModel,
						model.ReasoningEffort,
						cancellationToken)
					.ConfigureAwait(false);
			}
		}
		catch (ProviderException exception)
		{
			LogUpstreamFailure(logger, model.BackendName, requestedModel, exception);

			await OpenAiHttp
				.WriteErrorAsync(
					context,
					OpenAiHttp.MapProviderStatus(exception),
					exception.Message,
					"api_error",
					cancellationToken)
				.ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Forwards a streaming request and relays each upstream Server-Sent-Events frame to the client,
	/// rewriting the <c>model</c> field of every chunk back to the client-facing name and appending the
	/// terminating <c>[DONE]</c> sentinel.
	/// </summary>
	/// <param name="context">The HTTP context whose response receives the stream.</param>
	/// <param name="forwarder">The OpenAI forwarder servicing the backend.</param>
	/// <param name="backend">The backend identity to target.</param>
	/// <param name="upstreamPath">The backend-relative path to forward to.</param>
	/// <param name="body">The rewritten request body to forward.</param>
	/// <param name="clientModel">The client-facing model name to echo on every chunk.</param>
	/// <param name="pinnedEffort">The resolved model's pinned reasoning effort, or <see langword="null"/> when none is pinned.</param>
	/// <param name="cancellationToken">A token tied to the client connection.</param>
	/// <returns>A task that completes when the full stream has been written.</returns>
	private static async Task StreamAsync(
		HttpContext       context,
		IOpenAiForwarder  forwarder,
		BackendContext    backend,
		string            upstreamPath,
		JsonObject        body,
		string            clientModel,
		ReasoningEffort?  pinnedEffort,
		CancellationToken cancellationToken)
	{
		context.Response.ContentType = OpenAiHttp.EventStreamContentType;

		IAsyncEnumerable<string> payloads =
			forwarder.ForwardSseAsync(backend, upstreamPath, body, pinnedEffort, cancellationToken);

		await foreach (string payload in payloads.ConfigureAwait(false))
		{
			string rewritten = RewriteModel(payload, clientModel);
			await OpenAiHttp.WriteSseFrameAsync(context, rewritten, cancellationToken).ConfigureAwait(false);
		}

		await OpenAiHttp.WriteSseDoneAsync(context, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Forwards a non-streaming request and writes the single aggregated JSON response, rewriting the
	/// <c>model</c> field back to the client-facing name.
	/// </summary>
	/// <param name="context">The HTTP context whose response receives the JSON body.</param>
	/// <param name="forwarder">The OpenAI forwarder servicing the backend.</param>
	/// <param name="backend">The backend identity to target.</param>
	/// <param name="upstreamPath">The backend-relative path to forward to.</param>
	/// <param name="body">The rewritten request body to forward.</param>
	/// <param name="clientModel">The client-facing model name to echo in the response.</param>
	/// <param name="pinnedEffort">The resolved model's pinned reasoning effort, or <see langword="null"/> when none is pinned.</param>
	/// <param name="cancellationToken">A token tied to the client connection.</param>
	/// <returns>A task that completes when the response has been written.</returns>
	private static async Task CompleteAsync(
		HttpContext       context,
		IOpenAiForwarder  forwarder,
		BackendContext    backend,
		string            upstreamPath,
		JsonObject        body,
		string            clientModel,
		ReasoningEffort?  pinnedEffort,
		CancellationToken cancellationToken)
	{
		JsonObject response = await forwarder
			                      .ForwardJsonAsync(backend, upstreamPath, body, pinnedEffort, cancellationToken)
			                      .ConfigureAwait(false);

		if (response.ContainsKey(ModelField)) response[ModelField] = clientModel;

		context.Response.ContentType = "application/json";

		await context.Response
			.WriteAsync(response.ToJsonString(), cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>
	/// Reads the request body as a <see cref="JsonObject"/>, returning <see langword="null"/> when the
	/// body is absent, empty, or not a JSON object.
	/// </summary>
	/// <param name="context">The HTTP context whose request body is read.</param>
	/// <param name="cancellationToken">A token observed while reading the body.</param>
	/// <returns>The parsed JSON object, or <see langword="null"/> when the body is invalid.</returns>
	private static async Task<JsonObject?> ReadBodyAsync(HttpContext context, CancellationToken cancellationToken)
	{
		try
		{
			JsonNode? node = await context.Request
				                 .ReadFromJsonAsync<JsonNode>(cancellationToken)
				                 .ConfigureAwait(false);

			return node as JsonObject;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	/// <summary>
	/// Reads a string-valued field from the request body, returning <see langword="null"/> when the
	/// field is absent, JSON <c>null</c>, or present with a non-string type. The non-throwing
	/// <see cref="JsonValue.TryGetValue{TValue}(out TValue)"/> keeps a malformed field (such as a
	/// numeric <c>model</c>) on the clean validation path instead of surfacing as an unhandled
	/// exception (an opaque <c>500</c>).
	/// </summary>
	/// <param name="body">The parsed request body.</param>
	/// <param name="field">The name of the field to read.</param>
	/// <returns>The field's string value, or <see langword="null"/> when missing or not a string.</returns>
	private static string? ReadStringField(JsonObject body, string field) =>
		body[field] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

	/// <summary>
	/// Reads a boolean-valued field from the request body, returning <see langword="false"/> when the
	/// field is absent, JSON <c>null</c>, or present with a non-boolean type. A malformed <c>stream</c>
	/// flag therefore degrades to non-streaming (the OpenAI default) rather than throwing.
	/// </summary>
	/// <param name="body">The parsed request body.</param>
	/// <param name="field">The name of the field to read.</param>
	/// <returns>The field's boolean value, or <see langword="false"/> when missing or not a boolean.</returns>
	private static bool ReadBoolField(JsonObject body, string field) =>
		body[field] is JsonValue value && value.TryGetValue(out bool flag) && flag;

	/// <summary>
	/// Rewrites the <c>model</c> field of a raw chunk payload to the client-facing name, leaving every
	/// other field untouched. A payload that cannot be parsed is relayed verbatim so a malformed frame
	/// never aborts the stream.
	/// </summary>
	/// <param name="payload">The raw JSON payload of one SSE frame.</param>
	/// <param name="clientModel">The client-facing model name to write.</param>
	/// <returns>The payload with its model field rewritten, or the original text on a parse failure.</returns>
	private static string RewriteModel(string payload, string clientModel)
	{
		try
		{
			if (JsonNode.Parse(payload) is not JsonObject chunk) return payload;

			if (chunk.ContainsKey(ModelField)) chunk[ModelField] = clientModel;

			return chunk.ToJsonString();
		}
		catch (JsonException)
		{
			return payload;
		}
	}

	/// <summary>
	/// The pre-compiled warning logged when an upstream backend fails while servicing an OpenAI
	/// request. Defined via <see cref="LoggerMessageAttribute"/> so the template is cached once instead
	/// of being parsed per failure (CA1848); this path runs per request.
	/// </summary>
	[LoggerMessage(Level = LogLevel.Warning, Message = "Upstream backend {Backend} failed for model {Model}.")]
	private static partial void LogUpstreamFailure(
		ILogger   logger,
		string    backend,
		string    model,
		Exception exception);

	/// <summary>
	/// A marker type that names the <see cref="ILogger{TCategoryName}"/> category for OpenAI-request
	/// handling. It carries no members; its sole purpose is to give the endpoint's log entries a stable,
	/// discoverable category rather than logging under a framework-generated name.
	/// </summary>
	internal sealed class OpenAiRequestLog;
}
