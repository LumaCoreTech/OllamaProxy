// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

using OllamaProxy.Configuration;
using OllamaProxy.Contracts.Ollama;
using OllamaProxy.Core;
using OllamaProxy.Providers.Abstractions;
using OllamaProxy.Providers.OpenAiProtocol;

namespace OllamaProxy.Tests.Endpoints;

/// <summary>
/// Shared harness and test doubles for the request-endpoint handler tests. The five endpoint classes
/// expose their handlers only as <see langword="private"/> minimal-API delegates, so they are exercised
/// through the mapped <see cref="RouteEndpoint.RequestDelegate"/> against an in-memory
/// <see cref="WebApplication.CreateSlimBuilder()"/> application, mirroring
/// <see cref="SystemEndpointsTests"/>. This file centralizes the invocation plumbing (locate the route,
/// drive a <see cref="DefaultHttpContext"/>, capture the response) and the configurable
/// <see cref="IModelRouter"/> / <see cref="IProviderResolver"/> / <see cref="IProviderAdapter"/> doubles
/// the handlers depend on, so the per-endpoint test files stay focused on scenarios.
/// </summary>
static class EndpointTestSupport
{
	/// <summary>
	/// The content type minimal-API JSON responses report (via <c>WriteAsJsonAsync</c>).
	/// </summary>
	internal const string JsonWithCharsetContentType = "application/json; charset=utf-8";

	/// <summary>
	/// The bare content type the OpenAI forwarder writes for aggregated JSON responses.
	/// </summary>
	internal const string JsonContentType = "application/json";

	/// <summary>
	/// The content type of an Ollama newline-delimited JSON stream.
	/// </summary>
	internal const string NdjsonContentType = "application/x-ndjson";

	/// <summary>
	/// The content type of an OpenAI Server-Sent-Events stream.
	/// </summary>
	internal const string EventStreamContentType = "text/event-stream";

	/// <summary>
	/// Builds a <see cref="RegisteredModel"/> for tests, defaulting every field a handler does not care
	/// about so callers specify only the values under test.
	/// </summary>
	/// <param name="name">The client-facing model name.</param>
	/// <param name="backendName">The logical backend serving the model.</param>
	/// <param name="upstreamModel">The upstream model identifier the adapter receives.</param>
	/// <param name="contextLength">The enforced context-window limit in tokens.</param>
	/// <param name="reasoningEffort">The pinned reasoning effort, or <see langword="null"/> when none.</param>
	/// <returns>A fully constructed registered model.</returns>
	internal static RegisteredModel Model(
		string           name            = "gpt-test",
		string           backendName     = "cloud",
		string           upstreamModel   = "upstream-model",
		long             contextLength   = 8192,
		ReasoningEffort? reasoningEffort = null)
	{
		return new RegisteredModel(
			name,
			backendName,
			upstreamModel,
			ModelCapabilities.CompletionOnly,
			contextLength,
			reasoningEffort);
	}

	/// <summary>
	/// Invokes the mapped POST endpoint carrying <paramref name="jsonBody"/> and captures the complete
	/// response. A <see langword="null"/> body sends an empty request (no content type), driving the
	/// handler's null-request branch.
	/// </summary>
	/// <param name="app">The in-memory application whose route is invoked.</param>
	/// <param name="route">The exact route pattern to invoke.</param>
	/// <param name="jsonBody">The JSON request body, or <see langword="null"/> for an empty body.</param>
	/// <returns>The captured status code, content type, and response body.</returns>
	internal static Task<EndpointResponse> InvokePostAsync(WebApplication app, string route, string? jsonBody) =>
		InvokeAsync(app, HttpMethods.Post, route, jsonBody);

	/// <summary>
	/// Invokes the mapped GET endpoint and captures the complete response.
	/// </summary>
	/// <param name="app">The in-memory application whose route is invoked.</param>
	/// <param name="route">The exact route pattern to invoke.</param>
	/// <returns>The captured status code, content type, and response body.</returns>
	internal static Task<EndpointResponse> InvokeGetAsync(WebApplication app, string route) =>
		InvokeAsync(app, HttpMethods.Get, route, jsonBody: null);

