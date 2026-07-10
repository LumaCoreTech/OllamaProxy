// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using OllamaProxy.Contracts.Ollama;
using OllamaProxy.Core;
using OllamaProxy.Endpoints;

using static OllamaProxy.Tests.Endpoints.EndpointTestSupport;

// ReSharper disable ParameterOnlyUsedForPreconditionCheck.Local

namespace OllamaProxy.Tests.Endpoints;

/// <summary>
/// Tests for <see cref="ModelEndpoints"/>, the read-only Ollama metadata surface (<c>GET /api/tags</c>
/// and <c>POST /api/show</c>) that projects the in-memory catalog onto the Ollama shapes without any
/// upstream call.
/// <para>
/// The story runs from the harmless read path to the guarded lookup:
/// </para>
/// <list type="number">
///     <item>
///         <description>
///         Mapping: the two documented routes register with their methods (RegistersTagsAndShowRoutes), and
///         a null builder is rejected (WhenEndpointBuilderIsNull).
///         </description>
///     </item>
///     <item>
///         <description>
///         Tags: an empty catalog yields the empty list (WhenCatalogEmpty); a populated catalog projects
///         every entry with a consistent timestamp (WhenModelsRegistered).
///         </description>
///     </item>
///     <item>
///         <description>
///         Show: a resolved model reports its capabilities (WhenModelResolved); a missing/blank/unknown
///         model is rejected with the Ollama-shaped error (WhenModelMissing/Blank/Unknown).
///         </description>
///     </item>
/// </list>
/// </summary>
[Trait("Category", "Unit")]
public sealed class ModelEndpointsTests
{
	private const string TagsRoute = "/api/tags";
	private const string ShowRoute = "/api/show";

	private static readonly DateTimeOffset FixedNow = new(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);

	// --- 1. Mapping ---

	/// <summary>
	/// Verifies that <see cref="ModelEndpoints.MapModelEndpoints"/> registers exactly the tags GET and
	/// show POST routes.
	/// </summary>
	[Fact]
	public void MapModelEndpoints_WhenCalled_RegistersTagsAndShowRoutes()
	{
		// Arrange
		using WebApplication app = CreateApp();

		// Act
		string[] routes = ((IEndpointRouteBuilder)app).DataSources
			.SelectMany(source => source.Endpoints)
			.OfType<RouteEndpoint>()
			.Select(DescribeRoute)
			.OrderBy(description => description, StringComparer.Ordinal)
			.ToArray();

		// Assert
		Assert.Equal([$"{ShowRoute}:POST", $"{TagsRoute}:GET"], routes);
	}

