// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using OllamaProxy.Contracts.Ollama;
using OllamaProxy.Core;
using OllamaProxy.Endpoints;
using OllamaProxy.Providers.Abstractions;

using static OllamaProxy.Tests.Endpoints.EndpointTestSupport;

// ReSharper disable ParameterOnlyUsedForPreconditionCheck.Local

namespace OllamaProxy.Tests.Endpoints;

/// <summary>
/// Tests for <see cref="EmbeddingEndpoints"/>, the Ollama embeddings surface: the modern batch
/// <c>POST /api/embed</c> and the legacy single-prompt <c>POST /api/embeddings</c> (expressed as a
/// one-element batch whose first vector is unwrapped).
/// <para>
/// The story covers both routes from validation to upstream failure:
/// </para>
/// <list type="number">
///     <item>
///         <description>
///         Mapping: both routes register with their methods (RegistersEmbeddingRoutes); a null builder is
///         rejected (WhenEndpointBuilderIsNull).
///         </description>
///     </item>
///     <item>
///         <description>
///         Embed: missing body/blank model/unknown model are rejected
///         (WhenRequestIsNull/WhenModelBlank/WhenModelUnknown); a resolved model returns the batch vectors
///         (WhenModelResolved) and forwards the request unchanged (WhenTuningFieldsSupplied); a provider
///         failure is mapped (WhenProviderFails).
///         </description>
///     </item>
///     <item>
///         <description>
///         Legacy embeddings: a resolved model unwraps the first vector (WhenModelResolved) and wraps the
///         prompt into a one-element batch input (WhenPromptSupplied); an upstream returning no vectors
///         yields an empty embedding (WhenNoVectorsReturned); blank/unknown model are rejected
///         (WhenModelBlank/WhenModelUnknown); a provider failure is mapped (WhenProviderFails).
///         </description>
///     </item>
/// </list>
/// </summary>
[Trait("Category", "Unit")]
public sealed class EmbeddingEndpointsTests
{
	private const string EmbedRoute  = "/api/embed";
	private const string LegacyRoute = "/api/embeddings";

	// --- 1. Mapping ---

	/// <summary>
	/// Verifies that <see cref="EmbeddingEndpoints.MapEmbeddingEndpoints"/> registers both the modern and
	/// legacy embeddings POST routes.
	/// </summary>
	[Fact]
	public void MapEmbeddingEndpoints_WhenCalled_RegistersEmbeddingRoutes()
	{
		// Arrange
		using WebApplication app = CreateApp(new FakeChatAdapter(), Model("alpha"));

		// Act
		string[] routes = ((IEndpointRouteBuilder)app).DataSources
			.SelectMany(source => source.Endpoints)
			.OfType<RouteEndpoint>()
			.Select(DescribeRoute)
			.OrderBy(description => description, StringComparer.Ordinal)
			.ToArray();

		// Assert
		Assert.Equal([$"{EmbedRoute}:POST", $"{LegacyRoute}:POST"], routes);
	}

