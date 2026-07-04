// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json.Nodes;

using OllamaProxy.Contracts.Ollama;
using OllamaProxy.Providers.OpenAiProtocol.Contracts;
using OllamaProxy.Providers.OpenAiProtocol.Mapping;

namespace OllamaProxy.Tests.Providers.OpenAiProtocol.Mapping;

/// <summary>
/// Tests for <see cref="OpenAiResponseMapper"/>, which aggregates a non-streaming OpenAI completion
/// into the single Ollama chat response the proxy returns. The story moves from the happy path
/// (content, reasoning, usage, tool calls, log-probabilities, caller-supplied timing) to the
/// defensive defaults applied when the upstream omits choices or a message.
/// </summary>
[Trait("Category", "Unit")]
public sealed class OpenAiResponseMapperTests
{
	private static OpenAiChatCompletion Completion(
		OpenAiChatMessage? message      = null,
		string?            finishReason = "stop",
		OpenAiUsage?       usage        = null,
		bool               withChoice   = true)
	{
		IReadOnlyList<OpenAiChatChoice> choices = withChoice
			                                          ?
			                                          [
				                                          new OpenAiChatChoice(
					                                          0,
					                                          message ?? new OpenAiChatMessage(
						                                          "assistant",
						                                          JsonValue.Create("hi")),
					                                          finishReason)
			                                          ]
			                                          : [];

		return new OpenAiChatCompletion("id", "gpt-4o", 0, choices, usage);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiResponseMapper.MapCompletion"/> projects the first choice's text,
	/// echoes the client-facing model and timestamp, marks the response done, and carries the
	/// caller-measured duration.
	/// </summary>
	[Fact]
	public void MapCompletion_WhenContentPresent_ProducesDoneResponseWithText()
	{
		// Arrange
		OpenAiChatCompletion completion = Completion(new OpenAiChatMessage("assistant", JsonValue.Create("hello")));

		// Act
		OllamaChatResponse result = OpenAiResponseMapper.MapCompletion(
			completion,
			"client-model",
			"2026-01-01T00:00:00Z",
			123L);

		// Assert
		Assert.Equal("client-model", result.Model);
		Assert.Equal("2026-01-01T00:00:00Z", result.CreatedAt);
		Assert.True(result.Done);
		Assert.Equal("stop", result.DoneReason);
		Assert.Equal("hello", result.Message.Content);
		Assert.Equal(123L, result.TotalDuration);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiResponseMapper.MapCompletion"/> surfaces the upstream
	/// <c>reasoning_content</c> as Ollama's native <c>thinking</c> field, separate from the visible content.
	/// </summary>
	[Fact]
	public void MapCompletion_WhenReasoningContentPresent_SurfacesThinking()
	{
		// Arrange
		OpenAiChatCompletion completion = Completion(
			new OpenAiChatMessage("assistant", JsonValue.Create("hello"), ReasoningContent: "let me think"));

		// Act
		OllamaChatResponse result = OpenAiResponseMapper.MapCompletion(completion, "m", "t", 0L);

		// Assert
		Assert.Equal("hello", result.Message.Content);
		Assert.Equal("let me think", result.Message.Thinking);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiResponseMapper.MapCompletion"/> reads OpenRouter's <c>reasoning</c>
	/// spelling as <c>thinking</c> when the de-facto <c>reasoning_content</c> field is absent.
	/// </summary>
	[Fact]
	public void MapCompletion_WhenOpenRouterReasoningPresent_SurfacesThinking()
	{
		// Arrange
		OpenAiChatCompletion completion = Completion(
			new OpenAiChatMessage("assistant", JsonValue.Create("hello"), Reasoning: "router thoughts"));

		// Act
		OllamaChatResponse result = OpenAiResponseMapper.MapCompletion(completion, "m", "t", 0L);

		// Assert
		Assert.Equal("router thoughts", result.Message.Thinking);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiResponseMapper.MapCompletion"/> leaves <c>thinking</c> as
	/// <see langword="null"/> (and therefore omitted from the response) when the upstream carries no
	/// reasoning channel.
	/// </summary>
	[Fact]
	public void MapCompletion_WhenNoReasoning_LeavesThinkingNull()
	{
		// Arrange
		OpenAiChatCompletion completion = Completion(new OpenAiChatMessage("assistant", JsonValue.Create("hello")));

		// Act
		OllamaChatResponse result = OpenAiResponseMapper.MapCompletion(completion, "m", "t", 0L);

		// Assert
		Assert.Null(result.Message.Thinking);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiResponseMapper.MapCompletion"/> projects token usage onto the
	/// Ollama prompt- and generation-token accounting fields.
	/// </summary>
	[Fact]
	public void MapCompletion_WhenUsageReported_ProjectsTokenCounts()
	{
		// Arrange
		OpenAiChatCompletion completion = Completion(usage: new OpenAiUsage(11, 22, 33));

		// Act
		OllamaChatResponse result = OpenAiResponseMapper.MapCompletion(completion, "m", "t", 0L);

		// Assert
		Assert.Equal(11, result.PromptEvalCount);
		Assert.Equal(22, result.EvalCount);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiResponseMapper.MapCompletion"/> converts the assistant's tool
	/// calls and normalizes the <c>tool_calls</c> finish reason to the Ollama <c>stop</c> reason.
	/// </summary>
	[Fact]
	public void MapCompletion_WhenToolCallsReturned_ConvertsCallsAndNormalizesReason()
	{
		// Arrange
		OpenAiChatMessage message = new(
			"assistant",
			Content: null,
			ToolCalls: [new OpenAiToolCall("c1", "function", new OpenAiToolCallFunction("search", """{"q":"x"}"""))]);
		OpenAiChatCompletion completion = Completion(message, finishReason: "tool_calls");

		// Act
		OllamaChatResponse result = OpenAiResponseMapper.MapCompletion(completion, "m", "t", 0L);

		// Assert
		Assert.Equal("stop", result.DoneReason);
		OllamaToolCall call = Assert.Single(result.Message.ToolCalls!);
		Assert.Equal("c1", call.Id);
		Assert.Equal("search", call.Function.Name);
		Assert.Equal("x", call.Function.Arguments?["q"]?.GetValue<string>());
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiResponseMapper.MapCompletion"/> unwraps the choice's
	/// <c>logprobs</c> object onto the Ollama response's bare log-probability array.
	/// </summary>
	[Fact]
	public void MapCompletion_WhenLogprobsPresent_UnwrapsOntoResponse()
	{
		// Arrange: OpenAI nests the per-token entries under a content array on the choice.
		JsonNode logprobs = new JsonObject
		{
			["content"] = new JsonArray { new JsonObject { ["token"] = "hi", ["logprob"] = -0.5 } }
		};
		OpenAiChatChoice choice = new(0, new OpenAiChatMessage("assistant", JsonValue.Create("hi")), "stop", logprobs);
		OpenAiChatCompletion completion = new("id", "gpt-4o", 0, [choice]);

		// Act
		OllamaChatResponse result = OpenAiResponseMapper.MapCompletion(completion, "m", "t", 0L);

		// Assert: the content wrapper is stripped, leaving the bare per-token array on the response.
		var array = Assert.IsType<JsonArray>(result.Logprobs);
		Assert.Equal("hi", array[0]?["token"]?.GetValue<string>());
		Assert.Equal(-0.5, array[0]?["logprob"]?.GetValue<double>());
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiResponseMapper.MapCompletion"/> leaves <c>logprobs</c> as
	/// <see langword="null"/> (and therefore omitted) when the upstream choice carries none.
	/// </summary>
	[Fact]
	public void MapCompletion_WhenNoLogprobs_LeavesLogprobsNull()
	{
		// Arrange
		OpenAiChatCompletion completion = Completion(new OpenAiChatMessage("assistant", JsonValue.Create("hello")));

		// Act
		OllamaChatResponse result = OpenAiResponseMapper.MapCompletion(completion, "m", "t", 0L);

		// Assert
		Assert.Null(result.Logprobs);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiResponseMapper.MapCompletion"/> falls back to an empty assistant
	/// message and the default <c>stop</c> reason when the upstream returns no choices.
	/// </summary>
	[Fact]
	public void MapCompletion_WhenNoChoices_UsesAssistantDefaults()
	{
		// Arrange
		OpenAiChatCompletion completion = Completion(withChoice: false);

		// Act
		OllamaChatResponse result = OpenAiResponseMapper.MapCompletion(completion, "m", "t", 0L);

		// Assert
		Assert.Equal("assistant", result.Message.Role);
		Assert.Equal(string.Empty, result.Message.Content);
		Assert.Equal("stop", result.DoneReason);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiResponseMapper.MapCompletion"/> applies the same assistant
	/// defaults when a non-conforming backend omits the <c>choices</c> array entirely (deserialized as
	/// <see langword="null"/>, distinct from the empty-array case above), rather than dereferencing the
	/// missing collection.
	/// </summary>
	[Fact]
	public void MapCompletion_WhenChoicesNull_UsesAssistantDefaults()
	{
		// Arrange: a 2xx body that carried no "choices" key at all leaves the non-nullable-looking
		// property null, because the shared serializer does not enforce the annotation at runtime.
		OpenAiChatCompletion completion = new("id", "gpt-4o", 0, Choices: null, Usage: null);

		// Act
		OllamaChatResponse result = OpenAiResponseMapper.MapCompletion(completion, "m", "t", 0L);

		// Assert
		Assert.Equal("assistant", result.Message.Role);
		Assert.Equal(string.Empty, result.Message.Content);
		Assert.Equal("stop", result.DoneReason);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiResponseMapper.MapCompletion"/> rejects a <see langword="null"/>
	/// completion.
	/// </summary>
	[Fact]
	public void MapCompletion_WhenCompletionIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception =
			Assert.Throws<ArgumentNullException>(() => OpenAiResponseMapper.MapCompletion(null!, "m", "t", 0L));
		Assert.Equal("completion", exception.ParamName);
	}
}
