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
/// Tests for <see cref="ChatEndpoints"/>, the native Ollama <c>POST /api/chat</c> handler that routes
/// a model, then streams NDJSON chunks or writes a single aggregated response.
/// <para>
/// The story escalates from validation gates through the two success shapes to upstream failure:
/// </para>
/// <list type="number">
///     <item>
///         <description>
///         Mapping: the route registers with its method (RegistersChatRoute); a null builder is rejected
///         (WhenEndpointBuilderIsNull).
///         </description>
///     </item>
///     <item>
///         <description>
///         Request validation: a missing body, blank model, and oversized context window are each rejected
///         before any upstream call (WhenRequestIsNull/WhenModelBlank/WhenContextWindowExceeded).
///         </description>
///     </item>
///     <item>
///         <description>
///         Routing: an unknown model yields a not-found error (WhenModelUnknown).
///         </description>
///     </item>
///     <item>
///         <description>
///         Success: an omitted stream flag defaults to streaming NDJSON (WhenStreamOmitted); an explicit
///         false aggregates a single JSON response (WhenStreamFalse).
///         </description>
///     </item>
///     <item>
///         <description>
///         Upstream failure: a provider failure is mapped to its Ollama-shaped error (WhenProviderFails).
///         </description>
///     </item>
/// </list>
/// </summary>
[Trait("Category", "Unit")]
public sealed class ChatEndpointsTests
{
	private const string ChatRoute = "/api/chat";

	// --- 1. Mapping ---

	/// <summary>
	/// Verifies that <see cref="ChatEndpoints.MapChatEndpoints"/> registers the chat POST route.
	/// </summary>
	[Fact]
	public void MapChatEndpoints_WhenCalled_RegistersChatRoute()
	{
		// Arrange
		using WebApplication app = CreateApp(new FakeChatAdapter(), Model("alpha"));

		// Act
		RouteEndpoint endpoint = GetRouteEndpoint(app, ChatRoute);
		IReadOnlyList<string> methods =
			endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];

