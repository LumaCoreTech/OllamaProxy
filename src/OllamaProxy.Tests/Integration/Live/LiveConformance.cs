// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json.Nodes;

using OllamaProxy.Contracts.Ollama;
using OllamaProxy.Providers.Abstractions;
using OllamaProxy.Providers.OpenAiProtocol;

namespace OllamaProxy.Tests.Integration.Live;

/// <summary>
/// The shared conformance harness for the live provider suite. It drives the <em>real</em>
/// <see cref="IProviderAdapter"/> against a live backend — the same public surface production routes through —
/// and asserts the invariants that must hold regardless of the model's non-deterministic wording: the call
/// completes without a <see cref="ProviderException"/> (a mistranslated request would surface as a backend
/// 4xx turned into one), the response maps cleanly into the Ollama contracts, tool-call arguments arrive as a
/// structured JSON <em>object</em> (Ollama's shape, not OpenAI's string), and a streamed response terminates
/// with a <c>done</c> chunk. Each backend's test class supplies a provider and its <see cref="LiveBackendConfig"/>;
/// the per-knob methods here own the request construction and the assertions, so the two backend files stay
/// thin and the conformance contract lives in exactly one place.
/// <para>
/// What this harness deliberately does <em>not</em> assert: the exact text, token counts, or which tool the
/// model chose to call. Those are non-deterministic. It asserts only contract and shape — what the proxy
/// guarantees — so a passing run means "our provider code speaks every feature correctly with this backend",
/// not "the model said X".
/// </para>
/// </summary>
static class LiveConformance
{
	// A tiny prompt keeps the live call cheap and fast while still exercising the full translation pipeline.
	private const string TrivialPrompt = "Reply with the single word: pong.";

	// A small 64x64 PNG with real, visible content — a blue square on white with an orange circle (base64, no
	// data: prefix — the proxy adds the data URL wrapper). A degenerate 1x1 transparent pixel is rejected by
	// real vision backends ("not a valid image"), so the fixture carries genuine dimensions and content while
	// still being tiny enough to keep the live call cheap. Exercises the multimodal image-part mapping
	// (Ollama images to OpenAI image_url parts) without depending on any external asset.
	private const string SampleImagePngBase64 =
		"iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZ" +
		"cwAADsMAAA7DAcdvqGQAAAFbSURBVHhe7dDRacQwFERRV5fC0ks6SH8bdiE/B8NItiwQehfuz4BHfnO8Nucw2I0awGA" +
		"3agCD3agBDHYjDvD1/bu0iRrAQCxczUQNYCAWrmaiBjAQC1czUQMYiIWrmagBDMTC1UzUAAZi4Wompg/w+jmifnPHxLQB" +
		"PLJFO66YmDKAh/VoV6+JxwfwoCva2WPi0QE85I52t5p4bAAPGKFvtJioAQzEwhb98ZH6VjJRAxiIhS360yP1rWSiBjAQ" +
		"C5P+8Gh9L5kYPsDn0ZMfH6VvJRM1gIFY2KI/PVLfSiZqAAOxsFV/fIS+0WKiBjAQC3v0gDva3Wri0QE+P3ByTK929ph4" +
		"fIC3HtSjXb0mpgzw1sNatOOKiWkD/OuRZ/rNHRPTB5htogYwEAtXM1EDGIiFq5moAQzEwtVM1AAGYuFqJmoAA7FwNRM1" +
		"gMFu1AAGu1EDGOxGDWCwG9sP8AfL/X5f2orKNAAAAABJRU5ErkJggg==";

	/// <summary>
	/// Drives a non-streaming chat completion through the real adapter and asserts the proxy returned a
	/// well-formed, terminal Ollama response echoing the client model — proving request translation was
	/// accepted and the response mapped back cleanly.
	/// </summary>
	/// <param name="provider">The provider under test.</param>
	/// <param name="config">The resolved live backend configuration.</param>
	/// <param name="cancellationToken">A token to cancel the operation.</param>
	public static async Task AssertChatCompletesAsync(
		IProviderAdapter  provider,
		LiveBackendConfig config,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(provider);
		ArgumentNullException.ThrowIfNull(config);

		OllamaChatRequest request = new(config.ChatModel, [new OllamaChatMessage("user", TrivialPrompt)]);

		OllamaChatResponse response = await provider.CompleteChatAsync(
			                              new BackendContext(config.BackendName),
			                              config.ChatModel,
			                              request,
			                              pinnedEffort: null,
			                              cancellationToken);

		Assert.Equal(config.ChatModel, response.Model);
		Assert.True(response.Done, "A non-streaming completion must be marked done.");
		Assert.Equal("assistant", response.Message.Role);
		Assert.False(
			string.IsNullOrWhiteSpace(response.Message.Content),
			"A plain chat completion must carry non-empty assistant content.");
	}

