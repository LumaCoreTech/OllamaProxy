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
using OllamaProxy.Providers.OpenAi;
using OllamaProxy.Providers.OpenAiProtocol;

namespace OllamaProxy.Tests.Integration;

/// <summary>
/// Integration tests exercising <see cref="OpenAiProvider"/> end to end against a mock OpenAI-compatible
/// backend. A canned <see cref="HttpMessageHandler"/> stands in for the upstream so each test drives the
/// full path — request mapping, HTTP transport, and response mapping — without a live network. The story
/// covers a non-streaming completion, an SSE stream, embeddings, strict OpenAI model discovery (only
/// <c>id</c> and <c>created</c>, ignoring capability metadata), and upstream-error translation into a
/// <see cref="ProviderException"/>. Provider-specific discovery dialects live in the respective
/// <c>VllmProviderIntegrationTests</c>, <c>OpenRouterProviderIntegrationTests</c>, and
/// <c>VeniceProviderIntegrationTests</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OpenAiProviderIntegrationTests
{
	private const string BackendName = "mock";

	private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
		{ Content = new StringContent(body, Encoding.UTF8, "application/json") };

	private static OpenAiProvider CreateProvider(ScriptedHandler handler) =>
		CreateProvider(handler, new RequestTraceAccessor());

	private static OpenAiProvider CreateProvider(ScriptedHandler handler, IRequestTraceAccessor traceAccessor) => new(
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
						BaseUrl = "https://mock.test/v1/", ProviderType = "openai", ApiKey = "test-key-1234"
					}
				}
			}),
		traceAccessor,
		TestReasoningDetailsCache.CreateDefault(),
		NullLogger<OpenAiProvider>.Instance);

	private static OpenAiProvider CreateProvider(ScriptedHandler handler, ICapabilityProber prober) => new(
		new StubHttpClientProvider(handler),
		prober,
		TimeProvider.System,
		Options.Create(
			new ProxyOptions
			{
				Backends =
				{
					[BackendName] = new BackendOptions
					{
						BaseUrl = "https://mock.test/v1/", ProviderType = "openai", ApiKey = "test-key-1234"
					}
				}
			}),
		new RequestTraceAccessor(),
		TestReasoningDetailsCache.CreateDefault(),
		NullLogger<OpenAiProvider>.Instance);

	/// <summary>
	/// Publishes a fresh <see cref="TraceScope"/> over a new <see cref="RequestTrace"/> on the ambient
	/// <see cref="RequestTraceAccessor"/> and returns both, so a test can drive the provider and then
	/// inspect the recorded entries. The accessor is <see cref="AsyncLocal{T}"/>-backed; setting it from
	/// this synchronous helper leaves the scope visible to the test's subsequent <c>await</c> calls.
	/// </summary>
	/// <returns>The trace-aware accessor and the underlying trace it records into.</returns>
	private static (IRequestTraceAccessor Accessor, RequestTrace Trace) CreateTrace()
	{
		RequestTrace trace = new("corr-1", DateTimeOffset.UnixEpoch, "POST", "/api/chat");
		RequestTraceAccessor accessor = new();
		accessor.Set(new TraceScope(trace, maxBodyBytes: 64 * 1024, redactAttachments: true, TimeProvider.System));

		return (accessor, trace);
	}

	/// <summary>
	/// Verifies that a non-streaming completion is posted to <c>chat/completions</c> with the resolved
	/// upstream model and that the upstream answer is mapped back into an Ollama response.
	/// </summary>
	[Fact]
	public async Task CompleteChatAsync_AgainstMockBackend_PostsRequestAndMapsResponse()
	{
		// Arrange
		const string responseBody = """
		                            {"id":"c1","model":"gpt-4o","choices":[{"index":0,"message":{"role":"assistant","content":"Hi there"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}
		                            """;
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		OpenAiProvider sut = CreateProvider(handler);
		OllamaChatRequest request = new("client-model", [new OllamaChatMessage("user", "hello")]);

		// Act
		OllamaChatResponse result = await sut.CompleteChatAsync(
			                            new BackendContext(BackendName),
			                            "openai/gpt-4o",
			                            request,
			                            pinnedEffort: null,
			                            CancellationToken.None);

		// Assert: the proxy targeted the right path/model and surfaced the mapped answer.
		Assert.EndsWith("chat/completions", handler.LastRequest!.RequestUri!.AbsolutePath, StringComparison.Ordinal);
		Assert.Contains("\"openai/gpt-4o\"", handler.LastRequestBody);
		Assert.Equal("client-model", result.Model);
		Assert.Equal("Hi there", result.Message.Content);
		Assert.True(result.Done);
		Assert.Equal(3, result.PromptEvalCount);
		Assert.Equal(2, result.EvalCount);
	}

	/// <summary>
	/// Verifies that a non-streaming completion whose <c>choices</c> array is omitted entirely (a
	/// non-conforming backend returning <c>{"id":…,"usage":…}</c> on a 2xx) maps to an empty assistant
	/// message with the default <c>stop</c> reason rather than dereferencing the missing collection and
	/// surfacing an unmapped error. The reported usage still flows through.
	/// </summary>
	[Fact]
	public async Task CompleteChatAsync_WhenChoicesOmitted_ReturnsAssistantDefaults()
	{
		// Arrange: a 2xx completion body that carries no "choices" key at all.
		const string responseBody = """
		                            {"id":"c1","model":"gpt-4o","usage":{"prompt_tokens":3,"completion_tokens":0,"total_tokens":3}}
		                            """;
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		OpenAiProvider sut = CreateProvider(handler);
		OllamaChatRequest request = new("client-model", [new OllamaChatMessage("user", "hello")]);

		// Act
		OllamaChatResponse result = await sut.CompleteChatAsync(
			                            new BackendContext(BackendName),
			                            "gpt-4o",
			                            request,
			                            pinnedEffort: null,
			                            CancellationToken.None);

		// Assert: empty assistant message and default reason, with the backend's usage preserved.
		Assert.Equal("assistant", result.Message.Role);
		Assert.Equal(string.Empty, result.Message.Content);
		Assert.Equal("stop", result.DoneReason);
		Assert.True(result.Done);
		Assert.Equal(3, result.PromptEvalCount);
	}

	/// <summary>
	/// Verifies that the generic OpenAI provider strips the non-standard <c>top_k</c> and <c>min_p</c>
	/// sampling extensions from the outgoing body even when the client supplies them, so a strict OpenAI
	/// backend never receives a field it would reject. The supporting providers keep these fields; that
	/// counterpart is verified in <c>VllmProviderIntegrationTests</c>.
	/// </summary>
	[Fact]
	public async Task CompleteChatAsync_WhenClientSuppliesTopKAndMinP_OmitsThemFromStrictOpenAiRequest()
	{
		// Arrange: the client sets both non-standard extensions; the strict provider must drop them.
		const string responseBody = """
		                            {"id":"c1","model":"gpt-4o","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
		                            """;
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		OpenAiProvider sut = CreateProvider(handler);
		OllamaChatRequest request = new(
			"client-model",
			[new OllamaChatMessage("user", "hello")],
			Options: new OllamaOptions(TopK: 20, MinP: 0.1));

		// Act
		await sut.CompleteChatAsync(
			new BackendContext(BackendName),
			"gpt-4o",
			request,
			pinnedEffort: null,
			CancellationToken.None);

		// Assert: neither extension reached the wire, though the standard fields would have.
		JsonObject body = JsonNode.Parse(handler.LastRequestBody!)!.AsObject();
		Assert.False(body.ContainsKey("top_k"));
		Assert.False(body.ContainsKey("min_p"));
	}

	/// <summary>
	/// Verifies that a streamed completion is parsed from the SSE body into incremental Ollama chunks
	/// followed by a terminal done chunk carrying the usage accounting.
	/// </summary>
	[Fact]
	public async Task StreamChatAsync_AgainstMockBackend_TranslatesSseIntoOllamaChunks()
	{
		// Arrange: two content deltas and a terminal usage-only event, framed as OpenAI SSE.
		string sse = string.Join(
			"\n",
			"""data: {"id":"c1","model":"gpt-4o","choices":[{"index":0,"delta":{"role":"assistant","content":"Hel"},"finish_reason":null}]}""",
			"""data: {"id":"c1","model":"gpt-4o","choices":[{"index":0,"delta":{"content":"lo"},"finish_reason":"stop"}]}""",
			"""data: {"id":"c1","model":"gpt-4o","choices":[],"usage":{"prompt_tokens":4,"completion_tokens":6,"total_tokens":10}}""",
			"data: [DONE]",
			string.Empty);
		ScriptedHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
		});
		OpenAiProvider sut = CreateProvider(handler);
		OllamaChatRequest request = new("client-model", [new OllamaChatMessage("user", "hi")], Stream: true);

		// Act
		List<OllamaChatResponse> chunks = [];
		await foreach (OllamaChatResponse chunk in sut.StreamChatAsync(
			               new BackendContext(BackendName),
			               "gpt-4o",
			               request,
			               pinnedEffort: null,
			               CancellationToken.None))
		{
			chunks.Add(chunk);
		}

		// Assert: two content chunks plus the terminal chunk with usage.
		Assert.Equal(3, chunks.Count);
		Assert.Equal("Hel", chunks[0].Message.Content);
		Assert.Equal("lo", chunks[1].Message.Content);
		Assert.True(chunks[2].Done);
		Assert.Equal(4, chunks[2].PromptEvalCount);
		Assert.Equal(6, chunks[2].EvalCount);
	}

	/// <summary>
	/// Verifies that a streamed completion records a single aggregated <see cref="TraceStage.BackendResponse"/>
	/// entry carrying the composed assistant text rather than one entry per wire chunk, so a trace reads as
	/// the model's answer instead of its per-token framing.
	/// </summary>
	[Fact]
	public async Task StreamChatAsync_WhenTraced_RecordsAggregatedBackendResponse()
	{
		// Arrange: two content deltas, a usage-only event, and the sentinel.
		string sse = string.Join(
			"\n",
			"""data: {"id":"c1","model":"gpt-4o","choices":[{"index":0,"delta":{"role":"assistant","content":"Hel"},"finish_reason":null}]}""",
			"""data: {"id":"c1","model":"gpt-4o","choices":[{"index":0,"delta":{"content":"lo"},"finish_reason":"stop"}]}""",
			"""data: {"id":"c1","model":"gpt-4o","choices":[],"usage":{"prompt_tokens":4,"completion_tokens":6,"total_tokens":10}}""",
			"data: [DONE]",
			string.Empty);
		ScriptedHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
		});
		(IRequestTraceAccessor accessor, RequestTrace trace) = CreateTrace();
		OpenAiProvider sut = CreateProvider(handler, accessor);
		OllamaChatRequest request = new("client-model", [new OllamaChatMessage("user", "hi")], Stream: true);

		// Act: the stream must be drained fully so the provider's finally records the assembled text.
		await foreach (OllamaChatResponse _ in sut.StreamChatAsync(
			               new BackendContext(BackendName),
			               "gpt-4o",
			               request,
			               pinnedEffort: null,
			               CancellationToken.None))
		{
			// Drain only — the assertion is on the recorded trace, not the yielded chunks.
		}

		// Assert: exactly one BackendResponse entry carrying the concatenation of the two deltas.
		TraceEntry response = Assert.Single(trace.Entries, e => e.Stage == TraceStage.BackendResponse);
		Assert.Equal("Hello", response.Body);
	}

	/// <summary>
	/// Verifies that a streamed completion whose deltas carry <c>reasoning_content</c> surfaces the
	/// chain-of-thought to the client as Ollama's native <c>thinking</c> field and records it under a
	/// distinct <see cref="TraceStage.BackendReasoning"/> entry, keeping the visible answer separate.
	/// </summary>
	[Fact]
	public async Task StreamChatAsync_WhenTracedWithReasoning_RecordsAggregatedBackendReasoning()
	{
		// Arrange: each content delta is preceded by a reasoning delta, as a reasoning model streams it.
		string sse = string.Join(
			"\n",
			"""data: {"id":"c1","model":"gpt-4o","choices":[{"index":0,"delta":{"role":"assistant","reasoning_content":"Think"},"finish_reason":null}]}""",
			"""data: {"id":"c1","model":"gpt-4o","choices":[{"index":0,"delta":{"content":"Hel"},"finish_reason":null}]}""",
			"""data: {"id":"c1","model":"gpt-4o","choices":[{"index":0,"delta":{"reasoning_content":"ing","content":"lo"},"finish_reason":"stop"}]}""",
			"""data: {"id":"c1","model":"gpt-4o","choices":[],"usage":{"prompt_tokens":4,"completion_tokens":6,"total_tokens":10}}""",
			"data: [DONE]",
			string.Empty);
		ScriptedHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
		});
		(IRequestTraceAccessor accessor, RequestTrace trace) = CreateTrace();
		OpenAiProvider sut = CreateProvider(handler, accessor);
		OllamaChatRequest request = new("client-model", [new OllamaChatMessage("user", "hi")], Stream: true);

		// Act: capture the yielded chunks to assert client-visible thinking, and drain fully so the
		// provider's finally records both assembled buffers.
		List<OllamaChatResponse> chunks = [];
		await foreach (OllamaChatResponse chunk in sut.StreamChatAsync(
			               new BackendContext(BackendName),
			               "gpt-4o",
			               request,
			               pinnedEffort: null,
			               CancellationToken.None))
		{
			chunks.Add(chunk);
		}

		// Assert: the client sees the reasoning surfaced as native thinking chunks, separate from content.
		Assert.Equal("Think", Assert.Single(chunks, c => c.Message.Thinking == "Think").Message.Thinking);
		Assert.Equal("ing", Assert.Single(chunks, c => c.Message.Thinking == "ing").Message.Thinking);

		// And the trace isolates the aggregated reasoning from the aggregated visible answer.
		TraceEntry reasoning = Assert.Single(trace.Entries, e => e.Stage == TraceStage.BackendReasoning);
		Assert.Equal("Thinking", reasoning.Body);
		TraceEntry response = Assert.Single(trace.Entries, e => e.Stage == TraceStage.BackendResponse);
		Assert.Equal("Hello", response.Body);
	}

	/// <summary>
	/// Verifies that a non-streaming completion whose message carries <c>reasoning_content</c> surfaces
	/// the chain-of-thought to the client as Ollama's native <c>thinking</c> field and records it under a
	/// distinct <see cref="TraceStage.BackendReasoning"/> entry.
	/// </summary>
	[Fact]
	public async Task CompleteChatAsync_WhenTracedWithReasoning_RecordsBackendReasoning()
	{
		// Arrange: the upstream message carries both visible content and a separate reasoning channel.
		const string responseBody = """
		                            {"id":"c1","model":"gpt-4o","choices":[{"index":0,"message":{"role":"assistant","content":"Hi there","reasoning_content":"Thinking hard"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}
		                            """;
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		(IRequestTraceAccessor accessor, RequestTrace trace) = CreateTrace();
		OpenAiProvider sut = CreateProvider(handler, accessor);
		OllamaChatRequest request = new("client-model", [new OllamaChatMessage("user", "hello")]);

		// Act
		OllamaChatResponse result = await sut.CompleteChatAsync(
			                            new BackendContext(BackendName),
			                            "openai/gpt-4o",
			                            request,
			                            pinnedEffort: null,
			                            CancellationToken.None);

		// Assert: the client sees the reasoning as native thinking, separate from the visible content.
		Assert.Equal("Hi there", result.Message.Content);
		Assert.Equal("Thinking hard", result.Message.Thinking);

		// And the trace isolates the reasoning into its own stage while the full JSON lands in the response.
		TraceEntry reasoning = Assert.Single(trace.Entries, e => e.Stage == TraceStage.BackendReasoning);
		Assert.Equal("Thinking hard", reasoning.Body);
		TraceEntry response = Assert.Single(trace.Entries, e => e.Stage == TraceStage.BackendResponse);
		Assert.Contains("Hi there", response.Body, StringComparison.Ordinal);
	}

	#region Parallel tool calls

	/// <summary>
	/// Verifies that two parallel calls to the <em>same</em> tool, returned in one non-streaming
	/// completion, each surface to the client carrying their own distinct call id. The id is the only
	/// thing that distinguishes the calls — both are <c>get_weather</c> — so dropping it would leave the
	/// client unable to tell which result belongs to which call. This is the response-side guard for the
	/// parallel-tool-call correlation the proxy must preserve.
	/// </summary>
	[Fact]
	public async Task CompleteChatAsync_WhenBackendReturnsParallelToolCalls_CarriesEachDistinctId()
	{
		// Arrange: one assistant turn calling get_weather twice (Berlin and Hamburg), each with its own id.
		const string responseBody = """
		                            {"id":"c1","model":"gpt-4o","choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_berlin","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Berlin\"}"}},{"id":"call_hamburg","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Hamburg\"}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":10,"completion_tokens":8,"total_tokens":18}}
		                            """;
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		OpenAiProvider sut = CreateProvider(handler);
		OllamaChatRequest request = new(
			"client-model",
			[new OllamaChatMessage("user", "Weather in Berlin and Hamburg?")]);

		// Act
		OllamaChatResponse result = await sut.CompleteChatAsync(
			                            new BackendContext(BackendName),
			                            "gpt-4o",
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
			"""data: {"id":"c1","model":"gpt-4o","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_berlin","type":"function","function":{"name":"get_weather","arguments":""}}]},"finish_reason":null}]}""",
			"""data: {"id":"c1","model":"gpt-4o","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"city\":\"Berlin\"}"}}]},"finish_reason":null}]}""",
			"""data: {"id":"c1","model":"gpt-4o","choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"id":"call_hamburg","type":"function","function":{"name":"get_weather","arguments":""}}]},"finish_reason":null}]}""",
			"""data: {"id":"c1","model":"gpt-4o","choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"function":{"arguments":"{\"city\":\"Hamburg\"}"}}]},"finish_reason":null}]}""",
			"""data: {"id":"c1","model":"gpt-4o","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
			"""data: {"id":"c1","model":"gpt-4o","choices":[],"usage":{"prompt_tokens":10,"completion_tokens":8,"total_tokens":18}}""",
			"data: [DONE]",
			string.Empty);
		ScriptedHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
		});
		OpenAiProvider sut = CreateProvider(handler);
		OllamaChatRequest request = new(
			"client-model",
			[new OllamaChatMessage("user", "Weather in Berlin and Hamburg?")],
			Stream: true);

		// Act
		List<OllamaChatResponse> chunks = [];
		await foreach (OllamaChatResponse chunk in sut.StreamChatAsync(
			               new BackendContext(BackendName),
			               "gpt-4o",
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
	/// calls and their two results, the outgoing OpenAI request preserves each assistant call's id and
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
		                            {"id":"c2","model":"gpt-4o","choices":[{"index":0,"message":{"role":"assistant","content":"Berlin 22C, Hamburg 9C"},"finish_reason":"stop"}],"usage":{"prompt_tokens":20,"completion_tokens":10,"total_tokens":30}}
		                            """;
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		OpenAiProvider sut = CreateProvider(handler);

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
			"gpt-4o",
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

	/// <summary>
	/// Verifies that an embeddings call posts to <c>embeddings</c> and projects the returned vectors,
	/// preserving their input order via the reported index.
	/// </summary>
	[Fact]
	public async Task CreateEmbeddingsAsync_AgainstMockBackend_ProjectsVectorsInOrder()
	{
		// Arrange: data returned out of order to prove the provider reorders by index.
		const string responseBody = """
		                            {"model":"text-embedding-3-small","data":[{"index":1,"embedding":[0.3,0.4]},{"index":0,"embedding":[0.1,0.2]}],"usage":{"prompt_tokens":8}}
		                            """;
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		OpenAiProvider sut = CreateProvider(handler);
		OllamaEmbedRequest request = new("client-embed", JsonValue.Create("hello"));

		// Act
		OllamaEmbedResponse result = await sut.CreateEmbeddingsAsync(
			                             new BackendContext(BackendName),
			                             "text-embedding-3-small",
			                             request,
			                             CancellationToken.None);

		// Assert
		Assert.EndsWith("embeddings", handler.LastRequest!.RequestUri!.AbsolutePath, StringComparison.Ordinal);
		Assert.Equal("client-embed", result.Model);
		Assert.Equal(2, result.Embeddings.Count);
		Assert.Equal([0.1f, 0.2f], result.Embeddings[0]);
		Assert.Equal([0.3f, 0.4f], result.Embeddings[1]);
		Assert.Equal(8, result.PromptEvalCount);
	}

	/// <summary>
	/// Verifies that an embeddings response whose <c>data</c> array is omitted entirely (a non-conforming
	/// backend returning <c>{"model":…}</c> on a 2xx) projects to an empty vector list rather than
	/// dereferencing the missing collection and surfacing an unmapped error. The shared serializer does not
	/// enforce the non-nullable annotation at runtime, so the missing key really does arrive as null.
	/// </summary>
	[Fact]
	public async Task CreateEmbeddingsAsync_WhenDataOmitted_ProjectsEmptyVectors()
	{
		// Arrange: a 2xx embeddings body that carries no "data" key at all.
		const string responseBody = """{"model":"text-embedding-3-small","usage":{"prompt_tokens":8}}""";
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		OpenAiProvider sut = CreateProvider(handler);
		OllamaEmbedRequest request = new("client-embed", JsonValue.Create("hello"));

		// Act
		OllamaEmbedResponse result = await sut.CreateEmbeddingsAsync(
			                             new BackendContext(BackendName),
			                             "text-embedding-3-small",
			                             request,
			                             CancellationToken.None);

		// Assert: no vectors, but the usage the backend did report still flows through.
		Assert.Empty(result.Embeddings);
		Assert.Equal(8, result.PromptEvalCount);
	}

	/// <summary>
	/// Verifies that strict OpenAI discovery reads the <c>models</c> listing and projects only the
	/// stable identity fields — <c>id</c> and <c>created</c> — deliberately ignoring any capability or
	/// context metadata a backend might include, because the official OpenAI API advertises none.
	/// </summary>
	[Fact]
	public async Task DiscoverModelsAsync_AgainstMockBackend_ProjectsOnlyIdAndCreated()
	{
		// Arrange: a listing that even carries capability metadata, which strict OpenAI must ignore.
		const string responseBody = """
		                            {"data":[{"id":"gpt-4o","created":1,"architecture":{"input_modalities":["text","image"]},"supported_parameters":["tools"]}]}
		                            """;
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		OpenAiProvider sut = CreateProvider(handler);

		// Act
		IReadOnlyList<DiscoveredModel> models = await sut.DiscoverModelsAsync(
			                                        new BackendContext(BackendName),
			                                        CancellationToken.None);

		// Assert: only id and created are projected; capability metadata is deliberately dropped.
		Assert.EndsWith("models", handler.LastRequest!.RequestUri!.AbsolutePath, StringComparison.Ordinal);
		DiscoveredModel model = Assert.Single(models);
		Assert.Equal("gpt-4o", model.Id);
		Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1), model.Created);
		Assert.Null(model.Capabilities);
		Assert.Null(model.ContextLength);
	}

	/// <summary>
	/// Verifies that a minimal OpenAI listing — only an <c>id</c>, no context or capability metadata —
	/// projects to a model with no context length, the case the catalog builder later skips or backfills
	/// from configuration.
	/// </summary>
	[Fact]
	public async Task DiscoverModelsAsync_WhenBackendReportsNoContext_ProjectsNullContextLength()
	{
		// Arrange: a bare OpenAI-style listing (OpenAI itself reports neither context nor capabilities).
		const string responseBody = """{"data":[{"id":"gpt-4o"}]}""";
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		OpenAiProvider sut = CreateProvider(handler);

		// Act
		IReadOnlyList<DiscoveredModel> models = await sut.DiscoverModelsAsync(
			                                        new BackendContext(BackendName),
			                                        CancellationToken.None);

		// Assert
		DiscoveredModel model = Assert.Single(models);
		Assert.Null(model.ContextLength);
		Assert.Null(model.Capabilities);
	}

	/// <summary>
	/// Verifies that a model listing whose <c>data</c> array is omitted entirely (a non-conforming backend
	/// returning <c>{}</c> on a 2xx) projects to an empty model list rather than dereferencing the missing
	/// collection. The discovery seam treats the absent array as “backend reports no models”.
	/// </summary>
	[Fact]
	public async Task DiscoverModelsAsync_WhenDataOmitted_ReturnsEmptyListing()
	{
		// Arrange: a 2xx listing body that carries no "data" key at all.
		const string responseBody = "{}";
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, responseBody));
		OpenAiProvider sut = CreateProvider(handler);

		// Act
		IReadOnlyList<DiscoveredModel> models = await sut.DiscoverModelsAsync(
			                                        new BackendContext(BackendName),
			                                        CancellationToken.None);

		// Assert
		Assert.Empty(models);
	}

	/// <summary>
	/// Verifies that an upstream error response is translated into a <see cref="ProviderException"/>
	/// carrying the backend's status code and the message from the OpenAI error envelope.
	/// </summary>
	[Fact]
	public async Task CompleteChatAsync_WhenBackendReturnsError_ThrowsProviderExceptionWithStatus()
	{
		// Arrange
		const string errorBody = """{"error":{"message":"invalid api key","type":"auth_error"}}""";
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.Unauthorized, errorBody));
		OpenAiProvider sut = CreateProvider(handler);
		OllamaChatRequest request = new("client-model", [new OllamaChatMessage("user", "hi")]);

		// Act + Assert
		var exception = await Assert.ThrowsAsync<ProviderException>(() =>
			                sut.CompleteChatAsync(
				                new BackendContext(BackendName),
				                "gpt-4o",
				                request,
				                pinnedEffort: null,
				                CancellationToken.None));
		Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
		Assert.Contains("invalid api key", exception.Message, StringComparison.Ordinal);
	}

	/// <summary>
	/// Verifies that a very large upstream error body is length-capped before it is folded into the
	/// <see cref="ProviderException"/> message, so a misconfigured or hostile backend cannot bloat the
	/// proxy's error message and the logs that record it. A non-envelope body (raw text) is used so the
	/// raw fallback path — the one that previously copied the body verbatim — is the branch under test.
	/// </summary>
	[Fact]
	public async Task CompleteChatAsync_WhenErrorBodyHuge_TruncatesDetailInProviderException()
	{
		// Arrange: a 2 KB raw (non-envelope) error body, well past the 500-char diagnostic cap.
		string hugeBody = new('x', 2_000);
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.BadGateway, hugeBody));
		OpenAiProvider sut = CreateProvider(handler);
		OllamaChatRequest request = new("client-model", [new OllamaChatMessage("user", "hi")]);

		// Act + Assert
		var exception = await Assert.ThrowsAsync<ProviderException>(() =>
			                sut.CompleteChatAsync(
				                new BackendContext(BackendName),
				                "gpt-4o",
				                request,
				                pinnedEffort: null,
				                CancellationToken.None));

		// The truncation marker is present and the message stays far below the raw body length.
		Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
		Assert.Contains("… (truncated)", exception.Message, StringComparison.Ordinal);
		Assert.True(
			exception.Message.Length < 700,
			$"Expected the capped detail to keep the message short, but it was {exception.Message.Length} chars.");
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiProvider.DetermineCapabilitiesAsync"/> merges every conclusive probe
	/// result into the final capability set: a model the backend confirms for completion, tools, and vision
	/// surfaces all three, not just completion. This is the regression guard for the bug where concurrent
	/// probing let the heavier tool and vision probes time out (and a timeout is not retried), silently
	/// dropping a tool- and vision-capable model down to completion-only.
	/// </summary>
	[Fact]
	public async Task DetermineCapabilitiesAsync_WhenProbesConfirmCompletionToolsAndVision_MergesAllThree()
	{
		// Arrange: a prober that confirms completion, tools, and vision (the Gemma-style model) and denies
		// embeddings. The HTTP handler is never hit because probing is delegated to the injected prober.
		RecordingCapabilityProber prober = new(completion: true, tools: true, vision: true, embeddings: false);
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, "{}"));
		OpenAiProvider sut = CreateProvider(handler, prober);

		// Act
		ModelCapabilities capabilities = await sut.DetermineCapabilitiesAsync(
			                                 new BackendContext(BackendName),
			                                 new DiscoveredModel("gemma"),
			                                 CancellationToken.None);

		// Assert: every confirmed capability is carried, none is lost in the merge.
		Assert.True(capabilities.SupportsCompletion);
		Assert.True(capabilities.SupportsTools);
		Assert.True(capabilities.SupportsVision);
		Assert.False(capabilities.SupportsEmbeddings);
		Assert.Equal(CapabilitySource.Probed, capabilities.Source);
	}

	/// <summary>
	/// Verifies that the capability probes run <em>sequentially</em> with the completion probe first. The
	/// completion probe loads the model into memory so the tool, vision, and embedding probes that follow run
	/// against a warm, idle model and each gets the full per-attempt timeout to itself — the fix for the
	/// concurrent-probing race that made the heavier probes time out against a still-cold model.
	/// </summary>
	[Fact]
	public async Task DetermineCapabilitiesAsync_WhenProbing_RunsProbesSequentiallyCompletionFirst()
	{
		// Arrange: a prober that records the order its probes are invoked and proves no two overlap.
		RecordingCapabilityProber prober = new(completion: true, tools: true, vision: true, embeddings: true);
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK, "{}"));
		OpenAiProvider sut = CreateProvider(handler, prober);

		// Act
		await sut.DetermineCapabilitiesAsync(
			new BackendContext(BackendName),
			new DiscoveredModel("gemma"),
			CancellationToken.None);

		// Assert: completion is probed first (it warms the model), and the probes never overlapped.
		Assert.Equal(["completion", "tool", "vision", "embedding"], prober.InvocationOrder);
		Assert.Equal(0, prober.MaxConcurrency);
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
	/// Reports every probe as inconclusive so discovery does not depend on probing internals; these tests
	/// exercise request/response mapping and discovery projection, not capability determination.
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

	/// <summary>
	/// A prober that returns the configured per-capability answers while recording the order its probes were
	/// invoked and the peak number running at once. It lets a test assert both that every conclusive result is
	/// merged and that the probes run sequentially (completion first), without a live backend.
	/// </summary>
	private sealed class RecordingCapabilityProber(
		bool completion,
		bool tools,
		bool vision,
		bool embeddings)
		: ICapabilityProber
	{
		private readonly Lock         mGate            = new();
		private readonly List<string> mInvocationOrder = [];
		private          int          mActive;

		/// <summary>Gets the capability labels in the order their probes were invoked.</summary>
		public IReadOnlyList<string> InvocationOrder => mInvocationOrder;

		/// <summary>Gets the highest number of probes observed running concurrently (0 when fully sequential).</summary>
		public int MaxConcurrency { get; private set; }

		public Task<bool?> ProbeCompletionSupportAsync(
			BackendContext    backend,
			string            modelId,
			CancellationToken cancellationToken) => RecordAsync("completion", completion);

		public Task<bool?> ProbeToolSupportAsync(
			BackendContext    backend,
			string            modelId,
			CancellationToken cancellationToken) => RecordAsync("tool", tools);

		public Task<bool?> ProbeVisionSupportAsync(
			BackendContext    backend,
			string            modelId,
			CancellationToken cancellationToken) => RecordAsync("vision", vision);

		public Task<bool?> ProbeEmbeddingSupportAsync(
			BackendContext    backend,
			string            modelId,
			CancellationToken cancellationToken) => RecordAsync("embedding", embeddings);

		// Records the invocation order and tracks overlap: MaxConcurrency stays 0 only if no second probe ever
		// enters while another is active, which a sequential awaiter guarantees and a concurrent fan-out breaks.
		private async Task<bool?> RecordAsync(string capability, bool result)
		{
			lock (mGate)
			{
				mInvocationOrder.Add(capability);
				mActive++;
				MaxConcurrency = Math.Max(MaxConcurrency, mActive - 1);
			}

			await Task.Yield();

			lock (mGate)
			{
				mActive--;
			}

			return result;
		}
	}
}