		// Assert
		Assert.Equal([HttpMethods.Post], methods);
	}

	/// <summary>
	/// Verifies that <see cref="ChatEndpoints.MapChatEndpoints"/> rejects a <see langword="null"/> route
	/// builder.
	/// </summary>
	[Fact]
	public void MapChatEndpoints_WhenEndpointBuilderIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() => ChatEndpoints.MapChatEndpoints(null!));
		Assert.Equal("endpoints", exception.ParamName);
	}

	// --- 2. Request validation ---

	/// <summary>
	/// Verifies that <c>POST /api/chat</c> rejects an empty request body before any routing occurs.
	/// </summary>
	[Fact]
	public async Task Chat_WhenRequestIsNull_ReturnsBadRequest()
	{
		// Arrange
		await using WebApplication app = CreateApp(new FakeChatAdapter(), Model("alpha"));

		// Act
		EndpointResponse response = await InvokePostAsync(app, ChatRoute, jsonBody: null);

		// Assert
		AssertOllamaError(response, StatusCodes.Status400BadRequest, "A model name is required.");
	}

	/// <summary>
	/// Verifies that <c>POST /api/chat</c> rejects a blank (whitespace-only) model name.
	/// </summary>
	[Fact]
	public async Task Chat_WhenModelBlank_ReturnsBadRequest()
	{
		// Arrange
		await using WebApplication app = CreateApp(new FakeChatAdapter(), Model("alpha"));

		// Act
		EndpointResponse response = await InvokePostAsync(app, ChatRoute, """{ "model": "  ", "messages": [] }""");

		// Assert
		AssertOllamaError(response, StatusCodes.Status400BadRequest, "A model name is required.");
	}

	/// <summary>
	/// Verifies that <c>POST /api/chat</c> rejects a request whose <c>num_ctx</c> exceeds the model's
	/// enforced context-window limit.
	/// </summary>
	[Fact]
	public async Task Chat_WhenContextWindowExceeded_ReturnsBadRequest()
	{
		// Arrange
		await using WebApplication app = CreateApp(new FakeChatAdapter(), Model("alpha", contextLength: 4096));
		const string body = """{ "model": "alpha", "messages": [], "options": { "num_ctx": 8192 } }""";

		// Act
		EndpointResponse response = await InvokePostAsync(app, ChatRoute, body);

		// Assert
		Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
		var error = Deserialize<OllamaErrorResponse>(response.Body);
		Assert.Equal(
			"Requested context window of 8192 tokens exceeds the limit of 4096 tokens for model 'alpha'. " +
			"Reduce 'options.num_ctx' to 4096 or less.",
			error.Error);
	}

	// --- 3. Routing ---

	/// <summary>
	/// Verifies that <c>POST /api/chat</c> reports a not-found error for a model absent from the catalog.
	/// </summary>
	[Fact]
	public async Task Chat_WhenModelUnknown_ReturnsNotFound()
	{
		// Arrange
		await using WebApplication app = CreateApp(new FakeChatAdapter(), Model("alpha"));

		// Act
		EndpointResponse response = await InvokePostAsync(app, ChatRoute, """{ "model": "ghost", "messages": [] }""");

		// Assert
		AssertOllamaError(response, StatusCodes.Status404NotFound, "Model 'ghost' was not found.");
	}

	// --- 4. Success ---

	/// <summary>
	/// Verifies that an omitted <c>stream</c> flag defaults to streaming, writing each chunk as a line of
	/// newline-delimited JSON.
	/// </summary>
	[Fact]
	public async Task Chat_WhenStreamOmitted_StreamsNdjsonChunks()
	{
		// Arrange
		OllamaChatResponse chunk1 = ChatChunk("alpha", "Hel", done: false);
		OllamaChatResponse chunk2 = ChatChunk("alpha", "lo", done: true);
		FakeChatAdapter adapter = new() { OnStreamChat = () => AsyncSequence(chunk1, chunk2) };
		await using WebApplication app = CreateApp(adapter, Model("alpha"));

		// Act
		EndpointResponse response = await InvokePostAsync(app, ChatRoute, """{ "model": "alpha", "messages": [] }""");

		// Assert
		Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
		Assert.Equal(NdjsonContentType, response.ContentType);
		string[] lines = response.Body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
		Assert.Equal(2, lines.Length);
		Assert.Equal("Hel", Deserialize<OllamaChatResponse>(lines[0]).Message.Content);
		Assert.False(Deserialize<OllamaChatResponse>(lines[0]).Done);
		Assert.Equal("lo", Deserialize<OllamaChatResponse>(lines[1]).Message.Content);
		Assert.True(Deserialize<OllamaChatResponse>(lines[1]).Done);
	}

	/// <summary>
	/// Verifies that an explicit <c>stream: false</c> aggregates the upstream reply into a single JSON
	/// response rather than a stream.
	/// </summary>
	[Fact]
	public async Task Chat_WhenStreamFalse_ReturnsAggregatedResponse()
	{
		// Arrange
		OllamaChatResponse aggregated = ChatChunk("alpha", "Hello world", done: true);
		FakeChatAdapter adapter = new() { OnCompleteChat = () => Task.FromResult(aggregated) };
		await using WebApplication app = CreateApp(adapter, Model("alpha"));
		const string body = """{ "model": "alpha", "messages": [], "stream": false }""";

		// Act
		EndpointResponse response = await InvokePostAsync(app, ChatRoute, body);

		// Assert
		Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
		Assert.Equal(JsonWithCharsetContentType, response.ContentType);
		var actual = Deserialize<OllamaChatResponse>(response.Body);
		Assert.Equal("Hello world", actual.Message.Content);
		Assert.True(actual.Done);
	}

	// --- 5. Upstream failure ---

	/// <summary>
	/// Verifies that a <see cref="ProviderException"/> raised mid-completion is mapped to its
	/// corresponding Ollama-shaped error (a backend 5xx normalizes to a <c>502 Bad Gateway</c> because,
	/// from the client's perspective, the upstream is at fault).
	/// </summary>
	[Fact]
	public async Task Chat_WhenProviderFails_ReturnsMappedError()
	{
		// Arrange: a backend 503 is not a client-correctable request error, so it normalizes to 502.
		FakeChatAdapter adapter = new()
		{
			OnCompleteChat = () =>
				throw new ProviderException(HttpStatusCode.ServiceUnavailable, "Upstream is unavailable.")
		};
		await using WebApplication app = CreateApp(adapter, Model("alpha"));
		const string body = """{ "model": "alpha", "messages": [], "stream": false }""";

		// Act
		EndpointResponse response = await InvokePostAsync(app, ChatRoute, body);

		// Assert
		AssertOllamaError(response, StatusCodes.Status502BadGateway, "Upstream is unavailable.");
	}

	/// <summary>
	/// Builds an in-memory application whose endpoint table holds only the chat route, backed by a router
	/// over <paramref name="models"/> and a resolver returning <paramref name="adapter"/>.
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
		app.MapChatEndpoints();
		return app;
	}

	/// <summary>
	/// Builds a chat response chunk carrying a single assistant text fragment.
	/// </summary>
	/// <param name="model">The model name echoed on the chunk.</param>
	/// <param name="content">The assistant text fragment for this chunk.</param>
	/// <param name="done">Whether this is the terminal chunk.</param>
	/// <returns>The chat response chunk.</returns>
	private static OllamaChatResponse ChatChunk(string model, string content, bool done) => new(
		model,
		"2026-07-10T12:00:00Z",
		new OllamaChatMessage("assistant", content),
		done);

	/// <summary>
	/// Produces an async sequence over the supplied chunks, mimicking a provider's streamed response.
	/// </summary>
	/// <param name="chunks">The chunks to yield in order.</param>
	/// <returns>The asynchronous chunk sequence.</returns>
	private static async IAsyncEnumerable<OllamaChatResponse> AsyncSequence(params OllamaChatResponse[] chunks)
	{
		foreach (OllamaChatResponse chunk in chunks)
		{
			await Task.Yield();
			yield return chunk;
		}
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
