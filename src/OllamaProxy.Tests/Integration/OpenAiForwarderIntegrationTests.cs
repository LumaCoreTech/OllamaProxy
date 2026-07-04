// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using OllamaProxy.Configuration;
using OllamaProxy.Diagnostics;
using OllamaProxy.Providers.Abstractions;
using OllamaProxy.Providers.Http;
using OllamaProxy.Providers.OpenAi;
using OllamaProxy.Providers.OpenAiProtocol;

namespace OllamaProxy.Tests.Integration;

/// <summary>
/// Integration tests for the <see cref="IOpenAiForwarder"/> passthrough on <see cref="OpenAiProvider"/>,
/// exercised end to end against a canned OpenAI-compatible backend. The forwarder underpins the inbound
/// <c>/v1</c> endpoints: it sends a request body verbatim and relays the response without a lossy
/// round-trip through the Ollama contracts. The story covers a non-streaming JSON forward and a raw SSE
/// forward, asserting the request reaches the right path and the response is returned faithfully.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OpenAiForwarderIntegrationTests
{
	private const string BackendName                         = "mock";
	private const string ChatCompletionsPath                 = "chat/completions";
	private const string ExpectedChatCompletionsAbsolutePath = "/v1/chat/completions";
	private const string UpstreamModel                       = "upstream-model";
	private const string ForwardRequestBody                  = "{\"model\":\"upstream-model\",\"stream\":false}";
	private const string ForwardStreamingRequestBody         = "{\"model\":\"upstream-model\",\"stream\":true}";

	private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
		{ Content = new StringContent(body, Encoding.UTF8, "application/json") };

	private static OpenAiProvider CreateProvider(ScriptedHandler handler) =>
		CreateProvider(handler, new RequestTraceAccessor());

	private static OpenAiProvider CreateProvider(ScriptedHandler handler, IRequestTraceAccessor traceAccessor) =>
		CreateProvider(handler, traceAccessor, backendDefaultReasoning: null);

	private static OpenAiProvider CreateProvider(
		ScriptedHandler       handler,
		IRequestTraceAccessor traceAccessor,
		ReasoningEffort?      backendDefaultReasoning) => new(
		new StubHttpClientProvider(handler),
		new StubCapabilityProber(),
		TimeProvider.System,
		Options.Create(
			new ProxyOptions
			{
				Backends =
				{
					[BackendName] = new BackendOptions
					{
						BaseUrl = "https://mock.test/v1/",
						ProviderType = "openai",
						ApiKey = "test-key-1234",
						ReasoningEffort = backendDefaultReasoning
					}
				}
			}),
		traceAccessor,
		TestReasoningDetailsCache.CreateDefault(),
		NullLogger<OpenAiProvider>.Instance);

	/// <summary>
	/// Publishes a fresh <see cref="TraceScope"/> over a new <see cref="RequestTrace"/> on the ambient
	/// <see cref="RequestTraceAccessor"/> and returns both, so a test can drive the forwarder and then
	/// inspect the recorded entries. The accessor is <see cref="AsyncLocal{T}"/>-backed; setting it from
	/// this synchronous helper leaves the scope visible to the test's subsequent <c>await</c> calls.
	/// </summary>
	/// <returns>
	/// The trace-aware accessor and the underlying trace it records into.
	/// </returns>
	private static (IRequestTraceAccessor Accessor, RequestTrace Trace) CreateTrace()
	{
		RequestTrace trace = new("corr-1", DateTimeOffset.UnixEpoch, "POST", "/v1/chat/completions");
		RequestTraceAccessor accessor = new();
		accessor.Set(new TraceScope(trace, maxBodyBytes: 64 * 1024, redactAttachments: true, TimeProvider.System));

		return (accessor, trace);
	}

	/// <summary>
	/// Verifies that <see cref="IOpenAiForwarder.ForwardJsonAsync"/> posts the body verbatim to the
	/// given path and returns the upstream JSON object unchanged.
	/// </summary>
	[Fact]
	public async Task ForwardJsonAsync_AgainstMockBackend_PostsBodyAndReturnsObject()
	{
		// Arrange
		const string responseBody = """{"id":"c1","model":"upstream-model","object":"chat.completion"}""";
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		OpenAiProvider sut = CreateProvider(handler);
		JsonObject body = new() { ["model"] = UpstreamModel, ["stream"] = false };

		// Act
		JsonObject result = await sut.ForwardJsonAsync(
			                    new BackendContext(BackendName),
			                    ChatCompletionsPath,
			                    body,
			                    pinnedEffort: null,
			                    CancellationToken.None);

		// Assert
		Assert.Equal(ExpectedChatCompletionsAbsolutePath, handler.LastRequest!.RequestUri!.AbsolutePath);
		Assert.Equal(ForwardRequestBody, handler.LastRequestBody);
		Assert.Equal("c1", result["id"]!.GetValue<string>());
		Assert.Equal(UpstreamModel, result["model"]!.GetValue<string>());
		Assert.Equal("chat.completion", result["object"]!.GetValue<string>());
	}

	/// <summary>
	/// Verifies that a non-streaming <c>/v1</c> forward records both the forwarded
	/// <see cref="TraceStage.BackendRequest"/> and the upstream <see cref="TraceStage.BackendResponse"/>,
	/// because the passthrough bypasses the Ollama chat mapping that would otherwise record them.
	/// </summary>
	[Fact]
	public async Task ForwardJsonAsync_WhenTraced_RecordsBackendRequestAndResponse()
	{
		// Arrange
		const string responseBody = """{"id":"c1","model":"upstream-model","object":"chat.completion"}""";
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		(IRequestTraceAccessor accessor, RequestTrace trace) = CreateTrace();
		OpenAiProvider sut = CreateProvider(handler, accessor);
		JsonObject body = new() { ["model"] = UpstreamModel, ["stream"] = false };

		// Act
		await sut.ForwardJsonAsync(
			new BackendContext(BackendName),
			ChatCompletionsPath,
			body,
			pinnedEffort: null,
			CancellationToken.None);

		// Assert: the request body is recorded verbatim and the response carries the upstream JSON.
		TraceEntry request = Assert.Single(trace.Entries, e => e.Stage == TraceStage.BackendRequest);
		Assert.Equal(ForwardRequestBody, request.Body);
		TraceEntry response = Assert.Single(trace.Entries, e => e.Stage == TraceStage.BackendResponse);
		Assert.Equal(responseBody, response.Body);
	}

	/// <summary>
	/// Verifies that a non-streaming <c>/v1</c> forward whose response carries <c>reasoning_content</c>
	/// records the chain-of-thought under its own <see cref="TraceStage.BackendReasoning"/> entry, while
	/// the full upstream JSON still lands in <see cref="TraceStage.BackendResponse"/>.
	/// </summary>
	[Fact]
	public async Task ForwardJsonAsync_WhenTracedWithReasoning_RecordsBackendReasoning()
	{
		// Arrange: the upstream message carries both visible content and a separate reasoning channel.
		const string responseBody =
			"""
			{"id":"c1","model":"upstream-model","choices":[{"message":{"role":"assistant","content":"Hello","reasoning_content":"Thinking"}}]}
			""";
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		(IRequestTraceAccessor accessor, RequestTrace trace) = CreateTrace();
		OpenAiProvider sut = CreateProvider(handler, accessor);
		JsonObject body = new() { ["model"] = UpstreamModel, ["stream"] = false };

		// Act
		await sut.ForwardJsonAsync(
			new BackendContext(BackendName),
			"chat/completions",
			body,
			pinnedEffort: null,
			CancellationToken.None);

		// Assert: reasoning is isolated into its own stage; the full JSON (including the visible answer)
		// still lands in BackendResponse.
		TraceEntry reasoning = Assert.Single(trace.Entries, e => e.Stage == TraceStage.BackendReasoning);
		Assert.Equal("Thinking", reasoning.Body);
		TraceEntry response = Assert.Single(trace.Entries, e => e.Stage == TraceStage.BackendResponse);
		Assert.Contains("Hello", response.Body, StringComparison.Ordinal);
	}

	/// <summary>
	/// Verifies that a non-streaming <c>/v1</c> forward whose response has no reasoning channel does not
	/// emit an empty <see cref="TraceStage.BackendReasoning"/> entry.
	/// </summary>
	[Fact]
	public async Task ForwardJsonAsync_WhenTracedWithoutReasoning_DoesNotRecordBackendReasoning()
	{
		// Arrange: a plain completion with no reasoning_content field.
		const string responseBody =
			"""{"id":"c1","model":"upstream-model","choices":[{"message":{"role":"assistant","content":"Hello"}}]}""";
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		(IRequestTraceAccessor accessor, RequestTrace trace) = CreateTrace();
		OpenAiProvider sut = CreateProvider(handler, accessor);
		JsonObject body = new() { ["model"] = "upstream-model", ["stream"] = false };

		// Act
		await sut.ForwardJsonAsync(
			new BackendContext(BackendName),
			"chat/completions",
			body,
			pinnedEffort: null,
			CancellationToken.None);

		// Assert: no reasoning stage, but the visible response is still recorded.
		Assert.DoesNotContain(trace.Entries, e => e.Stage == TraceStage.BackendReasoning);
		TraceEntry response = Assert.Single(trace.Entries, e => e.Stage == TraceStage.BackendResponse);
		Assert.Contains("Hello", response.Body, StringComparison.Ordinal);
	}

	/// <summary>
	/// Verifies that a non-streaming <c>/v1</c> forward whose body omits a reasoning directive has the
	/// backend's configured default <see cref="ReasoningEffort"/> injected as <c>reasoning_effort</c>
	/// before forwarding, and that the provenance is recorded under
	/// <see cref="TraceStage.ReasoningResolution"/> with a <c>backend default</c> source. This mirrors
	/// the Ollama-native path so the verbatim passthrough still honors the proxy's reasoning policy.
	/// </summary>
	[Fact]
	public async Task ForwardJsonAsync_WhenBackendDefaultSetAndClientOmits_InjectsReasoningEffort()
	{
		// Arrange: the backend defaults to Max reasoning (which the OpenAI dialect clamps to xhigh), and the
		// client sends no reasoning directive (exactly the Copilot → /v1/chat/completions case).
		const string responseBody = """{"id":"c1","model":"upstream-model","object":"chat.completion"}""";
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		(IRequestTraceAccessor accessor, RequestTrace trace) = CreateTrace();
		OpenAiProvider sut = CreateProvider(handler, accessor, ReasoningEffort.Max);
		JsonObject body = new() { ["model"] = "upstream-model", ["stream"] = false };

		// Act
		await sut.ForwardJsonAsync(
			new BackendContext(BackendName),
			"chat/completions",
			body,
			pinnedEffort: null,
			CancellationToken.None);

		// Assert: the non-pinned backend default max is clamped to the OpenAI dialect ceiling (xhigh) on the wire.
		Assert.Contains("\"reasoning_effort\":\"xhigh\"", handler.LastRequestBody, StringComparison.Ordinal);

		// And the provenance stage records the effective (clamped) value, while still attributing the source
		// to the backend default and reporting the original configured default.
		TraceEntry reasoning = Assert.Single(trace.Entries, e => e.Stage == TraceStage.ReasoningResolution);
		Assert.Equal("xhigh", reasoning.Detail!["resolvedEffort"]);
		Assert.Equal("backend default", reasoning.Detail!["source"]);
		Assert.Equal("max", reasoning.Detail!["backendDefault"]);
		Assert.Equal("reasoning_effort", reasoning.Detail!["wireField"]);
	}

	/// <summary>
	/// Verifies that a non-streaming <c>/v1</c> forward whose body already carries an explicit
	/// <c>reasoning_effort</c> keeps the client's value untouched even when the backend default differs,
	/// and records the provenance as a <c>request</c> source. An explicit per-request wish always wins.
	/// </summary>
	[Fact]
	public async Task ForwardJsonAsync_WhenClientSetsReasoningEffort_PreservesClientValue()
	{
		// Arrange: the backend defaults to Max, but the client explicitly asks for "low" — the client wins.
		const string responseBody = """{"id":"c1","model":"upstream-model","object":"chat.completion"}""";
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		(IRequestTraceAccessor accessor, RequestTrace trace) = CreateTrace();
		OpenAiProvider sut = CreateProvider(handler, accessor, ReasoningEffort.Max);
		JsonObject body = new()
		{
			["model"] = "upstream-model", ["stream"] = false, ["reasoning_effort"] = "low"
		};

		// Act
		await sut.ForwardJsonAsync(
			new BackendContext(BackendName),
			"chat/completions",
			body,
			pinnedEffort: null,
			CancellationToken.None);

		// Assert: the client's value is forwarded verbatim; the backend default did not overwrite it.
		Assert.Contains("\"reasoning_effort\":\"low\"", handler.LastRequestBody, StringComparison.Ordinal);
		Assert.DoesNotContain("\"max\"", handler.LastRequestBody, StringComparison.Ordinal);

		// And provenance attributes the value to the request, not the backend default.
		TraceEntry reasoning = Assert.Single(trace.Entries, e => e.Stage == TraceStage.ReasoningResolution);
		Assert.Equal("low", reasoning.Detail!["resolvedEffort"]);
		Assert.Equal("request", reasoning.Detail!["source"]);
	}

	/// <summary>
	/// Verifies that the backend default reasoning effort is <em>not</em> injected on the legacy
	/// <c>/v1/completions</c> passthrough, because reasoning is a chat concept; the text-completion path
	/// must forward the body untouched and record no <see cref="TraceStage.ReasoningResolution"/> stage.
	/// </summary>
	[Fact]
	public async Task ForwardJsonAsync_WhenPathIsNotChatCompletions_DoesNotInjectReasoning()
	{
		// Arrange: a backend default is configured, but the target is the legacy text-completions path.
		const string responseBody = """{"id":"c1","model":"upstream-model","object":"text_completion"}""";
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		(IRequestTraceAccessor accessor, RequestTrace trace) = CreateTrace();
		OpenAiProvider sut = CreateProvider(handler, accessor, ReasoningEffort.Max);
		JsonObject body = new() { ["model"] = "upstream-model", ["stream"] = false };

		// Act
		await sut.ForwardJsonAsync(
			new BackendContext(BackendName),
			"completions",
			body,
			pinnedEffort: null,
			CancellationToken.None);

		// Assert: the body is forwarded untouched and no reasoning provenance is recorded.
		Assert.DoesNotContain("reasoning_effort", handler.LastRequestBody, StringComparison.Ordinal);
		Assert.DoesNotContain(trace.Entries, e => e.Stage == TraceStage.ReasoningResolution);
	}

	/// <summary>
	/// Verifies that <see cref="IOpenAiForwarder.ForwardSseAsync"/> yields the raw JSON payload of each
	/// SSE data frame in order and stops at the <c>[DONE]</c> sentinel.
	/// </summary>
	[Fact]
	public async Task ForwardSseAsync_AgainstMockBackend_YieldsRawDataPayloads()
	{
		// Arrange
		string sse = string.Join(
			"\n",
			"""data: {"id":"c1","choices":[{"delta":{"content":"Hel"}}]}""",
			"""data: {"id":"c1","choices":[{"delta":{"content":"lo"}}]}""",
			"data: [DONE]",
			"");
		ScriptedHandler handler = new(_ =>
			new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
			});
		OpenAiProvider sut = CreateProvider(handler);
		JsonObject body = new() { ["model"] = UpstreamModel, ["stream"] = true };

		// Act
		List<string> payloads = [];
		await foreach (string payload in sut.ForwardSseAsync(
			               new BackendContext(BackendName),
			               ChatCompletionsPath,
			               body,
			               pinnedEffort: null,
			               CancellationToken.None))
		{
			payloads.Add(payload);
		}

		// Assert: both content frames are surfaced verbatim; the sentinel is consumed, not yielded.
		Assert.Equal(
			[
				"{\"id\":\"c1\",\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}",
				"{\"id\":\"c1\",\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}"
			],
			payloads);
		Assert.Equal(ForwardStreamingRequestBody, handler.LastRequestBody);
	}

	/// <summary>
	/// Verifies that a streamed <c>/v1</c> forward records a single aggregated
	/// <see cref="TraceStage.BackendResponse"/> carrying the composed assistant text assembled from the
	/// SSE delta frames, alongside the forwarded <see cref="TraceStage.BackendRequest"/>.
	/// </summary>
	[Fact]
	public async Task ForwardSseAsync_WhenTraced_RecordsAggregatedBackendResponse()
	{
		// Arrange: two content deltas and the sentinel, framed as OpenAI SSE.
		string sse = string.Join(
			"\n",
			"""data: {"id":"c1","choices":[{"delta":{"content":"Hel"}}]}""",
			"""data: {"id":"c1","choices":[{"delta":{"content":"lo"}}]}""",
			"data: [DONE]",
			"");
		ScriptedHandler handler = new(_ =>
			new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
			});
		(IRequestTraceAccessor accessor, RequestTrace trace) = CreateTrace();
		OpenAiProvider sut = CreateProvider(handler, accessor);
		JsonObject body = new() { ["model"] = "upstream-model", ["stream"] = true };

		// Act: the stream must be drained fully so the provider's finally records the assembled text.
		await foreach (string _ in sut.ForwardSseAsync(
			               new BackendContext(BackendName),
			               "chat/completions",
			               body,
			               pinnedEffort: null,
			               CancellationToken.None))
		{
			// Drain only — the assertion is on the recorded trace, not the yielded frames.
		}

		// Assert: the forwarded request is recorded and the response aggregates the two deltas into "Hello".
		TraceEntry request = Assert.Single(trace.Entries, e => e.Stage == TraceStage.BackendRequest);
		Assert.Contains("\"upstream-model\"", request.Body, StringComparison.Ordinal);
		TraceEntry response = Assert.Single(trace.Entries, e => e.Stage == TraceStage.BackendResponse);
		Assert.Equal("Hello", response.Body);

		// And a response without reasoning frames must not emit an (empty) reasoning stage.
		Assert.DoesNotContain(trace.Entries, e => e.Stage == TraceStage.BackendReasoning);
	}

	/// <summary>
	/// Verifies that a streamed <c>/v1</c> forward whose frames carry <c>reasoning_content</c> deltas
	/// records the aggregated chain-of-thought under its own <see cref="TraceStage.BackendReasoning"/>
	/// entry while the visible answer is aggregated separately under
	/// <see cref="TraceStage.BackendResponse"/>.
	/// </summary>
	[Fact]
	public async Task ForwardSseAsync_WhenTracedWithReasoning_RecordsAggregatedBackendReasoning()
	{
		// Arrange: each frame interleaves a reasoning delta with a visible content delta, as a reasoning
		// model streams it, followed by the sentinel.
		string sse = string.Join(
			"\n",
			"""data: {"id":"c1","choices":[{"delta":{"reasoning_content":"Think","content":"Hel"}}]}""",
			"""data: {"id":"c1","choices":[{"delta":{"reasoning_content":"ing","content":"lo"}}]}""",
			"data: [DONE]",
			"");
		ScriptedHandler handler = new(_ =>
			new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
			});
		(IRequestTraceAccessor accessor, RequestTrace trace) = CreateTrace();
		OpenAiProvider sut = CreateProvider(handler, accessor);
		JsonObject body = new() { ["model"] = "upstream-model", ["stream"] = true };

		// Act: the stream must be drained fully so the provider's finally records both assembled buffers.
		await foreach (string _ in sut.ForwardSseAsync(
			               new BackendContext(BackendName),
			               "chat/completions",
			               body,
			               pinnedEffort: null,
			               CancellationToken.None))
		{
			// Drain only — the assertion is on the recorded trace, not the yielded frames.
		}

		// Assert: reasoning and visible answer are aggregated into distinct stages, neither bleeding into
		// the other.
		TraceEntry reasoning = Assert.Single(trace.Entries, e => e.Stage == TraceStage.BackendReasoning);
		Assert.Equal("Thinking", reasoning.Body);
		TraceEntry response = Assert.Single(trace.Entries, e => e.Stage == TraceStage.BackendResponse);
		Assert.Equal("Hello", response.Body);
	}

	/// <summary>
	/// Verifies that a streamed <c>/v1</c> forward whose body omits a reasoning directive has the
	/// backend's configured default <see cref="ReasoningEffort"/> injected as <c>reasoning_effort</c>
	/// into the forwarded request and recorded under <see cref="TraceStage.ReasoningResolution"/>. This
	/// is the streaming counterpart to the non-streaming injection, since Copilot streams its chat calls.
	/// </summary>
	[Fact]
	public async Task ForwardSseAsync_WhenBackendDefaultSetAndClientOmits_InjectsReasoningEffort()
	{
		// Arrange: a streaming request with no client reasoning directive; the backend defaults to Max, which
		// the OpenAI dialect clamps to xhigh.
		string sse = string.Join(
			"\n",
			"""data: {"id":"c1","choices":[{"delta":{"content":"Hi"}}]}""",
			"data: [DONE]",
			"");
		ScriptedHandler handler = new(_ =>
			new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
			});
		(IRequestTraceAccessor accessor, RequestTrace trace) = CreateTrace();
		OpenAiProvider sut = CreateProvider(handler, accessor, ReasoningEffort.Max);
		JsonObject body = new() { ["model"] = "upstream-model", ["stream"] = true };

		// Act: drain the stream so the request is sent and the trace is recorded.
		await foreach (string _ in sut.ForwardSseAsync(
			               new BackendContext(BackendName),
			               "chat/completions",
			               body,
			               pinnedEffort: null,
			               CancellationToken.None))
		{
			// Drain only — the assertion is on the forwarded request and the recorded provenance.
		}

		// Assert: the non-pinned backend default max is clamped to the OpenAI dialect ceiling (xhigh) on the wire.
		Assert.Contains("\"reasoning_effort\":\"xhigh\"", handler.LastRequestBody, StringComparison.Ordinal);

		// And the provenance stage attributes it to the backend default, recording the effective clamped value.
		TraceEntry reasoning = Assert.Single(trace.Entries, e => e.Stage == TraceStage.ReasoningResolution);
		Assert.Equal("xhigh", reasoning.Detail!["resolvedEffort"]);
		Assert.Equal("backend default", reasoning.Detail!["source"]);
	}

	/// <summary>
	/// Verifies that an upstream error during a forward is translated into a
	/// <see cref="ProviderException"/> carrying the backend's status code.
	/// </summary>
	[Fact]
	public async Task ForwardJsonAsync_WhenBackendReturnsError_ThrowsProviderExceptionWithStatus()
	{
		// Arrange
		const string errorBody = """{"error":{"message":"boom","type":"api_error"}}""";
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.ServiceUnavailable, errorBody));
		OpenAiProvider sut = CreateProvider(handler);
		JsonObject body = new() { ["model"] = "upstream-model" };

		// Act + Assert
		var exception = await Assert.ThrowsAsync<ProviderException>(() =>
			                sut.ForwardJsonAsync(
				                new BackendContext(BackendName),
				                "chat/completions",
				                body,
				                pinnedEffort: null,
				                CancellationToken.None));
		Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
	}

	/// <summary>Captures the request and returns a scripted response.</summary>
	private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
	{
		public HttpRequestMessage? LastRequest { get; private set; }

		public string? LastRequestBody { get; private set; }

		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken  cancellationToken)
		{
			LastRequest = request;
			if (request.Content is not null)
				LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);

			return responder(request);
		}
	}

	/// <summary>Hands out a single <see cref="HttpClient"/> over the scripted handler with a test base address.</summary>
	private sealed class StubHttpClientProvider(ScriptedHandler handler) : IBackendHttpClientProvider
	{
		public HttpClient CreateClient(string backendName) => new(handler, disposeHandler: false)
			{ BaseAddress = new Uri("https://mock.test/v1/") };
	}

	/// <summary>
	/// Reports every probe as inconclusive so the provider can be constructed without probing internals;
	/// these tests exercise the forwarder passthrough, not capability determination.
	/// </summary>
	private sealed class StubCapabilityProber : ICapabilityProber
	{
		public Task<bool?> ProbeCompletionSupportAsync(
			BackendContext    backend,
			string            modelId,
			CancellationToken cancellationToken) => Task.FromResult<bool?>(null);

		public Task<bool?> ProbeToolSupportAsync(
			BackendContext    backend,
			string            modelId,
			CancellationToken cancellationToken) => Task.FromResult<bool?>(null);

		public Task<bool?> ProbeVisionSupportAsync(
			BackendContext    backend,
			string            modelId,
			CancellationToken cancellationToken) => Task.FromResult<bool?>(null);

		public Task<bool?> ProbeEmbeddingSupportAsync(
			BackendContext    backend,
			string            modelId,
			CancellationToken cancellationToken) => Task.FromResult<bool?>(null);
	}
}
