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
/// Tests for <see cref="GenerateEndpoints"/>, the native Ollama <c>POST /api/generate</c> handler that
/// translates a single-prompt completion into a chat request, forwards it through the shared adapter,
/// and projects the chat chunks back into the generate shape (text in <c>response</c>).
/// <para>
/// The story escalates from validation through the two success shapes to upstream failure:
/// </para>
/// <list type="number">
///     <item>
///         <description>
///         Mapping: the route registers with its method (RegistersGenerateRoute); a null builder is rejected
///         (WhenEndpointBuilderIsNull).
///         </description>
///     </item>
///     <item>
///         <description>
///         Request validation: a missing body and blank model are rejected before routing
///         (WhenRequestIsNull/WhenModelBlank).
///         </description>
///     </item>
///     <item>
///         <description>
///         Routing: an unknown model yields a not-found error (WhenModelUnknown); an oversized context
///         window is rejected after routing (WhenContextWindowExceeded).
///         </description>
///     </item>
///     <item>
///         <description>
///         Success: an omitted stream flag streams NDJSON in the generate shape (WhenStreamOmitted); an
///         explicit false aggregates a single generate response (WhenStreamFalse). The system prompt is
///         wrapped into the chat request (WhenSystemPromptSupplied), and the completion tuning fields are
///         forwarded verbatim (WhenTuningFieldsSupplied).
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
public sealed class GenerateEndpointsTests
{
	private const string GenerateRoute = "/api/generate";

	// --- 1. Mapping ---

	/// <summary>
	/// Verifies that <see cref="GenerateEndpoints.MapGenerateEndpoints"/> registers the generate POST
	/// route.
	/// </summary>
	[Fact]
	public void MapGenerateEndpoints_WhenCalled_RegistersGenerateRoute()
	{
		// Arrange
		using WebApplication app = CreateApp(new FakeChatAdapter(), Model("alpha"));

		// Act
		RouteEndpoint endpoint = GetRouteEndpoint(app, GenerateRoute);
		IReadOnlyList<string> methods =
			endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];

