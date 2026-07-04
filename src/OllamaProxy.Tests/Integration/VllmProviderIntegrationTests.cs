// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using OllamaProxy.Configuration;
using OllamaProxy.Contracts.Ollama;
using OllamaProxy.Diagnostics;
using OllamaProxy.Providers.Abstractions;
using OllamaProxy.Providers.Http;
using OllamaProxy.Providers.OpenAiProtocol;
using OllamaProxy.Providers.Vllm;

namespace OllamaProxy.Tests.Integration;

/// <summary>
/// Integration tests exercising <see cref="VllmProvider"/> against a mock vLLM-compatible backend.
/// Discovery: vLLM advertises its served context window under <c>max_model_len</c> but no capability
/// metadata, so these tests verify that the context window is read while modalities and supported
/// parameters stay unset for the later detection stages to resolve. Chat: vLLM honors the de-facto
/// <c>top_k</c>/<c>min_p</c> sampling extensions, so the provider forwards them — the counterpart to the
/// generic OpenAI provider stripping them, verified in <c>OpenAiProviderIntegrationTests</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class VllmProviderIntegrationTests
{
	private const string BackendName = "mock";

	private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
		{ Content = new StringContent(body, Encoding.UTF8, "application/json") };

	private static VllmProvider CreateProvider(ScriptedHandler handler) => new(
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
						BaseUrl = "https://mock.test/v1/", ProviderType = "vllm", ApiKey = "test-key-1234"
					}
				}
			}),
		new RequestTraceAccessor(),
		TestReasoningDetailsCache.CreateDefault(),
		NullLogger<VllmProvider>.Instance);

	/// <summary>
	/// Verifies that discovery reads vLLM's top-level <c>max_model_len</c> as the model's context length
	/// while leaving capability metadata unset, since vLLM advertises none.
	/// </summary>
	[Fact]
	public async Task DiscoverModelsAsync_WhenBackendReportsMaxModelLen_ProjectsContextLength()
	{
		// Arrange: a vLLM-style listing carrying max_model_len.
		const string responseBody = """{"data":[{"id":"qwen3-next-80b","max_model_len":262144}]}""";
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		VllmProvider sut = CreateProvider(handler);

		// Act
		IReadOnlyList<DiscoveredModel> models = await sut.DiscoverModelsAsync(
			                                        new BackendContext(BackendName),
			                                        CancellationToken.None);

		// Assert: context window read; no capability metadata synthesized.
		Assert.EndsWith("models", handler.LastRequest!.RequestUri!.AbsolutePath, StringComparison.Ordinal);
		DiscoveredModel model = Assert.Single(models);
		Assert.Equal("qwen3-next-80b", model.Id);
		Assert.Equal(262144, model.ContextLength);
		Assert.Null(model.Capabilities);
	}

	/// <summary>
	/// Verifies that a vLLM listing without <c>max_model_len</c> projects to a model with no context
	/// length, the case the catalog builder later backfills from configuration.
	/// </summary>
	[Fact]
	public async Task DiscoverModelsAsync_WhenBackendReportsNoMaxModelLen_ProjectsNullContextLength()
	{
		// Arrange: a bare vLLM-style listing carrying only an id.
		const string responseBody = """{"data":[{"id":"qwen3-next-80b"}]}""";
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		VllmProvider sut = CreateProvider(handler);

		// Act
		IReadOnlyList<DiscoveredModel> models = await sut.DiscoverModelsAsync(
			                                        new BackendContext(BackendName),
			                                        CancellationToken.None);

		// Assert
		DiscoveredModel model = Assert.Single(models);
		Assert.Equal("qwen3-next-80b", model.Id);
		Assert.Null(model.ContextLength);
	}

	/// <summary>
	/// Verifies that vLLM forwards the non-standard <c>top_k</c> and <c>min_p</c> sampling extensions
	/// onto the outgoing chat body, since vLLM honors them. This is the counterpart to the generic
	/// OpenAI provider stripping them, verified in <c>OpenAiProviderIntegrationTests</c>.
	/// </summary>
	[Fact]
	public async Task CompleteChatAsync_WhenClientSuppliesTopKAndMinP_ForwardsThemToVllm()
	{
		// Arrange: the client sets both extensions and vLLM is expected to keep them on the wire.
		const string responseBody = """
		                            {"id":"c1","model":"qwen3","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
		                            """;
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		VllmProvider sut = CreateProvider(handler);
		OllamaChatRequest request = new(
			"client-model",
			[new OllamaChatMessage("user", "hello")],
			Options: new OllamaOptions(TopK: 20, MinP: 0.1));

		// Act
		await sut.CompleteChatAsync(
			new BackendContext(BackendName),
			"qwen3",
			request,
			pinnedEffort: null,
			CancellationToken.None);

		// Assert: both extensions reached the wire with the client-supplied values.
		JsonObject body = JsonNode.Parse(handler.LastRequestBody!)!.AsObject();
		Assert.Equal(20, (int)body["top_k"]!);
		Assert.Equal(0.1, (double)body["min_p"]!);
	}

	#region Parallel tool calls

	/// <summary>
	/// Verifies that two parallel calls to the <em>same</em> tool, returned in one non-streaming vLLM
	/// completion, each surface to the client carrying their own distinct call id. The id is the only
	/// thing that distinguishes the calls — both are <c>get_weather</c> — so dropping it would leave the
	/// client unable to tell which result belongs to which call. This is the response-side guard for the
	/// parallel-tool-call correlation the proxy must preserve, mirroring the generic OpenAI coverage.
	/// </summary>
	[Fact]
	public async Task CompleteChatAsync_WhenBackendReturnsParallelToolCalls_CarriesEachDistinctId()
	{
		// Arrange: one assistant turn calling get_weather twice (Berlin and Hamburg), each with its own id.
		const string responseBody = """
		                            {"id":"c1","model":"qwen3","choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_berlin","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Berlin\"}"}},{"id":"call_hamburg","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Hamburg\"}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":10,"completion_tokens":8,"total_tokens":18}}
		                            """;
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		VllmProvider sut = CreateProvider(handler);
		OllamaChatRequest request = new(
			"client-model",
			[new OllamaChatMessage("user", "Weather in Berlin and Hamburg?")]);

		// Act
		OllamaChatResponse result = await sut.CompleteChatAsync(
			                            new BackendContext(BackendName),
			                            "qwen3",
			                            request,
			                            pinnedEffort: null,
			                            CancellationToken.None);

		// Assert: both calls surface, each tied to its id and its city — the id band the client correlates by.
		Assert.Equal(2, result.Message.ToolCalls!.Count);

		OllamaToolCall berlin = result.Message.ToolCalls[0];
		Assert.Equal("call_berlin", berlin.Id);
		Assert.Equal("get_weather", berlin.Function.Name);
		Assert.Equal("Berlin", berlin.Function.Arguments?["city"]?.GetValue<string>());

		OllamaToolCall hamburg = result.Message.ToolCalls[1];
		Assert.Equal("call_hamburg", hamburg.Id);
		Assert.Equal("get_weather", hamburg.Function.Name);
		Assert.Equal("Hamburg", hamburg.Function.Arguments?["city"]?.GetValue<string>());
	}

	/// <summary>
	/// Verifies that two parallel calls to the same tool, streamed across several SSE deltas (each call's
	/// id and name arrive on its opening delta, its arguments in later fragments), are reassembled onto the
	/// terminal chunk with both ids intact. This is the streaming counterpart to the non-streaming
	/// parallel-tool-call guard: the accumulator must keep each index's id while buffering its argument
	/// fragments.
	/// </summary>
	[Fact]
	public async Task StreamChatAsync_WhenBackendStreamsParallelToolCalls_ReassemblesBothIdsOnTerminalChunk()
	{
		// Arrange: index 0 (Berlin) and index 1 (Hamburg) each open with id+name, then stream their
		// arguments; a finish-reason delta and a usage-only event close the stream.
		string sse = string.Join(
			"\n",
			"""data: {"id":"c1","model":"qwen3","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_berlin","type":"function","function":{"name":"get_weather","arguments":""}}]},"finish_reason":null}]}""",
			"""data: {"id":"c1","model":"qwen3","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"city\":\"Berlin\"}"}}]},"finish_reason":null}]}""",
			"""data: {"id":"c1","model":"qwen3","choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"id":"call_hamburg","type":"function","function":{"name":"get_weather","arguments":""}}]},"finish_reason":null}]}""",
			"""data: {"id":"c1","model":"qwen3","choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"function":{"arguments":"{\"city\":\"Hamburg\"}"}}]},"finish_reason":null}]}""",
			"""data: {"id":"c1","model":"qwen3","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
			"""data: {"id":"c1","model":"qwen3","choices":[],"usage":{"prompt_tokens":10,"completion_tokens":8,"total_tokens":18}}""",
			"data: [DONE]",
			string.Empty);
		ScriptedHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
		});
		VllmProvider sut = CreateProvider(handler);
		OllamaChatRequest request = new(
			"client-model",
			[new OllamaChatMessage("user", "Weather in Berlin and Hamburg?")],
			Stream: true);

		// Act
		List<OllamaChatResponse> chunks = [];
		await foreach (OllamaChatResponse chunk in sut.StreamChatAsync(
			               new BackendContext(BackendName),
			               "qwen3",
			               request,
			               pinnedEffort: null,
			               CancellationToken.None))
		{
			chunks.Add(chunk);
		}

		// Assert: the terminal chunk carries both reassembled calls, each with its id and city preserved.
		OllamaChatResponse terminal = Assert.Single(chunks, c => c.Done);
		Assert.Equal(2, terminal.Message.ToolCalls!.Count);

		OllamaToolCall berlin = terminal.Message.ToolCalls[0];
		Assert.Equal("call_berlin", berlin.Id);
		Assert.Equal("Berlin", berlin.Function.Arguments?["city"]?.GetValue<string>());

		OllamaToolCall hamburg = terminal.Message.ToolCalls[1];
		Assert.Equal("call_hamburg", hamburg.Id);
		Assert.Equal("Hamburg", hamburg.Function.Arguments?["city"]?.GetValue<string>());
	}

	/// <summary>
	/// Verifies the inbound leg of the round-trip: when the client replays a turn with two parallel tool
	/// calls and their two results, the outgoing vLLM request preserves each assistant call's id and
	/// stamps each tool result with the matching <c>tool_call_id</c>. This is what lets the backend tie the
	/// 22°C result to the Berlin call and the 9°C result to the Hamburg call — a correlation the shared
	/// tool name alone cannot make.
	/// </summary>
	[Fact]
	public async Task CompleteChatAsync_WhenClientReplaysParallelToolResults_CorrelatesEachResultById()
	{
		// Arrange: a continuation turn — the prior assistant called get_weather twice, and the client now
		// returns both results, each keyed to the call it answers by tool_call_id.
		const string responseBody = """
		                            {"id":"c2","model":"qwen3","choices":[{"index":0,"message":{"role":"assistant","content":"Berlin 22C, Hamburg 9C"},"finish_reason":"stop"}],"usage":{"prompt_tokens":20,"completion_tokens":10,"total_tokens":30}}
		                            """;
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		VllmProvider sut = CreateProvider(handler);

		OllamaToolCall berlinCall = new(
			new OllamaToolCallFunction(
				"get_weather",
				Description: null,
				Arguments: new JsonObject { ["city"] = "Berlin" }),
			Id: "call_berlin");
		OllamaToolCall hamburgCall = new(
			new OllamaToolCallFunction(
				"get_weather",
				Description: null,
				Arguments: new JsonObject { ["city"] = "Hamburg" }),
			Id: "call_hamburg");
		OllamaChatRequest request = new(
			"client-model",
			[
				new OllamaChatMessage("user", "Weather in Berlin and Hamburg?"),
				new OllamaChatMessage("assistant", string.Empty, ToolCalls: [berlinCall, hamburgCall]),
				new OllamaChatMessage("tool", "22C", ToolName: "get_weather", ToolCallId: "call_berlin"),
				new OllamaChatMessage("tool", "9C", ToolName: "get_weather", ToolCallId: "call_hamburg")
			]);

		// Act
		await sut.CompleteChatAsync(
			new BackendContext(BackendName),
			"qwen3",
			request,
			pinnedEffort: null,
			CancellationToken.None);

		// Assert: the wire body preserves the order and, crucially, the id band on both legs.
		JsonArray messages = JsonNode.Parse(handler.LastRequestBody!)!.AsObject()["messages"]!.AsArray();

		// The assistant turn keeps each call's id alongside its city.
		JsonArray assistantCalls = messages[1]!["tool_calls"]!.AsArray();
		Assert.Equal("call_berlin", assistantCalls[0]!["id"]!.GetValue<string>());
		Assert.Contains("Berlin", assistantCalls[0]!["function"]!["arguments"]!.GetValue<string>());
		Assert.Equal("call_hamburg", assistantCalls[1]!["id"]!.GetValue<string>());
		Assert.Contains("Hamburg", assistantCalls[1]!["function"]!["arguments"]!.GetValue<string>());

		// Each tool result is correlated to its originating call by tool_call_id, not by the shared name.
		JsonObject berlinResult = messages[2]!.AsObject();
		Assert.Equal("call_berlin", berlinResult["tool_call_id"]!.GetValue<string>());
		Assert.Equal("22C", berlinResult["content"]!.GetValue<string>());

		JsonObject hamburgResult = messages[3]!.AsObject();
		Assert.Equal("call_hamburg", hamburgResult["tool_call_id"]!.GetValue<string>());
		Assert.Equal("9C", hamburgResult["content"]!.GetValue<string>());
	}

	#endregion

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
	/// Reports every probe as inconclusive so discovery does not depend on probing internals; these tests
	/// exercise only the context-length projection, not capability determination.
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
