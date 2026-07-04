// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using OllamaProxy.Endpoints;

namespace OllamaProxy.Tests.Endpoints;

/// <summary>
/// Tests for <see cref="SystemEndpoints.MapSystemEndpoints"/>, the route group that exposes the Ollama-compatible
/// system metadata and liveness endpoints.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SystemEndpointsTests
{
	private const string JsonContentType = "application/json; charset=utf-8";
	private const string GetRouteMethod = "GET";
	private const string VersionRoute = "/api/version";
	private const string RunningModelsRoute = "/api/ps";
	private const string HealthRoute = "/health";
	private const string VersionRouteDescription = $"{VersionRoute}:{GetRouteMethod}";
	private const string RunningModelsRouteDescription = $"{RunningModelsRoute}:{GetRouteMethod}";
	private const string HealthRouteDescription = $"{HealthRoute}:{GetRouteMethod}";
	private const string VersionResponseBody = "{\"version\":\"" + SystemEndpoints.ReportedOllamaVersion + "\"}";
	private const string RunningModelsResponseBody = "{\"models\":[]}";
	private const string HealthResponseBody = "{\"status\":\"ok\"}";

	/// <summary>
	/// Verifies that <see cref="SystemEndpoints.MapSystemEndpoints"/> registers exactly the documented system routes
	/// as HTTP GET endpoints.
	/// </summary>
	[Fact]
	public async Task MapSystemEndpoints_WhenCalled_RegistersSystemGetRoutes()
	{
		// Arrange
		await using WebApplication app = CreateSystemEndpointApp();

		// Act
		string[] routes = GetRouteEndpoints(app)
			.Select(DescribeRoute)
			.OrderBy(description => description, StringComparer.Ordinal)
			.ToArray();

		// Assert
		Assert.Equal(
			[
				RunningModelsRouteDescription,
				VersionRouteDescription,
				HealthRouteDescription
			],
			routes);
	}

	/// <summary>
	/// Verifies that the mapped <c>/api/version</c> route returns the advertised Ollama-compatible version payload.
	/// </summary>
	[Fact]
	public async Task MapSystemEndpoints_WhenVersionRouteInvoked_ReturnsReportedVersion()
	{
		// Act
		EndpointResponse response = await InvokeGetAsync(VersionRoute);

		// Assert
		AssertResponse(response, StatusCodes.Status200OK, JsonContentType, VersionResponseBody);
	}

	/// <summary>
	/// Verifies that the mapped <c>/api/ps</c> route returns the empty running-model list expected for a proxy that
	/// never loads models locally.
	/// </summary>
	[Fact]
	public async Task MapSystemEndpoints_WhenRunningModelsRouteInvoked_ReturnsEmptyModelList()
	{
		// Act
		EndpointResponse response = await InvokeGetAsync(RunningModelsRoute);

		// Assert
		AssertResponse(response, StatusCodes.Status200OK, JsonContentType, RunningModelsResponseBody);
	}

	/// <summary>
	/// Verifies that the mapped <c>/health</c> route returns the minimal liveness payload used by external probes.
	/// </summary>
	[Fact]
	public async Task MapSystemEndpoints_WhenHealthRouteInvoked_ReturnsOkStatusPayload()
	{
		// Act
		EndpointResponse response = await InvokeGetAsync(HealthRoute);

		// Assert
		AssertResponse(response, StatusCodes.Status200OK, JsonContentType, HealthResponseBody);
	}

	/// <summary>
	/// Verifies that <see cref="SystemEndpoints.MapSystemEndpoints"/> rejects a <see langword="null"/> route builder.
	/// </summary>
	[Fact]
	public void MapSystemEndpoints_WhenEndpointBuilderIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() => SystemEndpoints.MapSystemEndpoints(null!));
		Assert.Equal("endpoints", exception.ParamName);
	}

	/// <summary>
	/// Creates an in-memory application whose endpoint table contains only the system routes under test.
	/// </summary>
	/// <returns>
	/// A web application with <see cref="SystemEndpoints.MapSystemEndpoints"/> applied.
	/// </returns>
	private static WebApplication CreateSystemEndpointApp()
	{
		WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
		WebApplication app = builder.Build();
		app.MapSystemEndpoints();
		return app;
	}

	/// <summary>
	/// Formats a route endpoint as a stable route-pattern and HTTP-method description for exact assertions.
	/// </summary>
	/// <param name="endpoint">
	/// The route endpoint to describe.
	/// </param>
	/// <returns>
	/// A stable route description in the form <c>route:method1,method2</c>.
	/// </returns>
	private static string DescribeRoute(RouteEndpoint endpoint)
	{
		string routePattern = endpoint.RoutePattern.RawText ?? string.Empty;
		IReadOnlyList<string> methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
		return $"{routePattern}:{string.Join(',', methods)}";
	}

	/// <summary>
	/// Reads the route endpoints from the application's route-builder data sources.
	/// </summary>
	/// <param name="app">
	/// The application whose locally mapped endpoints should be inspected.
	/// </param>
	/// <returns>
	/// The route endpoints registered on the application.
	/// </returns>
	private static IEnumerable<RouteEndpoint> GetRouteEndpoints(WebApplication app) => ((IEndpointRouteBuilder)app)
		.DataSources.SelectMany(dataSource => dataSource.Endpoints)
		.OfType<RouteEndpoint>();

	/// <summary>
	/// Invokes the mapped GET endpoint with the supplied route pattern and captures its complete HTTP response state.
	/// </summary>
	/// <param name="routePattern">
	/// The exact route pattern to invoke.
	/// </param>
	/// <returns>
	/// The status code, content type, and serialized body written by the selected endpoint.
	/// </returns>
	private static async Task<EndpointResponse> InvokeGetAsync(string routePattern)
	{
		WebApplication app = CreateSystemEndpointApp();

		try
		{
			RouteEndpoint endpoint = Assert.Single(
				GetRouteEndpoints(app),
				candidate => string.Equals(candidate.RoutePattern.RawText, routePattern, StringComparison.Ordinal));

			await using MemoryStream body = new();
			DefaultHttpContext context = new()
			{
				RequestServices = app.Services,
				Response = { Body = body }
			};

			await endpoint.RequestDelegate!(context).ConfigureAwait(false);

			body.Position = 0;
			using StreamReader reader = new(body);
			string responseBody = await reader.ReadToEndAsync().ConfigureAwait(false);

			return new EndpointResponse(context.Response.StatusCode, context.Response.ContentType, responseBody);
		}
		finally
		{
			await app.DisposeAsync().ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Verifies every observable field of an endpoint response.
	/// </summary>
	/// <param name="response">The response returned by the endpoint invocation.</param>
	/// <param name="expectedStatusCode">The exact expected HTTP status code.</param>
	/// <param name="expectedContentType">The exact expected response content type.</param>
	/// <param name="expectedBody">The exact expected serialized response body.</param>
	private static void AssertResponse(
		EndpointResponse response,
		int              expectedStatusCode,
		string           expectedContentType,
		string           expectedBody)
	{
		Assert.Equal(expectedStatusCode, response.StatusCode);
		Assert.Equal(expectedContentType, response.ContentType);
		Assert.Equal(expectedBody, response.Body);
	}

	/// <summary>
	/// Captures the complete response state asserted by the system endpoint tests.
	/// </summary>
	/// <param name="StatusCode">
	/// The HTTP status code written by the endpoint.
	/// </param>
	/// <param name="ContentType">
	/// The response content type written by the endpoint.
	/// </param>
	/// <param name="Body">
	/// The serialized response body written by the endpoint.
	/// </param>
	private sealed record EndpointResponse(int StatusCode, string? ContentType, string Body);
}