	/// <summary>
	/// Verifies that <see cref="ModelEndpoints.MapModelEndpoints"/> rejects a <see langword="null"/>
	/// route builder.
	/// </summary>
	[Fact]
	public void MapModelEndpoints_WhenEndpointBuilderIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() => ModelEndpoints.MapModelEndpoints(null!));
		Assert.Equal("endpoints", exception.ParamName);
	}

	// --- 2. Tags ---

	/// <summary>
	/// Verifies that <c>GET /api/tags</c> returns the empty Ollama model list when the catalog is empty.
	/// </summary>
	[Fact]
	public async Task Tags_WhenCatalogEmpty_ReturnsEmptyModelList()
	{
		// Arrange
		await using WebApplication app = CreateApp();

		// Act
		EndpointResponse response = await InvokeGetAsync(app, TagsRoute);

		// Assert
		Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
		Assert.Equal(JsonWithCharsetContentType, response.ContentType);
		Assert.Equal("{\"models\":[]}", response.Body);
	}

	/// <summary>
	/// Verifies that <c>GET /api/tags</c> projects every registered model into a tags entry stamped with
	/// a single consistent modification timestamp.
	/// </summary>
	[Fact]
	public async Task Tags_WhenModelsRegistered_ProjectsEveryEntry()
	{
		// Arrange
		RegisteredModel first = Model("alpha", upstreamModel: "up-alpha");
		RegisteredModel second = Model("beta", upstreamModel: "up-beta");
		await using WebApplication app = CreateApp(first, second);
		string modifiedAt = FixedNow.ToString("o");
		OllamaTagsResponse expected = new(
		[
			ModelProjection.ToModelEntry(first, modifiedAt),
			ModelProjection.ToModelEntry(second, modifiedAt)
		]);

		// Act
		EndpointResponse response = await InvokeGetAsync(app, TagsRoute);

		// Assert
		Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
		Assert.Equal(JsonWithCharsetContentType, response.ContentType);
		// OllamaTagsResponse holds an IReadOnlyList whose record equality is reference-based, so the two
		// projections are compared by their normalized JSON (the observable contract). Normalization
		// reconciles harmless Unicode-escaping differences between the two serializer configurations.
		Assert.Equal(Normalize(JsonSerializer.Serialize(expected, OllamaJson.Options)), Normalize(response.Body));
	}

	// --- 3. Show ---

	/// <summary>
	/// Verifies that <c>POST /api/show</c> resolves the model and reports its projected capabilities and
	/// details.
	/// </summary>
	[Fact]
	public async Task Show_WhenModelResolved_ReturnsProjectedCapabilities()
	{
		// Arrange
		RegisteredModel model = Model("alpha");
		await using WebApplication app = CreateApp(model);
		OllamaShowResponse expected = ModelProjection.ToShowResponse(model);

		// Act
		EndpointResponse response = await InvokePostAsync(
			                            app,
			                            ShowRoute,
			                            """{ "model": "alpha" }""");

		// Assert
		Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
		Assert.Equal(JsonWithCharsetContentType, response.ContentType);
		var actual = Deserialize<OllamaShowResponse>(response.Body);

		// ModelInfo values round-trip to JsonElement, so the decisive contract fields (capabilities and
		// details) are asserted directly rather than via whole-record equality.
		Assert.Equal(expected.Capabilities, actual.Capabilities);
		Assert.Equal(expected.Details.ParentModel, actual.Details.ParentModel);
		Assert.Equal(expected.Details.Format, actual.Details.Format);
		Assert.Equal(expected.Details.Family, actual.Details.Family);
		Assert.Equal(expected.Details.Families, actual.Details.Families);
		Assert.Equal(expected.Details.ParameterSize, actual.Details.ParameterSize);
		Assert.Equal(expected.Details.QuantizationLevel, actual.Details.QuantizationLevel);
	}

	/// <summary>
	/// Verifies that <c>POST /api/show</c> rejects a request whose body omits the model name.
	/// </summary>
	[Fact]
	public async Task Show_WhenModelMissing_ReturnsBadRequest()
	{
		// Arrange
		await using WebApplication app = CreateApp();

		// Act
		EndpointResponse response = await InvokePostAsync(app, ShowRoute, "{}");

		// Assert
		AssertOllamaError(response, StatusCodes.Status400BadRequest, "A model name is required.");
	}

	/// <summary>
	/// Verifies that <c>POST /api/show</c> rejects a blank (whitespace-only) model name.
	/// </summary>
	[Fact]
	public async Task Show_WhenModelBlank_ReturnsBadRequest()
	{
		// Arrange
		await using WebApplication app = CreateApp();

		// Act
		EndpointResponse response = await InvokePostAsync(
			                            app,
			                            ShowRoute,
			                            """{ "model": "   " }""");

		// Assert
		AssertOllamaError(response, StatusCodes.Status400BadRequest, "A model name is required.");
	}

	/// <summary>
	/// Verifies that <c>POST /api/show</c> reports a not-found error for a model that is not in the
	/// catalog.
	/// </summary>
	[Fact]
	public async Task Show_WhenModelUnknown_ReturnsNotFound()
	{
		// Arrange
		await using WebApplication app = CreateApp(Model("alpha"));

		// Act
		EndpointResponse response = await InvokePostAsync(
			                            app,
			                            ShowRoute,
			                            """{ "model": "ghost" }""");

		// Assert
		AssertOllamaError(response, StatusCodes.Status404NotFound, "Model 'ghost' was not found.");
	}

	/// <summary>
	/// Builds an in-memory application whose endpoint table holds only the model routes, backed by a
	/// router over <paramref name="models"/> and a fixed clock for deterministic timestamps.
	/// </summary>
	/// <param name="models">The models the router exposes and resolves.</param>
	/// <returns>The configured web application.</returns>
	private static WebApplication CreateApp(params RegisteredModel[] models)
	{
		WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
		builder.Services.AddSingleton<IModelRouter>(new FakeModelRouter(models));
		builder.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(FixedNow));

		WebApplication app = builder.Build();
		app.MapModelEndpoints();
		return app;
	}

	/// <summary>
	/// Deserializes an endpoint response body into <typeparamref name="T"/> using the Ollama serializer
	/// options, asserting the body is present.
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
		// The body is deserialized rather than string-compared because the serializer Unicode-escapes
		// characters such as the apostrophe (e.g. \u0027), which is semantically identical but not a
		// byte-for-byte match against the raw message.
		var error = Deserialize<OllamaErrorResponse>(response.Body);
		Assert.Equal(expectedMessage, error.Error);
	}

	/// <summary>
	/// Normalizes a JSON string by round-tripping it through the parser, so two payloads that differ
	/// only in Unicode-escaping (e.g. <c>\u002B</c> vs. <c>+</c>) compare equal.
	/// </summary>
	/// <param name="json">The JSON payload to normalize.</param>
	/// <returns>The re-serialized, canonical form of the payload.</returns>
	private static string Normalize(string json)
	{
		return JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(json));
	}
}
