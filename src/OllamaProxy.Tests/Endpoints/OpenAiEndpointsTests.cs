// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using OllamaProxy.Contracts.Ollama;
using OllamaProxy.Contracts.OpenAi;
using OllamaProxy.Core;
using OllamaProxy.Endpoints;
using OllamaProxy.Providers.Abstractions;
using OllamaProxy.Providers.OpenAiProtocol;

using static OllamaProxy.Tests.Endpoints.EndpointTestSupport;

// ReSharper disable ParameterOnlyUsedForPreconditionCheck.Local

namespace OllamaProxy.Tests.Endpoints;

/// <summary>
/// Tests for <see cref="OpenAiEndpoints"/>, the inbound OpenAI-compatible <c>/v1</c> surface that
/// forwards request bodies verbatim through <see cref="IOpenAiForwarder"/>, rewriting only the
/// <c>model</c> field between the client-facing name and the upstream identifier.
/// <para>
/// The story runs from the catalog read through validation gates to the two forwarding shapes and
/// upstream failure:
/// </para>
/// <list type="number">
///     <item>
///         <description>
///         Mapping: the four documented routes register (RegistersV1Routes).
///         </description>
///     </item>
///     <item>
///         <description>
///         Models: the catalog projects into the OpenAI list envelope (WhenModelsRegistered).
///         </description>
///     </item>
///     <item>
///         <description>
///         Body validation: a non-object body and a missing model are rejected in the OpenAI error shape
///         (WhenBodyNotJsonObject/WhenModelMissing).
///         </description>
///     </item>
///     <item>
///         <description>
///         Routing: an unknown model yields a not-found error (WhenModelUnknown); a backend that does not
///         speak the OpenAI protocol yields a bad-gateway error (WhenBackendNotOpenAi).
///         </description>
///     </item>
///     <item>
///         <description>
///         Forwarding: a non-streaming request aggregates JSON with the model rewritten back
///         (WhenStreamFalse); a streaming request relays SSE frames and appends <c>[DONE]</c>
///         (WhenStreamTrue). Each route forwards to its own upstream path — <c>/v1/completions</c> to
///         <c>completions</c> (ForwardsToCompletionsPath, and streaming via RelaysSseFramesAndDone) and
///         <c>/v1/embeddings</c> to <c>embeddings</c> (ForwardsToEmbeddingsPath) — and the embeddings
///         route ignores <c>stream: true</c> because it is mapped with streaming disabled
///         (IgnoresStreamAndReturnsJson).
///         </description>
///     </item>
///     <item>
///         <description>
///         Upstream failure: a provider failure is mapped to the OpenAI error envelope (WhenProviderFails).
///         </description>
///     </item>
/// </list>
/// </summary>
[Trait("Category", "Unit")]
public sealed class OpenAiEndpointsTests
{
	private const string ModelsRoute      = "/v1/models";
	private const string ChatRoute        = "/v1/chat/completions";
	private const string CompletionsRoute = "/v1/completions";
	private const string EmbeddingsRoute  = "/v1/embeddings";

	private const string ChatCompletionsPath = "chat/completions";
	private const string CompletionsPath     = "completions";
	private const string EmbeddingsPath      = "embeddings";

	private static readonly DateTimeOffset FixedNow = new(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);

	// --- 1. Mapping ---