	/// <summary>
	/// Drives a streaming chat completion through the real adapter and asserts the stream yields at least one
	/// chunk and terminates with exactly one <c>done</c> chunk — proving the SSE translation pipeline runs
	/// end to end and the terminal chunk is produced.
	/// </summary>
	/// <param name="provider">The provider under test.</param>
	/// <param name="config">The resolved live backend configuration.</param>
	/// <param name="cancellationToken">A token to cancel the operation.</param>
	public static async Task AssertStreamCompletesAsync(
		IProviderAdapter  provider,
		LiveBackendConfig config,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(provider);
		ArgumentNullException.ThrowIfNull(config);

		OllamaChatRequest request = new(
			config.ChatModel,
			[new OllamaChatMessage("user", TrivialPrompt)],
			Stream: true);

		int chunkCount = 0;
		int doneCount = 0;
		string aggregatedContent = string.Empty;

		await foreach (OllamaChatResponse chunk in provider.StreamChatAsync(
			               new BackendContext(config.BackendName),
			               config.ChatModel,
			               request,
			               pinnedEffort: null,
			               cancellationToken))
		{
			chunkCount++;
			aggregatedContent += chunk.Message.Content;
			if (chunk.Done) doneCount++;
		}

		Assert.True(chunkCount > 0, "A streamed completion must yield at least one chunk.");
		Assert.Equal(1, doneCount);
		Assert.False(
			string.IsNullOrWhiteSpace(aggregatedContent),
			"The streamed chunks must aggregate to non-empty assistant content.");
	}

	/// <summary>
	/// Drives a tool-advertising chat completion through the real adapter and asserts that whenever the model
	/// chooses to call the advertised function, the proxy surfaces the call with a structured JSON-object
	/// argument payload (Ollama's contract) rather than OpenAI's argument string. When the model answers in
	/// prose instead of calling — a legitimate, non-deterministic choice — the call still conformed, so the
	/// test asserts the request was accepted and the response mapped cleanly.
	/// </summary>
	/// <param name="provider">The provider under test.</param>
	/// <param name="config">The resolved live backend configuration.</param>
	/// <param name="cancellationToken">A token to cancel the operation.</param>
	public static async Task AssertToolUseConformsAsync(
		IProviderAdapter  provider,
		LiveBackendConfig config,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(provider);
		ArgumentNullException.ThrowIfNull(config);

		OllamaTool weatherTool = new(
			"function",
			new OllamaToolFunction(
				"get_weather",
				"Get the current weather for a city.",
				new JsonObject
				{
					["type"] = "object",
					["properties"] = new JsonObject
					{
						["city"] = new JsonObject { ["type"] = "string", ["description"] = "The city name." }
					},
					["required"] = new JsonArray("city")
				}));

		OllamaChatRequest request = new(
			config.ChatModel,
			[new OllamaChatMessage("user", "What is the weather in Berlin? Use the get_weather tool.")],
			Tools: [weatherTool]);

		OllamaChatResponse response = await provider.CompleteChatAsync(
			                              new BackendContext(config.BackendName),
			                              config.ChatModel,
			                              request,
			                              pinnedEffort: null,
			                              cancellationToken);

		// The request was accepted and mapped back — that alone proves tool-definition translation conforms.
		Assert.Equal(config.ChatModel, response.Model);
		Assert.True(response.Done, "A tool-advertising completion must still terminate with done.");

		// If the model exercised the tool, the decisive invariant is Ollama's argument shape: a JSON object.
		if (response.Message.ToolCalls is { Count: > 0 } toolCalls)
		{
			OllamaToolCall call = toolCalls[0];
			Assert.False(
				string.IsNullOrWhiteSpace(call.Function.Name),
				"A surfaced tool call must name the function.");
			Assert.IsType<JsonObject>(call.Function.Arguments);
		}
	}