		// Assert
		Assert.Equal([HttpMethods.Post], methods);
	}

	/// <summary>
	/// Verifies that <see cref="GenerateEndpoints.MapGenerateEndpoints"/> rejects a
	/// <see langword="null"/> route builder.
	/// </summary>
	[Fact]
	public void MapGenerateEndpoints_WhenEndpointBuilderIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() => GenerateEndpoints.MapGenerateEndpoints(null!));
		Assert.Equal("endpoints", exception.ParamName);
	}

	// --- 2. Request validation ---

	/// <summary>
	/// Verifies that <c>POST /api/generate</c> rejects an empty request body before any routing occurs.
	/// </summary>
	[Fact]
	public async Task Generate_WhenRequestIsNull_ReturnsBadRequest()
	{
		// Arrange
		await using WebApplication app = CreateApp(new FakeChatAdapter(), Model("alpha"));

		// Act
		EndpointResponse response = await InvokePostAsync(app, GenerateRoute, jsonBody: null);

		// Assert
		AssertOllamaError(response, StatusCodes.Status400BadRequest, "A model name is required.");
	}

	/// <summary>
	/// Verifies that <c>POST /api/generate</c> rejects a blank (whitespace-only) model name.
	/// </summary>
	[Fact]
	public async Task Generate_WhenModelBlank_ReturnsBadRequest()
	{
		// Arrange
		await using WebApplication app = CreateApp(new FakeChatAdapter(), Model("alpha"));

		// Act
		EndpointResponse response = await InvokePostAsync(app, GenerateRoute, """{ "model": "  ", "prompt": "hi" }""");

		// Assert
		AssertOllamaError(response, StatusCodes.Status400BadRequest, "A model name is required.");
	}

	// --- 3. Routing ---

	/// <summary>
	/// Verifies that <c>POST /api/generate</c> reports a not-found error for a model absent from the
	/// catalog.
	/// </summary>
	[Fact]
	public async Task Generate_WhenModelUnknown_ReturnsNotFound()
	{
		// Arrange
		await using WebApplication app = CreateApp(new FakeChatAdapter(), Model("alpha"));

		// Act
		EndpointResponse response =
			await InvokePostAsync(app, GenerateRoute, """{ "model": "ghost", "prompt": "hi" }""");

		// Assert
		AssertOllamaError(response, StatusCodes.Status404NotFound, "Model 'ghost' was not found.");
	}

	/// <summary>
	/// Verifies that <c>POST /api/generate</c> rejects a request whose <c>num_ctx</c> exceeds the model's
	/// enforced context-window limit. Unlike chat, generate validates the window only after routing, so
	/// the model must resolve first.
	/// </summary>
	[Fact]
	public async Task Generate_WhenContextWindowExceeded_ReturnsBadRequest()
	{
		// Arrange
		await using WebApplication app = CreateApp(new FakeChatAdapter(), Model("alpha", contextLength: 4096));
		const string body = """{ "model": "alpha", "prompt": "hi", "options": { "num_ctx": 8192 } }""";

		// Act
		EndpointResponse response = await InvokePostAsync(app, GenerateRoute, body);

		// Assert
		AssertOllamaError(
			response,
			StatusCodes.Status400BadRequest,
			"Requested context window of 8192 tokens exceeds the limit of 4096 tokens for model 'alpha'. " +
			"Reduce 'options.num_ctx' to 4096 or less.");
	}

	// --- 4. Success ---

	/// <summary>
	/// Verifies that an omitted <c>stream</c> flag defaults to streaming, writing each chunk as a line of
	/// generate-shaped, newline-delimited JSON (text under <c>response</c>).
	/// </summary>
	[Fact]
	public async Task Generate_WhenStreamOmitted_StreamsNdjsonChunks()
	{
		// Arrange
		OllamaChatResponse chunk1 = ChatChunk("upstream-model", "Hel", done: false);
		OllamaChatResponse chunk2 = ChatChunk("upstream-model", "lo", done: true);
		FakeChatAdapter adapter = new() { OnStreamChat = () => AsyncSequence(chunk1, chunk2) };
		await using WebApplication app = CreateApp(adapter, Model("alpha"));

		// Act
		EndpointResponse response =
			await InvokePostAsync(app, GenerateRoute, """{ "model": "alpha", "prompt": "hi" }""");

		// Assert
		Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
		Assert.Equal(NdjsonContentType, response.ContentType);
		string[] lines = response.Body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
		Assert.Equal(2, lines.Length);
		var first = Deserialize<OllamaGenerateResponse>(lines[0]);
		var second = Deserialize<OllamaGenerateResponse>(lines[1]);
		// The client-facing model name is echoed, not the upstream identifier the adapter received.
		Assert.Equal("alpha", first.Model);
		Assert.Equal("Hel", first.Response);
		Assert.False(first.Done);
		Assert.Equal("lo", second.Response);
		Assert.True(second.Done);
	}

	/// <summary>
	/// Verifies that an explicit <c>stream: false</c> aggregates the reply into a single generate
	/// response.
	/// </summary>
	[Fact]
	public async Task Generate_WhenStreamFalse_ReturnsAggregatedResponse()
	{
		// Arrange
		OllamaChatResponse aggregated = ChatChunk("upstream-model", "Hello world", done: true);
		FakeChatAdapter adapter = new() { OnCompleteChat = () => Task.FromResult(aggregated) };
		await using WebApplication app = CreateApp(adapter, Model("alpha"));
		const string body = """{ "model": "alpha", "prompt": "hi", "stream": false }""";

		// Act
		EndpointResponse response = await InvokePostAsync(app, GenerateRoute, body);

		// Assert
		Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
		Assert.Equal(JsonWithCharsetContentType, response.ContentType);
		var actual = Deserialize<OllamaGenerateResponse>(response.Body);
		Assert.Equal("alpha", actual.Model);
		Assert.Equal("Hello world", actual.Response);
		Assert.True(actual.Done);
	}

	/// <summary>
	/// Verifies that a supplied <c>system</c> prompt is wrapped into the chat request as a leading
	/// system message ahead of the user prompt.
	/// </summary>
	[Fact]
	public async Task Generate_WhenSystemPromptSupplied_WrapsSystemAndUserMessages()
	{
		// Arrange: capture the translated chat request the adapter receives.
		OllamaChatRequest? captured = null;
		FakeChatAdapter adapter = new()
		{
			OnCompleteChat = () => Task.FromResult(ChatChunk("upstream-model", "ok", done: true))
		};
		adapter.OnCaptureCompleteRequest = request => captured = request;
		await using WebApplication app = CreateApp(adapter, Model("alpha"));
		const string body =
			"""{ "model": "alpha", "prompt": "Hi there", "system": "Be terse", "stream": false }""";

		// Act
		EndpointResponse response = await InvokePostAsync(app, GenerateRoute, body);

		// Assert
		Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
		Assert.NotNull(captured);
		Assert.Equal(2, captured.Messages.Count);
		Assert.Equal("system", captured.Messages[0].Role);
		Assert.Equal("Be terse", captured.Messages[0].Content);
		Assert.Equal("user", captured.Messages[1].Role);
		Assert.Equal("Hi there", captured.Messages[1].Content);
		// The routing contract: the resolved backend and the rewritten upstream model reached the adapter.
		Assert.NotNull(adapter.LastCall);
		Assert.Equal("cloud", adapter.LastCall.BackendName);
		Assert.Equal("upstream-model", adapter.LastCall.UpstreamModel);
	}

	/// <summary>
	/// Verifies that the generate-to-chat translation forwards the completion tuning fields verbatim:
	/// <c>images</c> ride on the user message, and <c>format</c>, <c>options</c>, <c>think</c>,
	/// <c>keep_alive</c>, <c>logprobs</c>, and <c>top_logprobs</c> pass through unchanged. It also confirms
	/// that generate never synthesizes tool definitions (<c>Tools</c> stays <see langword="null"/>).
	/// </summary>
	[Fact]
	public async Task Generate_WhenTuningFieldsSupplied_ForwardsThemToChatRequest()
	{
		// Arrange: capture the translated chat request the adapter receives.
		OllamaChatRequest? captured = null;
		FakeChatAdapter adapter = new()
		{
			OnCompleteChat = () => Task.FromResult(ChatChunk("upstream-model", "ok", done: true))
		};
		adapter.OnCaptureCompleteRequest = request => captured = request;
		await using WebApplication app = CreateApp(adapter, Model("alpha"));
		// A full tuning payload: multimodal image, structured-output directive, sampling options, a
		// reasoning directive, a keep-alive hint, and log-probability flags.
		const string body =
			"""
			{
				"model": "alpha",
				"prompt": "Describe",
				"images": ["aW1n"],
				"format": "json",
				"options": { "temperature": 0.25, "num_predict": 128 },
				"think": "high",
				"keep_alive": "5m",
				"logprobs": true,
				"top_logprobs": 3,
				"stream": false
			}
			""";

		// Act
		EndpointResponse response = await InvokePostAsync(app, GenerateRoute, body);

		// Assert
		Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
		Assert.NotNull(captured);
		// Images ride on the single user message the prompt was wrapped into.
		Assert.Single(captured.Messages);
		Assert.Equal("user", captured.Messages[0].Role);
		Assert.Equal(["aW1n"], captured.Messages[0].Images);
		// The structured-output directive and reasoning directive pass through as their raw JSON nodes.
		Assert.Equal("json", captured.Format?.GetValue<string>());
		Assert.Equal("high", captured.Think?.GetValue<string>());
		// The sampling options are forwarded field-for-field.
		Assert.NotNull(captured.Options);
		Assert.Equal(0.25, captured.Options.Temperature);
		Assert.Equal(128, captured.Options.NumPredict);
		// The keep-alive hint and log-probability flags survive the translation.
		Assert.Equal("5m", captured.KeepAlive?.GetValue<string>());
		Assert.True(captured.Logprobs);
		Assert.Equal(3, captured.TopLogprobs);
		// Generate never fabricates tool definitions.
		Assert.Null(captured.Tools);
	}

	// --- 5. Upstream failure ---

	/// <summary>
	/// Verifies that a <see cref="ProviderException"/> raised mid-completion is mapped to its
	/// corresponding Ollama-shaped error (a backend 5xx normalizes to a <c>502 Bad Gateway</c>).
	/// </summary>
	[Fact]
	public async Task Generate_WhenProviderFails_ReturnsMappedError()
	{
		// Arrange: a backend 503 is not a client-correctable request error, so it normalizes to 502.
		FakeChatAdapter adapter = new()
		{
			OnCompleteChat = () =>
				throw new ProviderException(HttpStatusCode.ServiceUnavailable, "Upstream is unavailable.")
		};
		await using WebApplication app = CreateApp(adapter, Model("alpha"));
		const string body = """{ "model": "alpha", "prompt": "hi", "stream": false }""";

		// Act
		EndpointResponse response = await InvokePostAsync(app, GenerateRoute, body);

		// Assert
		AssertOllamaError(response, StatusCodes.Status502BadGateway, "Upstream is unavailable.");
	}

	/// <summary>
	/// Builds an in-memory application whose endpoint table holds only the generate route, backed by a
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
		app.MapGenerateEndpoints();
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