	/// <summary>
	/// Verifies that <see cref="OpenAiEndpoints.MapOpenAiApi"/> registers the models, chat, completions,
	/// and embeddings routes.
	/// </summary>
	[Fact]
	public void MapOpenAiApi_WhenCalled_RegistersV1Routes()
	{
		// Arrange
		using WebApplication app = CreateApp(
			new FakeOpenAiForwarderAdapter(),
			Model("alpha"));

		// Act
		string[] routes = ((IEndpointRouteBuilder)app).DataSources
			.SelectMany(source => source.Endpoints)
			.OfType<RouteEndpoint>()
			.Select(DescribeRoute)
			.OrderBy(description => description, StringComparer.Ordinal)
			.ToArray();

		// Assert
		Assert.Equal(
			[
				$"{ChatRoute}:POST",
				$"{CompletionsRoute}:POST",
				$"{EmbeddingsRoute}:POST",
				$"{ModelsRoute}:GET"
			],
			routes);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiEndpoints.MapOpenAiApi"/> rejects a <see langword="null"/> route
	/// builder.
	/// </summary>
	[Fact]
	public void MapOpenAiApi_WhenEndpointBuilderIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() => OpenAiEndpoints.MapOpenAiApi(null!));
		Assert.Equal("endpoints", exception.ParamName);
	}

	// --- 2. Models ---

	/// <summary>
	/// Verifies that <c>GET /v1/models</c> projects every registered model into the OpenAI list envelope,
	/// stamping each entry with a consistent creation timestamp.
	/// </summary>
	[Fact]
	public async Task Models_WhenModelsRegistered_ReturnsOpenAiListEnvelope()
	{
		// Arrange
		await using WebApplication app = CreateApp(
			new FakeOpenAiForwarderAdapter(),
			Model("alpha", backendName: "cloud"),
			Model("beta", backendName: "local"));
		long created = FixedNow.ToUnixTimeSeconds();

		// Act
		EndpointResponse response = await InvokeGetAsync(app, ModelsRoute);

		// Assert
		Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
		var actual = Deserialize<OpenAiModelListResponse>(response.Body);
		Assert.Equal("list", actual.Object);
		Assert.Equal(2, actual.Data.Count);
		Assert.Equal(new OpenAiModelListEntry("alpha", created, "cloud"), actual.Data[0]);
		Assert.Equal(new OpenAiModelListEntry("beta", created, "local"), actual.Data[1]);
	}

	// --- 3. Body validation ---

	/// <summary>
	/// Verifies that a forwarding route rejects a body that is valid JSON but not an object.
	/// </summary>
	[Fact]
	public async Task ChatCompletions_WhenBodyNotJsonObject_ReturnsBadRequest()
	{
		// Arrange
		await using WebApplication app = CreateApp(
			new FakeOpenAiForwarderAdapter(),
			Model("alpha"));

		// Act: a JSON array is well-formed JSON but not the object the endpoint requires.
		EndpointResponse response = await InvokePostAsync(
			                            app,
			                            ChatRoute,
			                            "[1,2,3]");

		// Assert
		AssertOpenAiError(
			response,
			StatusCodes.Status400BadRequest,
			"The request body must be a JSON object.",
			"invalid_request_error");
	}

	/// <summary>
	/// Verifies that a forwarding route rejects a body that omits the model name.
	/// </summary>
	[Fact]
	public async Task ChatCompletions_WhenModelMissing_ReturnsBadRequest()
	{
		// Arrange
		await using WebApplication app = CreateApp(
			new FakeOpenAiForwarderAdapter(),
			Model("alpha"));

		// Act
		EndpointResponse response = await InvokePostAsync(
			                            app,
			                            ChatRoute,
			                            """{ "messages": [] }""");

		// Assert
		AssertOpenAiError(
			response,
			StatusCodes.Status400BadRequest,
			"A model name is required.",
			"invalid_request_error");
	}

	// --- 4. Routing ---

	/// <summary>
	/// Verifies that a forwarding route reports a not-found error for a model absent from the catalog.
	/// </summary>
	[Fact]
	public async Task ChatCompletions_WhenModelUnknown_ReturnsNotFound()
	{
		// Arrange
		await using WebApplication app = CreateApp(new FakeOpenAiForwarderAdapter(), Model("alpha"));

		// Act
		EndpointResponse response = await InvokePostAsync(
			                            app,
			                            ChatRoute,
			                            """{ "model": "ghost" }""");

		// Assert
		AssertOpenAiError(
			response,
			StatusCodes.Status404NotFound,
			"Model 'ghost' was not found.",
			"invalid_request_error");
	}

	/// <summary>
	/// Verifies that a forwarding route reports a bad-gateway error when the resolved backend does not
	/// implement the OpenAI forwarder capability.
	/// </summary>
	[Fact]
	public async Task ChatCompletions_WhenBackendNotOpenAi_ReturnsBadGateway()
	{
		// Arrange: FakeChatAdapter deliberately does not implement IOpenAiForwarder.
		await using WebApplication app = CreateApp(new FakeChatAdapter(), Model("alpha", backendName: "cloud"));

		// Act
		EndpointResponse response = await InvokePostAsync(
			                            app,
			                            ChatRoute,
			                            """{ "model": "alpha" }""");

		// Assert
		AssertOpenAiError(
			response,
			StatusCodes.Status502BadGateway,
			"Backend 'cloud' does not support the OpenAI protocol.",
			"api_error");
	}

	// --- 5. Forwarding ---

	/// <summary>
	/// Verifies that a non-streaming request aggregates the upstream JSON and rewrites the <c>model</c>
	/// field back to the client-facing name.
	/// </summary>
	[Fact]
	public async Task ChatCompletions_WhenStreamFalse_ReturnsAggregatedJson()
	{
		// Arrange: the upstream echoes the rewritten (upstream) model; the endpoint must restore "alpha".
		FakeOpenAiForwarderAdapter adapter = new()
		{
			OnForwardJson = body =>
			{
				Assert.Equal("upstream-model", (string?)body["model"]);
				JsonObject upstream = new()
				{
					["model"] = "upstream-model",
					["id"] = "resp-1"
				};
				return Task.FromResult(upstream);
			}
		};
		await using WebApplication app = CreateApp(adapter, Model("alpha"));

		// Act
		EndpointResponse response = await InvokePostAsync(
			                            app,
			                            ChatRoute,
			                            """{ "model": "alpha" }""");

		// Assert
		Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
		Assert.Equal(JsonContentType, response.ContentType);
		JsonObject actual = ParseObject(response.Body);
		Assert.Equal("alpha", (string?)actual["model"]);
		Assert.Equal("resp-1", (string?)actual["id"]);
		// The forwarder received the chat path, the resolved backend, and the body with "model" rewritten
		// to the upstream identifier — the routing contract, not just the projected response.
		Assert.NotNull(adapter.LastCall);
		Assert.Equal("cloud", adapter.LastCall.BackendName);
		Assert.Equal(ChatCompletionsPath, adapter.LastCall.UpstreamPath);
		Assert.Equal("upstream-model", (string?)adapter.LastCall.Body["model"]);
	}

	/// <summary>
	/// Verifies that a streaming request relays each upstream SSE frame with the <c>model</c> field
	/// rewritten and appends the terminating <c>[DONE]</c> sentinel.
	/// </summary>
	[Fact]
	public async Task ChatCompletions_WhenStreamTrue_RelaysSseFramesAndDone()
	{
		// Arrange
		FakeOpenAiForwarderAdapter adapter = new()
		{
			OnForwardSse = () => AsyncSequence(
				"""{ "model": "upstream-model", "choices": [{ "delta": { "content": "Hi" } }] }""",
				"""{ "model": "upstream-model", "choices": [{ "delta": { "content": "!" } }] }""")
		};
		await using WebApplication app = CreateApp(adapter, Model("alpha"));

		// Act
		EndpointResponse response = await InvokePostAsync(
			                            app,
			                            ChatRoute,
			                            """{ "model": "alpha", "stream": true }""");

		// Assert
		Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
		Assert.Equal(EventStreamContentType, response.ContentType);
		string[] frames = response.Body
			.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
			.Select(frame => frame.StartsWith("data: ", StringComparison.Ordinal)
				                 ? frame["data: ".Length..]
				                 : frame)
			.ToArray();
		Assert.Equal(3, frames.Length);
		// The model field is rewritten back to the client-facing name on every relayed frame.
		Assert.Equal("alpha", (string?)ParseObject(frames[0])["model"]);
		Assert.Equal("alpha", (string?)ParseObject(frames[1])["model"]);
		Assert.Equal("[DONE]", frames[2]);
	}

	/// <summary>
	/// Verifies that <c>POST /v1/completions</c> forwards to the backend-relative <c>completions</c> path
	/// (not the chat path), rewriting the <c>model</c> field to the upstream identifier.
	/// </summary>
	[Fact]
	public async Task Completions_WhenStreamFalse_ForwardsToCompletionsPath()
	{
		// Arrange
		FakeOpenAiForwarderAdapter adapter = new()
		{
			OnForwardJson = _ => Task.FromResult(
				new JsonObject
				{
					["model"] = "upstream-model",
					["id"] = "cmpl-1"
				})
		};
		await using WebApplication app = CreateApp(adapter, Model("alpha"));

		// Act
		EndpointResponse response = await InvokePostAsync(
			                            app,
			                            CompletionsRoute,
			                            """{ "model": "alpha" }""");

		// Assert
		Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
		Assert.Equal("alpha", (string?)ParseObject(response.Body)["model"]);
		// The distinguishing contract: /v1/completions maps to the "completions" upstream path.
		Assert.NotNull(adapter.LastCall);
		Assert.Equal(CompletionsPath, adapter.LastCall.UpstreamPath);
		Assert.Equal("upstream-model", (string?)adapter.LastCall.Body["model"]);
	}

	/// <summary>
	/// Verifies that a streaming <c>POST /v1/completions</c> request relays each upstream SSE frame with
	/// the <c>model</c> field rewritten to the client-facing name, appends the terminating <c>[DONE]</c>
	/// sentinel, and forwards to the backend-relative <c>completions</c> path.
	/// </summary>
	[Fact]
	public async Task Completions_WhenStreamTrue_RelaysSseFramesAndDone()
	{
		// Arrange
		FakeOpenAiForwarderAdapter adapter = new()
		{
			OnForwardSse = () => AsyncSequence(
				"""{ "model": "upstream-model", "choices": [{ "text": "Hi" }] }""",
				"""{ "model": "upstream-model", "choices": [{ "text": "!" }] }""")
		};
		await using WebApplication app = CreateApp(adapter, Model("alpha"));

		// Act
		EndpointResponse response = await InvokePostAsync(
			                            app,
			                            CompletionsRoute,
			                            """{ "model": "alpha", "stream": true }""");

		// Assert
		Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
		Assert.Equal(EventStreamContentType, response.ContentType);
		string[] frames = response.Body
			.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
			.Select(frame => frame.StartsWith("data: ", StringComparison.Ordinal)
				                 ? frame["data: ".Length..]
				                 : frame)
			.ToArray();
		Assert.Equal(3, frames.Length);
		// The model field is rewritten back to the client-facing name on every relayed frame.
		Assert.Equal("alpha", (string?)ParseObject(frames[0])["model"]);
		Assert.Equal("alpha", (string?)ParseObject(frames[1])["model"]);
		Assert.Equal("[DONE]", frames[2]);
		// The distinguishing contract: /v1/completions streams over the "completions" upstream path.
		Assert.NotNull(adapter.LastCall);
		Assert.Equal(CompletionsPath, adapter.LastCall.UpstreamPath);
	}

	/// <summary>
	/// Verifies that <c>POST /v1/embeddings</c> forwards to the backend-relative <c>embeddings</c> path,
	/// rewriting the <c>model</c> field to the upstream identifier.
	/// </summary>
	[Fact]
	public async Task Embeddings_WhenModelResolved_ForwardsToEmbeddingsPath()
	{
		// Arrange
		FakeOpenAiForwarderAdapter adapter = new()
		{
			OnForwardJson = _ => Task.FromResult(
				new JsonObject
				{
					["model"] = "upstream-model",
					["data"] = new JsonArray()
				})
		};
		await using WebApplication app = CreateApp(adapter, Model("alpha"));

		// Act
		EndpointResponse response = await InvokePostAsync(
			                            app,
			                            EmbeddingsRoute,
			                            """{ "model": "alpha", "input": "hi" }""");

		// Assert
		Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
		Assert.Equal("alpha", (string?)ParseObject(response.Body)["model"]);
		Assert.NotNull(adapter.LastCall);
		Assert.Equal(EmbeddingsPath, adapter.LastCall.UpstreamPath);
		Assert.Equal("upstream-model", (string?)adapter.LastCall.Body["model"]);
	}

	/// <summary>
	/// Verifies that <c>POST /v1/embeddings</c> ignores a client <c>stream: true</c> flag and still
	/// aggregates a single JSON response, because the embeddings route is mapped with streaming disabled.
	/// </summary>
	[Fact]
	public async Task Embeddings_WhenStreamTrue_IgnoresStreamAndReturnsJson()
	{
		// Arrange: the SSE path must never be taken for embeddings, so leave OnForwardSse unset — invoking
		// it would throw NotSupportedException and fail the test, proving the non-streaming path was used.
		FakeOpenAiForwarderAdapter adapter = new()
		{
			OnForwardJson = _ => Task.FromResult(
				new JsonObject
				{
					["model"] = "upstream-model"
				})
		};
		await using WebApplication app = CreateApp(adapter, Model("alpha"));

		// Act: the client asks to stream, but /v1/embeddings was mapped with allowStreaming: false.
		EndpointResponse response = await InvokePostAsync(
			                            app,
			                            EmbeddingsRoute,
			                            """{ "model": "alpha", "input": "hi", "stream": true }""");

		// Assert: aggregated JSON, not an SSE stream.
		Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
		Assert.Equal(JsonContentType, response.ContentType);
		Assert.Equal("alpha", (string?)ParseObject(response.Body)["model"]);
	}

	// --- 6. Upstream failure ---

	/// <summary>
	/// Verifies that a <see cref="ProviderException"/> raised during forwarding is mapped to the OpenAI
	/// error envelope (a backend 5xx normalizes to a <c>502 Bad Gateway</c>).
	/// </summary>
	[Fact]
	public async Task ChatCompletions_WhenProviderFails_ReturnsMappedError()
	{
		// Arrange: a backend 503 is not a client-correctable request error, so it normalizes to 502.
		FakeOpenAiForwarderAdapter adapter = new()
		{
			OnForwardJson = _ => throw new ProviderException(
				                     HttpStatusCode.ServiceUnavailable,
				                     "Upstream is unavailable.")
		};
		await using WebApplication app = CreateApp(adapter, Model("alpha"));

		// Act
		EndpointResponse response = await InvokePostAsync(
			                            app,
			                            ChatRoute,
			                            """{ "model": "alpha" }""");

		// Assert
		AssertOpenAiError(
			response,
			StatusCodes.Status502BadGateway,
			"Upstream is unavailable.",
			"api_error");
	}

	/// <summary>
	/// Builds an in-memory application whose endpoint table holds the OpenAI routes, backed by a router
	/// over <paramref name="models"/>, a resolver returning <paramref name="adapter"/>, and a fixed clock.
	/// </summary>
	/// <param name="adapter">The provider adapter every backend resolves to.</param>
	/// <param name="models">The models the router exposes and resolves.</param>
	/// <returns>The configured web application.</returns>
	private static WebApplication CreateApp(IProviderAdapter adapter, params RegisteredModel[] models)
	{
		WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
		builder.Services.AddSingleton<IModelRouter>(new FakeModelRouter(models));
		builder.Services.AddSingleton<IProviderResolver>(new FakeProviderResolver(adapter));
		builder.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(FixedNow));

		WebApplication app = builder.Build();
		app.MapOpenAiApi();
		return app;
	}

	/// <summary>
	/// Produces an async sequence over the supplied SSE payloads, mimicking an upstream stream.
	/// </summary>
	/// <param name="payloads">The raw JSON payloads to yield in order.</param>
	/// <returns>The asynchronous payload sequence.</returns>
	private static async IAsyncEnumerable<string> AsyncSequence(params string[] payloads)
	{
		foreach (string payload in payloads)
		{
			await Task.Yield();
			yield return payload;
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
	/// Parses a response body into a <see cref="JsonObject"/>, asserting it is a JSON object.
	/// </summary>
	/// <param name="body">The JSON body to parse.</param>
	/// <returns>The parsed object.</returns>
	private static JsonObject ParseObject(string body)
	{
		var node = JsonNode.Parse(body) as JsonObject;
		Assert.NotNull(node);
		return node;
	}

	/// <summary>
	/// Asserts that a response is the OpenAI-shaped error envelope with the expected status, message, and
	/// type discriminator.
	/// </summary>
	/// <param name="response">The response returned by the endpoint invocation.</param>
	/// <param name="expectedStatusCode">The exact expected HTTP status code.</param>
	/// <param name="expectedMessage">The exact expected error message.</param>
	/// <param name="expectedType">The exact expected OpenAI error type discriminator.</param>
	private static void AssertOpenAiError(
		EndpointResponse response,
		int              expectedStatusCode,
		string           expectedMessage,
		string           expectedType)
	{
		Assert.Equal(expectedStatusCode, response.StatusCode);
		JsonObject envelope = ParseObject(response.Body);
		var error = envelope["error"] as JsonObject;
		Assert.NotNull(error);
		Assert.Equal(expectedMessage, (string?)error["message"]);
		Assert.Equal(expectedType, (string?)error["type"]);
	}
}