	/// <summary>
	/// Drives a multimodal chat completion (a tiny inline image plus a question) through the real adapter and
	/// asserts the backend accepted the request and the proxy mapped a terminal answer back — proving the
	/// image-part translation (<c>images</c> to OpenAI <c>image_url</c> parts) conforms for this backend.
	/// </summary>
	/// <param name="provider">The provider under test.</param>
	/// <param name="config">The resolved live backend configuration; its vision model is used.</param>
	/// <param name="cancellationToken">A token to cancel the operation.</param>
	/// <exception cref="InvalidOperationException">
	/// <see cref="LiveBackendConfig.VisionModel"/> is <see langword="null"/>. The caller must gate on
	/// <see cref="LiveBackendConfig.SupportsVision"/> before invoking this method.
	/// </exception>
	public static async Task AssertVisionConformsAsync(
		IProviderAdapter  provider,
		LiveBackendConfig config,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(provider);
		ArgumentNullException.ThrowIfNull(config);

		string visionModel = config.VisionModel
		                     ?? throw new InvalidOperationException(
			                     "AssertVisionConformsAsync requires a wired vision model; gate on " +
			                     "LiveBackendConfig.SupportsVision before calling.");

		OllamaChatRequest request = new(
			visionModel,
			[
				new OllamaChatMessage(
					"user",
					"Describe this image in one short sentence.",
					Images: [SampleImagePngBase64])
			]);

		OllamaChatResponse response = await provider.CompleteChatAsync(
			                              new BackendContext(config.BackendName),
			                              visionModel,
			                              request,
			                              pinnedEffort: null,
			                              cancellationToken);

		Assert.Equal(visionModel, response.Model);
		Assert.True(response.Done, "A vision completion must terminate with done.");
		Assert.False(
			string.IsNullOrWhiteSpace(response.Message.Content),
			"A vision completion must carry a non-empty description.");
	}

	/// <summary>
	/// Drives an embeddings call through the real adapter and asserts the proxy returned a non-empty vector of
	/// finite components for the input — proving the embeddings request/response translation conforms.
	/// </summary>
	/// <param name="provider">The provider under test.</param>
	/// <param name="config">The resolved live backend configuration; its embedding model is used.</param>
	/// <param name="cancellationToken">A token to cancel the operation.</param>
	/// <exception cref="InvalidOperationException">
	/// <see cref="LiveBackendConfig.EmbeddingModel"/> is <see langword="null"/>. The caller must gate on
	/// <see cref="LiveBackendConfig.SupportsEmbeddings"/> before invoking this method.
	/// </exception>
	public static async Task AssertEmbeddingsConformAsync(
		IProviderAdapter  provider,
		LiveBackendConfig config,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(provider);
		ArgumentNullException.ThrowIfNull(config);

		string embeddingModel = config.EmbeddingModel
		                        ?? throw new InvalidOperationException(
			                        "AssertEmbeddingsConformAsync requires a wired embedding model; gate on " +
			                        "LiveBackendConfig.SupportsEmbeddings before calling.");

		OllamaEmbedRequest request = new(embeddingModel, JsonValue.Create("The quick brown fox."));

		OllamaEmbedResponse response = await provider.CreateEmbeddingsAsync(
			                               new BackendContext(config.BackendName),
			                               embeddingModel,
			                               request,
			                               cancellationToken);

		Assert.Equal(embeddingModel, response.Model);
		IReadOnlyList<float> vector = Assert.Single(response.Embeddings);
		Assert.NotEmpty(vector);
		Assert.All(vector, component => Assert.True(float.IsFinite(component), "Embedding components must be finite."));
	}