	/// <summary>
	/// Drives the endpoint's request delegate against a <see cref="DefaultHttpContext"/> and reads back
	/// the response state, so the handler runs through the real minimal-API parameter binding and
	/// response-writing path without a network stack.
	/// </summary>
	/// <param name="app">The in-memory application whose route is invoked.</param>
	/// <param name="method">The HTTP method to set on the request.</param>
	/// <param name="route">The exact route pattern to invoke.</param>
	/// <param name="jsonBody">The JSON request body, or <see langword="null"/> for an empty body.</param>
	/// <returns>The captured status code, content type, and response body.</returns>
	private static async Task<EndpointResponse> InvokeAsync(
		WebApplication app,
		string         method,
		string         route,
		string?        jsonBody)
	{
		RouteEndpoint endpoint = GetRouteEndpoint(app, route);

		await using MemoryStream responseBody = new();
		DefaultHttpContext context = new()
		{
			RequestServices = app.Services,
			Response = { Body = responseBody }
		};

		context.Request.Method = method;

		if (jsonBody is not null)
		{
			byte[] bytes = Encoding.UTF8.GetBytes(jsonBody);
			context.Request.Body = new MemoryStream(bytes);
			context.Request.ContentLength = bytes.Length;
			context.Request.ContentType = JsonContentType;

			// Minimal-API JSON body binding consults the body-detection feature before reading; the bare
			// DefaultHttpContext omits it, so ReadFromJsonAsync() would otherwise treat the body as absent
			// and bind the parameter to null (surfacing as a spurious 400).
			context.Features.Set<IHttpRequestBodyDetectionFeature>(new RequestBodyDetected());
		}

		await endpoint.RequestDelegate!(context).ConfigureAwait(false);

		responseBody.Position = 0;
		using StreamReader reader = new(responseBody);
		string body = await reader.ReadToEndAsync().ConfigureAwait(false);

		return new EndpointResponse(context.Response.StatusCode, context.Response.ContentType, body);
	}

