// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Net;

using OllamaProxy.Contracts.Ollama;
using OllamaProxy.Core;

namespace OllamaProxy.Endpoints;

/// <summary>
/// Maps and handles the Ollama model-metadata endpoints <c>GET /api/tags</c> and <c>POST /api/show</c>.
/// Both read exclusively from the in-memory <see cref="IModelRouter"/> catalog assembled at startup
/// (no upstream call is made per request) and project the resolved models onto the Ollama shapes via
/// <see cref="ModelProjection"/>. <c>/api/tags</c> backs a client's model picker; <c>/api/show</c>
/// reports the capability list that lets tool-aware clients enable function calling.
/// </summary>
static class ModelEndpoints
{
	/// <summary>
	/// Maps the <c>/api/tags</c> and <c>/api/show</c> routes onto the application's endpoint table.
	/// </summary>
	/// <param name="endpoints">The endpoint route builder to register the routes with.</param>
	/// <returns>The same <paramref name="endpoints"/> instance for chaining.</returns>
	public static IEndpointRouteBuilder MapModelEndpoints(this IEndpointRouteBuilder endpoints)
	{
		ArgumentNullException.ThrowIfNull(endpoints);

		endpoints.MapGet("/api/tags", HandleTags);
		endpoints.MapPost("/api/show", HandleShowAsync);
		return endpoints;
	}

	/// <summary>
	/// Handles <c>GET /api/tags</c> by projecting every catalog entry into an Ollama list entry. A
	/// single timestamp is captured up front so all entries in the listing report a consistent
	/// modification time.
	/// </summary>
	/// <param name="router">The catalog of client-facing models.</param>
	/// <param name="timeProvider">The clock used for the synthesized modification timestamp.</param>
	/// <returns>The Ollama tags response.</returns>
	private static OllamaTagsResponse HandleTags(IModelRouter router, TimeProvider timeProvider)
	{
		string modifiedAt = timeProvider.GetUtcNow().ToString("o");

		IReadOnlyList<OllamaModelEntry> entries = router.GetModels()
			.Select(model => ModelProjection.ToModelEntry(model, modifiedAt))
			.ToArray();

		return new OllamaTagsResponse(entries);
	}

	/// <summary>
	/// Handles <c>POST /api/show</c> by resolving the requested model and projecting its capabilities
	/// and metadata. An unknown or unspecified model yields an Ollama-shaped error so the client can
	/// surface a meaningful message rather than an empty body.
	/// </summary>
	/// <param name="context">The current HTTP context, used for error writing and cancellation.</param>
	/// <param name="request">The deserialized show request naming the model to describe.</param>
	/// <param name="router">Resolves the model name to its registered entry.</param>
	/// <returns>A task that completes when the response has been written.</returns>
	private static async Task HandleShowAsync(
		HttpContext        context,
		OllamaShowRequest? request,
		IModelRouter       router)
	{
		CancellationToken cancellationToken = context.RequestAborted;

		if (request is null || string.IsNullOrWhiteSpace(request.Model))
		{
			await OllamaHttp
				.WriteErrorAsync(context, HttpStatusCode.BadRequest, "A model name is required.", cancellationToken)
				.ConfigureAwait(false);
			return;
		}

		if (!router.TryResolve(request.Model, out RegisteredModel? model))
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

		await context.Response
			.WriteAsJsonAsync(ModelProjection.ToShowResponse(model), OllamaJson.Options, cancellationToken)
			.ConfigureAwait(false);
	}
}