	/// <summary>
	/// Drives the server-side <c>reasoning_details</c> round-trip through the real adapter against a live
	/// emitter (Venice/OpenRouter), using a single provider instance over a shared cache so the blob captured
	/// on the first turn can be replayed on the second. Turn one advertises a tool and lets the model answer;
	/// when it both calls the tool <em>and</em> the backend attached a <c>reasoning_details</c> blob (verified
	/// by re-deriving the correlation key and querying the real cache), turn two replays the assistant turn and
	/// this method asserts the recorded upstream body re-attached the exact captured blob onto the matching
	/// message. This is the one assumption the offline PowerShell probe could never measure: it exercises the
	/// real capture-store-correlate-reattach pipeline against live backend data.
	/// <para>
	/// When the model declines to call the tool, or the backend does not emit the blob on this turn — both
	/// legitimate, non-deterministic outcomes — the strong re-attachment assertion cannot be exercised. The
	/// method reports that via the return value rather than failing, so the caller can surface a skip-like
	/// signal instead of a false pass or a flaky failure.
	/// </para>
	/// </summary>
	/// <param name="provider">The provider under test, constructed over <paramref name="cache"/>.</param>
	/// <param name="recorder">The recording client provider that captured the second turn's outgoing body.</param>
	/// <param name="cache">The same cache instance the provider was constructed with.</param>
	/// <param name="config">The resolved live backend configuration; its reasoning model is used.</param>
	/// <param name="cancellationToken">A token to cancel the operation.</param>
	/// <returns>
	/// <see cref="ReasoningRoundTripOutcome.Reattached"/> when the blob was captured and verified re-attached;
	/// <see cref="ReasoningRoundTripOutcome.NoToolCall"/> when the model answered without calling the tool;
	/// <see cref="ReasoningRoundTripOutcome.NoReasoningDetails"/> when it called the tool but the backend
	/// attached no blob.
	/// </returns>
	/// <exception cref="InvalidOperationException">
	/// <see cref="LiveBackendConfig.ReasoningModel"/> is <see langword="null"/>. The caller must gate on
	/// <see cref="LiveBackendConfig.SupportsReasoning"/> before invoking this method.
	/// </exception>
	public static async Task<ReasoningRoundTripOutcome> AssertReasoningRoundTripConformsAsync(
		IProviderAdapter            provider,
		RecordingHttpClientProvider recorder,
		IReasoningDetailsCache      cache,
		LiveBackendConfig           config,
		CancellationToken           cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(provider);
		ArgumentNullException.ThrowIfNull(recorder);
		ArgumentNullException.ThrowIfNull(cache);
		ArgumentNullException.ThrowIfNull(config);

		string reasoningModel = config.ReasoningModel
		                        ?? throw new InvalidOperationException(
			                        "AssertReasoningRoundTripConformsAsync requires a wired reasoning model; " +
			                        "gate on LiveBackendConfig.SupportsReasoning before calling.");

		OllamaTool weatherTool = new(
			"function",
			new OllamaToolFunction(
				"get_weather",
				"Get the current weather for a city.",
				new JsonObject
				{
					["type"] = "object",
					["properties"] = new JsonObject
					{
						["city"] = new JsonObject { ["type"] = "string", ["description"] = "The city name." }
					},
					["required"] = new JsonArray("city")
				}));

		// Turn 1: ask a question that invites a tool call; the provider captures any reasoning_details blob.
		OllamaChatRequest firstRequest = new(
			reasoningModel,
			[new OllamaChatMessage("user", "What is the weather in Berlin? Use the get_weather tool.")],
			Tools: [weatherTool],
			Think: true);

		OllamaChatResponse first = await provider.CompleteChatAsync(
			                           new BackendContext(config.BackendName),
			                           reasoningModel,
			                           firstRequest,
			                           pinnedEffort: null,
			                           cancellationToken);

		if (first.Message.ToolCalls is not { Count: > 0 } toolCalls)
			return ReasoningRoundTripOutcome.NoToolCall;

		// Re-derive the exact correlation key the provider stored under, and ask the real cache whether a blob
		// was actually captured for this turn. A miss means the backend emitted none — a legitimate outcome.
		string? correlationKey = ReasoningDetailsCorrelation.TryComputeKey(config.BackendName, toolCalls);
		JsonNode? capturedBlob = correlationKey is null ? null : cache.Retrieve(correlationKey);
		if (capturedBlob is null)
			return ReasoningRoundTripOutcome.NoReasoningDetails;

		// Turn 2: replay the assistant tool-calling turn plus a tool result; the provider must re-attach the
		// captured blob onto the replayed assistant message in the upstream body.
		OllamaChatRequest followUpRequest = new(
			reasoningModel,
			[
				new OllamaChatMessage("user", "What is the weather in Berlin? Use the get_weather tool."),
				new OllamaChatMessage("assistant", string.Empty, ToolCalls: toolCalls),
				new OllamaChatMessage(
					"tool",
					"18C",
					ToolName: toolCalls[0].Function.Name,
					ToolCallId: toolCalls[0].Id)
			]);

		await provider.CompleteChatAsync(
			new BackendContext(config.BackendName),
			reasoningModel,
			followUpRequest,
			pinnedEffort: null,
			cancellationToken);

		// Assert: the recorded follow-up body re-attached the exact captured blob onto the assistant message
		// (index 1 in the replayed list — user, assistant, tool).
		var followUpBody = Assert.IsType<JsonObject>(JsonNode.Parse(recorder.LastRequestBody!));
		var messages = Assert.IsType<JsonArray>(followUpBody["messages"]);
		JsonNode? reattached = messages[1]!["reasoning_details"];

		Assert.NotNull(reattached);
		Assert.Equal(capturedBlob.ToJsonString(), reattached.ToJsonString());

		return ReasoningRoundTripOutcome.Reattached;
	}
}

/// <summary>
/// The outcome of a live <c>reasoning_details</c> round-trip attempt. Distinguishes the verified strong
/// result from the two non-deterministic outcomes where the strong re-attachment assertion could not be
/// exercised, so a backend test can surface a skip-like signal rather than a false pass.
/// </summary>
enum ReasoningRoundTripOutcome
{
	/// <summary>The blob was captured on turn one and verified re-attached on the recorded turn-two body.</summary>
	Reattached,

	/// <summary>The model answered without calling the advertised tool, so no turn could be correlated.</summary>
	NoToolCall,

	/// <summary>The model called the tool but the backend attached no <c>reasoning_details</c> blob.</summary>
	NoReasoningDetails
}