	/// <summary>
	/// Finds the single mapped route endpoint whose pattern equals <paramref name="route"/>.
	/// </summary>
	/// <param name="app">The application whose endpoint table is inspected.</param>
	/// <param name="route">The exact route pattern to locate.</param>
	/// <returns>The matching route endpoint.</returns>
	internal static RouteEndpoint GetRouteEndpoint(WebApplication app, string route)
	{
		return Assert.Single(
			((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(),
			candidate => string.Equals(candidate.RoutePattern.RawText, route, StringComparison.Ordinal));
	}

	/// <summary>
	/// Formats a route endpoint as a stable <c>route:methods</c> description for exact assertions, so a
	/// mapping test pins both the pattern and its HTTP verb (a wrong verb no longer slips through).
	/// </summary>
	/// <param name="endpoint">The route endpoint to describe.</param>
	/// <returns>The stable route description.</returns>
	internal static string DescribeRoute(RouteEndpoint endpoint)
	{
		string pattern = endpoint.RoutePattern.RawText ?? string.Empty;
		IReadOnlyList<string> methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
		return $"{pattern}:{string.Join(',', methods)}";
	}

	/// <summary>
	/// Captures the complete, observable response state produced by an endpoint invocation.
	/// </summary>
	/// <param name="StatusCode">The HTTP status code written by the endpoint.</param>
	/// <param name="ContentType">The response content type written by the endpoint.</param>
	/// <param name="Body">The serialized response body written by the endpoint.</param>
	internal sealed record EndpointResponse(int StatusCode, string? ContentType, string Body);
}

/// <summary>
/// Captures the full call context a handler passes to a chat/embeddings adapter, so a test asserts the
/// routing contract (which backend, which upstream model, which pinned reasoning effort, and the
/// translated request body) rather than only the response projection.
/// </summary>
/// <param name="BackendName">The name of the backend the handler resolved and dispatched to.</param>
/// <param name="UpstreamModel">The upstream model identifier the handler forwarded.</param>
/// <param name="PinnedEffort">The pinned reasoning effort passed through, or <see langword="null"/> when none.</param>
/// <param name="ChatRequest">The chat request the handler built, or <see langword="null"/> for an embeddings call.</param>
/// <param name="EmbedRequest">The embeddings request the handler built, or <see langword="null"/> for a chat call.</param>
sealed record CapturedChatCall(
	string              BackendName,
	string              UpstreamModel,
	ReasoningEffort?    PinnedEffort,
	OllamaChatRequest?  ChatRequest,
	OllamaEmbedRequest? EmbedRequest);

/// <summary>
/// Captures the full call context a handler passes to the OpenAI forwarder, so a test asserts the
/// forwarding contract (which backend, which upstream path, the pinned reasoning effort, and the
/// verbatim body with its rewritten <c>model</c> field) rather than only the relayed response.
/// </summary>
/// <param name="BackendName">The name of the backend the handler resolved and dispatched to.</param>
/// <param name="UpstreamPath">The backend-relative path the handler forwarded to (for example <c>completions</c>).</param>
/// <param name="PinnedEffort">The pinned reasoning effort passed through, or <see langword="null"/> when none.</param>
/// <param name="Body">The forwarded request body, with its <c>model</c> field already rewritten.</param>
sealed record CapturedOpenAiForwardCall(
	string           BackendName,
	string           UpstreamPath,
	ReasoningEffort? PinnedEffort,
	JsonObject       Body);

/// <summary>
/// A minimal <see cref="IHttpRequestBodyDetectionFeature"/> reporting that the request can carry a
/// body, so minimal-API JSON binding reads the supplied stream instead of short-circuiting to a null
/// body (which would surface as a spurious <c>400</c>).
/// </summary>
sealed class RequestBodyDetected : IHttpRequestBodyDetectionFeature
{
	/// <inheritdoc/>
	public bool CanHaveBody => true;
}

/// <summary>
/// A configurable <see cref="IModelRouter"/> double. It resolves a fixed set of models by exact
/// (ordinal, case-insensitive) name and returns that same set from <see cref="GetModels"/>, so a test
/// controls exactly which names route and which fall through to the not-found branch.
/// </summary>
sealed class FakeModelRouter : IModelRouter
{
	private readonly IReadOnlyList<RegisteredModel> mModels;

	/// <summary>
	/// Initializes the router with the models it exposes and resolves.
	/// </summary>
	/// <param name="models">The models the router knows about; an empty set resolves nothing.</param>
	public FakeModelRouter(params RegisteredModel[] models)
	{
		mModels = models;
	}

	/// <inheritdoc/>
	public IReadOnlyList<RegisteredModel> GetModels() => mModels;

	/// <inheritdoc/>
	public bool TryResolve(string modelName, [NotNullWhen(true)] out RegisteredModel? model)
	{
		model = mModels.FirstOrDefault(candidate => string.Equals(
			candidate.Name,
			modelName,
			StringComparison.OrdinalIgnoreCase));
		return model is not null;
	}
}

/// <summary>
/// A configurable <see cref="IProviderResolver"/> double that pairs the supplied adapter with a
/// backend context named after the requested backend, so handlers receive a ready-to-call adapter.
/// </summary>
sealed class FakeProviderResolver : IProviderResolver
{
	private readonly IProviderAdapter mAdapter;

	/// <summary>
	/// Initializes the resolver with the adapter every backend resolves to.
	/// </summary>
	/// <param name="adapter">The adapter returned for any committed backend.</param>
	public FakeProviderResolver(IProviderAdapter adapter)
	{
		mAdapter = adapter;
	}

	/// <inheritdoc/>
	public ResolvedBackend Resolve(string backendName)
	{
		return new ResolvedBackend(mAdapter, new BackendContext(backendName));
	}

	/// <inheritdoc/>
	public ResolvedBackend ResolveDraft(BackendOptions draft)
	{
		return new ResolvedBackend(mAdapter, new BackendContext("(draft)", draft));
	}
}

/// <summary>
/// A configurable Ollama-protocol <see cref="IProviderAdapter"/> double for the chat, generate, and
/// embeddings endpoint tests. Each request path is backed by a delegate so a test injects a fixed
/// chunk sequence, an aggregated response, an embeddings result, or a <see cref="ProviderException"/>.
/// It deliberately does <b>not</b> implement <see cref="IOpenAiForwarder"/>, so it also serves the
/// OpenAI endpoint's "backend does not speak the OpenAI protocol" branch. Discovery and capability
/// resolution are unsupported because the request endpoints never call them.
/// </summary>
sealed class FakeChatAdapter : IProviderAdapter
{
	/// <summary>Gets or sets the streaming chat behavior invoked by <see cref="StreamChatAsync"/>.</summary>
	public Func<IAsyncEnumerable<OllamaChatResponse>> OnStreamChat { get; set; } =
		() => throw new NotSupportedException();

	/// <summary>Gets or sets the non-streaming chat behavior invoked by <see cref="CompleteChatAsync"/>.</summary>
	public Func<Task<OllamaChatResponse>> OnCompleteChat { get; set; } =
		() => throw new NotSupportedException();

	/// <summary>Gets or sets the embeddings behavior invoked by <see cref="CreateEmbeddingsAsync"/>.</summary>
	public Func<Task<OllamaEmbedResponse>> OnCreateEmbeddings { get; set; } =
		() => throw new NotSupportedException();

	/// <summary>
	/// Gets or sets an optional inspector invoked with the chat request passed to
	/// <see cref="CompleteChatAsync"/>, letting a test assert on the translated request the handler
	/// built (e.g. the wrapped system/user messages of a generate call).
	/// </summary>
	public Action<OllamaChatRequest>? OnCaptureCompleteRequest { get; set; }

	/// <summary>
	/// Gets the full call context of the most recent adapter invocation, or <see langword="null"/> when
	/// no request path has been exercised yet. Lets a test assert the routing contract (backend name,
	/// upstream model, pinned reasoning effort, and the translated request body).
	/// </summary>
	public CapturedChatCall? LastCall { get; private set; }

	/// <inheritdoc/>
	public string ProviderType => "fake";

	/// <inheritdoc/>
	public IAsyncEnumerable<OllamaChatResponse> StreamChatAsync(
		BackendContext    backend,
		string            upstreamModel,
		OllamaChatRequest request,
		ReasoningEffort?  pinnedEffort,
		CancellationToken cancellationToken)
	{
		LastCall = new CapturedChatCall(backend.Name, upstreamModel, pinnedEffort, request, EmbedRequest: null);
		return OnStreamChat();
	}

	/// <inheritdoc/>
	public Task<OllamaChatResponse> CompleteChatAsync(
		BackendContext    backend,
		string            upstreamModel,
		OllamaChatRequest request,
		ReasoningEffort?  pinnedEffort,
		CancellationToken cancellationToken)
	{
		LastCall = new CapturedChatCall(backend.Name, upstreamModel, pinnedEffort, request, EmbedRequest: null);
		OnCaptureCompleteRequest?.Invoke(request);
		return OnCompleteChat();
	}

	/// <inheritdoc/>
	public Task<OllamaEmbedResponse> CreateEmbeddingsAsync(
		BackendContext     backend,
		string             upstreamModel,
		OllamaEmbedRequest request,
		CancellationToken  cancellationToken)
	{
		LastCall = new CapturedChatCall(backend.Name, upstreamModel, PinnedEffort: null, ChatRequest: null, request);
		return OnCreateEmbeddings();
	}

	/// <inheritdoc/>
	public Task<IReadOnlyList<DiscoveredModel>> DiscoverModelsAsync(
		BackendContext    backend,
		CancellationToken cancellationToken) => throw new NotSupportedException();

	/// <inheritdoc/>
	public Task<ModelCapabilities> DetermineCapabilitiesAsync(
		BackendContext    backend,
		DiscoveredModel   model,
		CancellationToken cancellationToken) => throw new NotSupportedException();
}

/// <summary>
/// A configurable <see cref="IProviderAdapter"/> that also implements <see cref="IOpenAiForwarder"/>,
/// for the OpenAI endpoint tests. The forwarding paths are backed by delegates so a test injects a
/// fixed aggregated JSON object, an SSE payload sequence, or a <see cref="ProviderException"/>. The
/// Ollama adapter surface is unsupported because the OpenAI endpoints forward verbatim rather than
/// translating through the Ollama contracts.
/// </summary>
sealed class FakeOpenAiForwarderAdapter : IProviderAdapter, IOpenAiForwarder
{
	/// <summary>
	/// Gets or sets the aggregated JSON behavior invoked by <see cref="ForwardJsonAsync"/>.
	/// </summary>
	public Func<JsonObject, Task<JsonObject>> OnForwardJson { get; set; } =
		_ => throw new NotSupportedException();

	/// <summary>
	/// Gets or sets the streaming behavior invoked by <see cref="ForwardSseAsync"/>.
	/// </summary>
	public Func<IAsyncEnumerable<string>> OnForwardSse { get; set; } = () => throw new NotSupportedException();

	/// <summary>
	/// Gets the full call context of the most recent forwarder invocation, or <see langword="null"/>
	/// when neither forwarding path has been exercised yet. Lets a test assert the forwarding contract
	/// (backend name, upstream path, pinned reasoning effort, and the rewritten request body).
	/// </summary>
	public CapturedOpenAiForwardCall? LastCall { get; private set; }

	/// <inheritdoc/>
	public string ProviderType => "fake-openai";

	/// <inheritdoc/>
	public Task<JsonObject> ForwardJsonAsync(
		BackendContext    backend,
		string            path,
		JsonObject        body,
		ReasoningEffort?  pinnedEffort,
		CancellationToken cancellationToken)
	{
		LastCall = new CapturedOpenAiForwardCall(backend.Name, path, pinnedEffort, body);
		return OnForwardJson(body);
	}

	/// <inheritdoc/>
	public IAsyncEnumerable<string> ForwardSseAsync(
		BackendContext    backend,
		string            path,
		JsonObject        body,
		ReasoningEffort?  pinnedEffort,
		CancellationToken cancellationToken)
	{
		LastCall = new CapturedOpenAiForwardCall(backend.Name, path, pinnedEffort, body);
		return OnForwardSse();
	}

	/// <inheritdoc/>
	public IAsyncEnumerable<OllamaChatResponse> StreamChatAsync(
		BackendContext    backend,
		string            upstreamModel,
		OllamaChatRequest request,
		ReasoningEffort?  pinnedEffort,
		CancellationToken cancellationToken) => throw new NotSupportedException();

	/// <inheritdoc/>
	public Task<OllamaChatResponse> CompleteChatAsync(
		BackendContext    backend,
		string            upstreamModel,
		OllamaChatRequest request,
		ReasoningEffort?  pinnedEffort,
		CancellationToken cancellationToken) => throw new NotSupportedException();

	/// <inheritdoc/>
	public Task<OllamaEmbedResponse> CreateEmbeddingsAsync(
		BackendContext     backend,
		string             upstreamModel,
		OllamaEmbedRequest request,
		CancellationToken  cancellationToken) => throw new NotSupportedException();

	/// <inheritdoc/>
	public Task<IReadOnlyList<DiscoveredModel>> DiscoverModelsAsync(
		BackendContext    backend,
		CancellationToken cancellationToken) => throw new NotSupportedException();

	/// <inheritdoc/>
	public Task<ModelCapabilities> DetermineCapabilitiesAsync(
		BackendContext    backend,
		DiscoveredModel   model,
		CancellationToken cancellationToken) => throw new NotSupportedException();
}

/// <summary>
/// A <see cref="TimeProvider"/> that always reports a fixed instant, so timestamp-stamping handlers
/// (<c>/api/tags</c>, <c>/v1/models</c>) produce deterministic, exactly-assertable output.
/// </summary>
sealed class FixedTimeProvider : TimeProvider
{
	private readonly DateTimeOffset mNow;

	/// <summary>
	/// Initializes the provider with the instant it always returns.
	/// </summary>
	/// <param name="now">The fixed UTC instant reported by <see cref="GetUtcNow"/>.</param>
	public FixedTimeProvider(DateTimeOffset now)
	{
		mNow = now;
	}

	/// <inheritdoc/>
	public override DateTimeOffset GetUtcNow() => mNow;
}