	/// <summary>
	/// Verifies that <see cref="EmbeddingEndpoints.MapEmbeddingEndpoints"/> rejects a
	/// <see langword="null"/> route builder.
	/// </summary>
	[Fact]
	public void MapEmbeddingEndpoints_WhenEndpointBuilderIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() => EmbeddingEndpoints.MapEmbeddingEndpoints(null!));
		Assert.Equal("endpoints", exception.ParamName);
	}

	// --- 2. Embed (/api/embed) ---

	/// <summary>
	/// Verifies that <c>POST /api/embed</c> rejects an empty request body before any routing occurs.
	/// </summary>
	[Fact]
	public async Task Embed_WhenRequestIsNull_ReturnsBadRequest()
	{
		// Arrange
		await using WebApplication app = CreateApp(new FakeChatAdapter(), Model("alpha"));

		// Act
		EndpointResponse response = await InvokePostAsync(app, EmbedRoute, jsonBody: null);

		// Assert
		AssertOllamaError(response, StatusCodes.Status400BadRequest, "A model name is required.");
	}

	/// <summary>
	/// Verifies that <c>POST /api/embed</c> rejects a blank (whitespace-only) model name.
	/// </summary>
	[Fact]
	public async Task Embed_WhenModelBlank_ReturnsBadRequest()
	{
		// Arrange
		await using WebApplication app = CreateApp(new FakeChatAdapter(), Model("alpha"));

		// Act
		EndpointResponse response = await InvokePostAsync(
			                            app,
			                            EmbedRoute,
			                            """{ "model": "  ", "input": "hi" }""");

		// Assert
		AssertOllamaError(response, StatusCodes.Status400BadRequest, "A model name is required.");
	}

	/// <summary>
	/// Verifies that <c>POST /api/embed</c> reports a not-found error for a model absent from the catalog.
	/// </summary>
	[Fact]
	public async Task Embed_WhenModelUnknown_ReturnsNotFound()
	{
		// Arrange
		await using WebApplication app = CreateApp(new FakeChatAdapter(), Model("alpha"));

		// Act
		EndpointResponse response = await InvokePostAsync(
			                            app,
			                            EmbedRoute,
			                            """{ "model": "ghost", "input": "hi" }""");

		// Assert
		AssertOllamaError(response, StatusCodes.Status404NotFound, "Model 'ghost' was not found.");
	}

	/// <summary>
	/// Verifies that <c>POST /api/embed</c> returns the upstream batch of embedding vectors as JSON.
	/// </summary>
	[Fact]
	public async Task Embed_WhenModelResolved_ReturnsBatchVectors()
	{
		// Arrange
		OllamaEmbedResponse upstream = new("upstream-model", [[1.0f, 2.0f], [3.0f, 4.0f]]);
		FakeChatAdapter adapter = new() { OnCreateEmbeddings = () => Task.FromResult(upstream) };
		await using WebApplication app = CreateApp(adapter, Model("alpha"));

		// Act
		EndpointResponse response = await InvokePostAsync(
			                            app,
			                            EmbedRoute,
			                            """{ "model": "alpha", "input": "hi" }""");

		// Assert
		Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
		Assert.Equal(JsonWithCharsetContentType, response.ContentType);
		var actual = Deserialize<OllamaEmbedResponse>(response.Body);
		Assert.Equal(2, actual.Embeddings.Count);
		Assert.Equal([1.0f, 2.0f], actual.Embeddings[0]);
		Assert.Equal([3.0f, 4.0f], actual.Embeddings[1]);
	}

	/// <summary>
	/// Verifies that <c>POST /api/embed</c> forwards the request to the adapter unchanged: the multi-item
	/// <c>input</c> array, the sampling <c>options</c>, and the <c>keep_alive</c> hint reach the backend
	/// verbatim, and the client model name is rewritten to the upstream identifier.
	/// </summary>
	[Fact]
	public async Task Embed_WhenTuningFieldsSupplied_ForwardsRequestUnchanged()
	{
		// Arrange
		OllamaEmbedResponse upstream = new("upstream-model", [[1.0f]]);
		FakeChatAdapter adapter = new() { OnCreateEmbeddings = () => Task.FromResult(upstream) };
		await using WebApplication app = CreateApp(adapter, Model("alpha"));
		const string body =
			"""
			{
				"model": "alpha",
				"input": ["first", "second"],
				"options": { "temperature": 0.5 },
				"keep_alive": "5m"
			}
			""";

		// Act
		EndpointResponse response = await InvokePostAsync(app, EmbedRoute, body);

		// Assert
		Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
		Assert.NotNull(adapter.LastCall);
		// The client model resolves to the upstream identifier before the adapter is called.
		Assert.Equal("upstream-model", adapter.LastCall.UpstreamModel);
		OllamaEmbedRequest? forwarded = adapter.LastCall.EmbedRequest;
		Assert.NotNull(forwarded);
		Assert.Equal("alpha", forwarded.Model);
		// The multi-item input array is preserved as the raw JSON node the handler received.
		Assert.Equal(["first", "second"], forwarded.Input!.AsArray().Select(node => node!.GetValue<string>()));
		// The sampling options and keep-alive hint pass through unchanged.
		Assert.NotNull(forwarded.Options);
		Assert.Equal(0.5, forwarded.Options.Temperature);
		Assert.Equal("5m", forwarded.KeepAlive?.GetValue<string>());
	}

	/// <summary>
	/// Verifies that a <see cref="ProviderException"/> raised during <c>/api/embed</c> is mapped to its
	/// corresponding Ollama-shaped error (a backend 5xx normalizes to a <c>502 Bad Gateway</c>).
	/// </summary>
	[Fact]
	public async Task Embed_WhenProviderFails_ReturnsMappedError()
	{
		// Arrange: a backend 503 is not a client-correctable request error, so it normalizes to 502.
		FakeChatAdapter adapter = new()
		{
			OnCreateEmbeddings = () => throw new ProviderException(
				                           HttpStatusCode.ServiceUnavailable,
				                           "Upstream is unavailable.")
		};
		await using WebApplication app = CreateApp(adapter, Model("alpha"));

		// Act
		EndpointResponse response = await InvokePostAsync(
			                            app,
			                            EmbedRoute,
			                            """{ "model": "alpha", "input": "hi" }""");

		// Assert
		AssertOllamaError(response, StatusCodes.Status502BadGateway, "Upstream is unavailable.");
	}

	// --- 3. Legacy embeddings (/api/embeddings) ---

	/// <summary>
	/// Verifies that <c>POST /api/embeddings</c> unwraps the first vector of the one-element batch into
	/// the legacy single-embedding response.
	/// </summary>
	[Fact]
	public async Task LegacyEmbeddings_WhenModelResolved_ReturnsFirstVector()
	{
		// Arrange
		OllamaEmbedResponse upstream = new("upstream-model", [[5.0f, 6.0f, 7.0f]]);
		FakeChatAdapter adapter = new() { OnCreateEmbeddings = () => Task.FromResult(upstream) };
		await using WebApplication app = CreateApp(adapter, Model("alpha"));

		// Act
		EndpointResponse response = await InvokePostAsync(
			                            app,
			                            LegacyRoute,
			                            """{ "model": "alpha", "prompt": "hi" }""");

		// Assert
		Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
		Assert.Equal(JsonWithCharsetContentType, response.ContentType);
		var actual = Deserialize<OllamaLegacyEmbeddingsResponse>(response.Body);
		Assert.Equal([5.0f, 6.0f, 7.0f], actual.Embedding);
	}

	/// <summary>
	/// Verifies that <c>POST /api/embeddings</c> wraps the single <c>prompt</c> into a one-element
	/// <c>input</c> for the modern batch adapter, and carries the <c>options</c> and <c>keep_alive</c>
	/// hints through unchanged.
	/// </summary>
	[Fact]
	public async Task LegacyEmbeddings_WhenPromptSupplied_WrapsPromptIntoBatchInput()
	{
		// Arrange
		OllamaEmbedResponse upstream = new("upstream-model", [[1.0f]]);
		FakeChatAdapter adapter = new() { OnCreateEmbeddings = () => Task.FromResult(upstream) };
		await using WebApplication app = CreateApp(adapter, Model("alpha"));
		const string body =
			"""
			{
				"model": "alpha",
				"prompt": "embed me",
				"options": { "seed": 7 },
				"keep_alive": "5m"
			}
			""";

		// Act
		EndpointResponse response = await InvokePostAsync(app, LegacyRoute, body);

		// Assert
		Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
		Assert.NotNull(adapter.LastCall);
		Assert.Equal("upstream-model", adapter.LastCall.UpstreamModel);
		OllamaEmbedRequest? forwarded = adapter.LastCall.EmbedRequest;
		Assert.NotNull(forwarded);
		Assert.Equal("alpha", forwarded.Model);
		// The legacy single prompt is wrapped into the modern input as a bare JSON string value.
		Assert.Equal("embed me", forwarded.Input!.GetValue<string>());
		// The compatibility tuning fields survive the wrapping.
		Assert.NotNull(forwarded.Options);
		Assert.Equal(7, forwarded.Options.Seed);
		Assert.Equal("5m", forwarded.KeepAlive?.GetValue<string>());
	}

	/// <summary>
	/// Verifies that <c>POST /api/embeddings</c> yields an empty embedding when the upstream returns no
	/// vectors.
	/// </summary>
	[Fact]
	public async Task LegacyEmbeddings_WhenNoVectorsReturned_ReturnsEmptyEmbedding()
	{
		// Arrange: an upstream that produced no vectors must not throw — it degrades to an empty vector.
		OllamaEmbedResponse upstream = new("upstream-model", []);
		FakeChatAdapter adapter = new() { OnCreateEmbeddings = () => Task.FromResult(upstream) };
		await using WebApplication app = CreateApp(adapter, Model("alpha"));

		// Act
		EndpointResponse response = await InvokePostAsync(
			                            app,
			                            LegacyRoute,
			                            """{ "model": "alpha", "prompt": "hi" }""");

		// Assert
		Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
		var actual = Deserialize<OllamaLegacyEmbeddingsResponse>(response.Body);
		Assert.Empty(actual.Embedding);
	}

	/// <summary>
	/// Verifies that <c>POST /api/embeddings</c> rejects a blank (whitespace-only) model name.
	/// </summary>
	[Fact]
	public async Task LegacyEmbeddings_WhenModelBlank_ReturnsBadRequest()
	{
		// Arrange
		await using WebApplication app = CreateApp(new FakeChatAdapter(), Model("alpha"));

		// Act
		EndpointResponse response = await InvokePostAsync(
			                            app,
			                            LegacyRoute,
			                            """{ "model": "  ", "prompt": "hi" }""");

		// Assert
		AssertOllamaError(response, StatusCodes.Status400BadRequest, "A model name is required.");
	}

	/// <summary>
	/// Verifies that <c>POST /api/embeddings</c> reports a not-found error for a model absent from the
	/// catalog.
	/// </summary>
	[Fact]
	public async Task LegacyEmbeddings_WhenModelUnknown_ReturnsNotFound()
	{
		// Arrange
		await using WebApplication app = CreateApp(new FakeChatAdapter(), Model("alpha"));

		// Act
		EndpointResponse response = await InvokePostAsync(
			                            app,
			                            LegacyRoute,
			                            """{ "model": "ghost", "prompt": "hi" }""");

		// Assert
		AssertOllamaError(response, StatusCodes.Status404NotFound, "Model 'ghost' was not found.");
	}

	/// <summary>
	/// Verifies that a <see cref="ProviderException"/> raised during <c>/api/embeddings</c> is mapped to
	/// its corresponding Ollama-shaped error (a backend 5xx normalizes to a <c>502 Bad Gateway</c>).
	/// </summary>
	[Fact]
	public async Task LegacyEmbeddings_WhenProviderFails_ReturnsMappedError()
	{
		// Arrange: a backend 503 is not a client-correctable request error, so it normalizes to 502.
		FakeChatAdapter adapter = new()
		{
			OnCreateEmbeddings = () => throw new ProviderException(
				                           HttpStatusCode.ServiceUnavailable,
				                           "Upstream is unavailable.")
		};
		await using WebApplication app = CreateApp(adapter, Model("alpha"));

		// Act
		EndpointResponse response = await InvokePostAsync(
			                            app,
			                            LegacyRoute,
			                            """{ "model": "alpha", "prompt": "hi" }""");

		// Assert
		AssertOllamaError(response, StatusCodes.Status502BadGateway, "Upstream is unavailable.");
	}

	/// <summary>
	/// Builds an in-memory application whose endpoint table holds only the embeddings routes, backed by a
	/// router over <paramref name="models"/> and a resolver returning <paramref name="adapter"/>.
	/// </summary>
	/// <param name="adapter">The provider adapter every backend resolves to.</param>
	/// <param name="models">The models the router exposes and resolves.</param>
	/// <returns>The configured web application.</returns>
	private static WebApplication CreateApp(IProviderAdapter adapter, params RegisteredModel[] models)
	{
		WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
		builder.Services.AddSingleton<IModelRouter>(new FakeModelRouter(models));
		builder.Services.AddSingleton<IProviderResolver>(new FakeProviderResolver(adapter));

		WebApplication app = builder.Build();
		app.MapEmbeddingEndpoints();
		return app;
	}

	/// <summary>
	/// Deserializes an endpoint response body using the Ollama serializer options, asserting presence.
	/// </summary>
	/// <typeparam name="T">The contract type to deserialize into.</typeparam>
	/// <param name="body">The response body JSON.</param>
	/// <returns>The deserialized instance.</returns>
	private static T Deserialize<T>(string body)
	{
		var value = JsonSerializer.Deserialize<T>(body, OllamaJson.Options);
		Assert.NotNull(value);
		return value;
	}

	/// <summary>
	/// Asserts that a response is the Ollama-shaped error with the expected status and message.
	/// </summary>
	/// <param name="response">The response returned by the endpoint invocation.</param>
	/// <param name="expectedStatusCode">The exact expected HTTP status code.</param>
	/// <param name="expectedMessage">The exact expected error message.</param>
	private static void AssertOllamaError(EndpointResponse response, int expectedStatusCode, string expectedMessage)
	{
		Assert.Equal(expectedStatusCode, response.StatusCode);
		Assert.Equal(JsonWithCharsetContentType, response.ContentType);
		var error = Deserialize<OllamaErrorResponse>(response.Body);
		Assert.Equal(expectedMessage, error.Error);
	}
}
