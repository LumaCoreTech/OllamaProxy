// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using OllamaProxy.Configuration;
using OllamaProxy.Providers.Abstractions;
using OllamaProxy.Providers.Http;
using OllamaProxy.Providers.OpenAiProtocol;

namespace OllamaProxy.Tests.Integration;

/// <summary>
/// Integration tests exercising <see cref="OpenAiCapabilityProber"/> end to end against a mock
/// OpenAI-compatible backend. A canned <see cref="HttpMessageHandler"/> stands in for the upstream so
/// each test drives the full path — payload construction, HTTP transport, and status interpretation —
/// without a live network.
/// 
/// The story is the same for all three probes and is told once per capability:
/// 
/// 1. Payload shape: the tool and vision probes target <c>chat/completions</c> with a silent-response
/// prompt, no token cap, and <c>stream</c> set (so they confirm on the response headers instead of the full
/// generation), and carry the capability-specific marker (a dummy function for tools, a feature-rich image for
/// vision); the embedding probe targets <c>embeddings</c> with a short input string and does not stream.
/// 2. Status interpretation: a 2xx confirms support; a non-auth 4xx (400/404/422, etc.) denies it;
/// authentication failures (401/403), throttling/server statuses (429/5xx) and transport failures are
/// inconclusive (<see langword="null"/>); a caller-requested cancellation propagates.
/// 3. Argument guards close each member's contract.
/// 
/// The three probes share an identical interpretation path inside the prober, but they are distinct
/// public members, so each gets its own dedicated tests rather than a shared cross-member theory.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OpenAiCapabilityProberIntegrationTests
{
	private const string BackendName = "mock";

	private const string ModelId = "some-model";

	/// <summary>Builds a JSON HTTP response with the given status and body.</summary>
	/// <param name="status">The HTTP status code to return.</param>
	/// <param name="body">The JSON response body; defaults to an empty object.</param>
	/// <returns>The constructed response message.</returns>
	private static HttpResponseMessage Json(HttpStatusCode status, string body = "{}") => new(status)
		{ Content = new StringContent(body, Encoding.UTF8, "application/json") };

	/// <summary>Creates the prober under test over the supplied scripted handler.</summary>
	/// <param name="handler">The scripted handler backing the prober's HTTP client.</param>
	/// <param name="maxProbeRetries">The retry budget for the backend; defaults to no retries.</param>
	/// <returns>The configured <see cref="OpenAiCapabilityProber"/>.</returns>
	private static OpenAiCapabilityProber CreateSut(ScriptedHandler handler, int maxProbeRetries = 0) => new(
		new StubHttpClientProvider(handler),
		OptionsWith(maxProbeRetries),
		TimeProvider.System,
		NullLogger<OpenAiCapabilityProber>.Instance);

	/// <summary>
	/// Creates the prober under test over an arbitrary handler, used by the retry/timeout tests that
	/// script a distinct response per attempt and need to tune the per-attempt timeout.
	/// </summary>
	/// <param name="handler">The handler backing the prober's HTTP client.</param>
	/// <param name="maxProbeRetries">The retry budget for the backend.</param>
	/// <param name="timeoutSeconds">The per-attempt timeout in seconds.</param>
	/// <returns>The configured <see cref="OpenAiCapabilityProber"/>.</returns>
	private static OpenAiCapabilityProber CreateSut(
		HttpMessageHandler handler,
		int                maxProbeRetries,
		int                timeoutSeconds) => new(
		new StubHttpClientProvider(handler),
		OptionsWith(maxProbeRetries, timeoutSeconds),
		TimeProvider.System,
		NullLogger<OpenAiCapabilityProber>.Instance);

	/// <summary>
	/// Builds an options monitor whose single backend carries the given retry budget and per-attempt
	/// timeout, with a zero retry delay so retry-driven tests do not actually wait between attempts.
	/// </summary>
	/// <param name="maxProbeRetries">The retry budget for the backend.</param>
	/// <param name="timeoutSeconds">The per-attempt timeout in seconds.</param>
	/// <returns>An options monitor exposing the configured backend.</returns>
	private static StaticOptionsMonitor OptionsWith(int maxProbeRetries, int timeoutSeconds = 10)
	{
		ProxyOptions options = new()
		{
			Backends =
			{
				[BackendName] = new BackendOptions
				{
					BaseUrl = "https://mock.test/v1",
					ProviderType = "openai",
					Probing = new CapabilityProbingOptions
					{
						MaxProbeRetries = maxProbeRetries,
						RetryBaseDelaySeconds = 0,
						TimeoutSeconds = timeoutSeconds
					}
				}
			}
		};

		return new StaticOptionsMonitor(options);
	}

	#region ProbeToolSupportAsync

	/// <summary>
	/// Verifies that <see cref="OpenAiCapabilityProber.ProbeToolSupportAsync"/> posts a completion to
	/// <c>chat/completions</c> that advertises a single dummy function, and reports tool support on a 2xx.
	/// </summary>
	[Fact]
	public async Task ProbeToolSupportAsync_WhenBackendAccepts_PostsToolPayloadAndReturnsTrue()
	{
		// Arrange
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK));
		OpenAiCapabilityProber sut = CreateSut(handler);

		// Act
		bool? result = await sut.ProbeToolSupportAsync(
			               new BackendContext(BackendName),
			               ModelId,
			               CancellationToken.None);

		// Assert: outcome is a confirmed capability.
		Assert.True(result);

		// Assert: the request hit the completions endpoint without a token cap, carrying the probed model.
		Assert.NotNull(handler.LastRequest);
		Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
		Assert.EndsWith("chat/completions", handler.LastRequest.RequestUri!.AbsolutePath, StringComparison.Ordinal);

		JsonNode payload = JsonNode.Parse(handler.LastRequestBody!)!;
		Assert.Equal(ModelId, (string?)payload["model"]);
		Assert.Null(payload["max_tokens"]);
		Assert.Null(payload["max_completion_tokens"]);

		// Assert: the chat probe streams so it confirms on the response headers rather than waiting for the
		// model to finish generating — the fix that keeps probes from timing out on slow models.
		Assert.True((bool?)payload["stream"]);

		// Assert: a single trivial function definition exercises the tools parameter.
		JsonArray tools = payload["tools"]!.AsArray();
		JsonNode function = Assert.Single(tools)!;
		Assert.Equal("function", (string?)function["type"]);
		Assert.Equal("ping", (string?)function["function"]!["name"]);

		// Assert (negative): a tool probe must not carry an image part.
		Assert.Null(payload["messages"]![0]!["content"] as JsonArray);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiCapabilityProber.ProbeToolSupportAsync"/> treats a non-auth 4xx
	/// (including 404 on a missing capability endpoint) as a definitive absence of tool support.
	/// </summary>
	/// <param name="status">The body-rejecting client error status the backend returns.</param>
	[Theory]
	[InlineData(HttpStatusCode.BadRequest)]
	[InlineData(HttpStatusCode.NotFound)]
	[InlineData(HttpStatusCode.UnprocessableEntity)]
	public async Task ProbeToolSupportAsync_WhenBackendRejectsBody_ReturnsFalse(HttpStatusCode status)
	{
		// Arrange
		ScriptedHandler handler = new(_ => Json(status, """{"error":{"message":"tools not supported"}}"""));
		OpenAiCapabilityProber sut = CreateSut(handler);

		// Act
		bool? result = await sut.ProbeToolSupportAsync(
			               new BackendContext(BackendName),
			               ModelId,
			               CancellationToken.None);

		// Assert
		Assert.False(result);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiCapabilityProber.ProbeToolSupportAsync"/> reports an inconclusive result
	/// for auth, throttling, and server-side statuses that say nothing about tool support.
	/// </summary>
	/// <param name="status">The non-conclusive status the backend returns.</param>
	[Theory]
	[InlineData(HttpStatusCode.Unauthorized)]
	[InlineData(HttpStatusCode.Forbidden)]
	[InlineData(HttpStatusCode.TooManyRequests)]
	[InlineData(HttpStatusCode.InternalServerError)]
	[InlineData(HttpStatusCode.BadGateway)]
	public async Task ProbeToolSupportAsync_WhenStatusInconclusive_ReturnsNull(HttpStatusCode status)
	{
		// Arrange
		ScriptedHandler handler = new(_ => Json(status));
		OpenAiCapabilityProber sut = CreateSut(handler);

		// Act
		bool? result = await sut.ProbeToolSupportAsync(
			               new BackendContext(BackendName),
			               ModelId,
			               CancellationToken.None);

		// Assert
		Assert.Null(result);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiCapabilityProber.ProbeToolSupportAsync"/> swallows a transport failure and
	/// reports an inconclusive result rather than propagating the exception.
	/// </summary>
	[Fact]
	public async Task ProbeToolSupportAsync_WhenTransportFails_ReturnsNull()
	{
		// Arrange: the handler throws before any response is produced, simulating a connection failure.
		ScriptedHandler handler = new(_ => throw new HttpRequestException("connection refused"));
		OpenAiCapabilityProber sut = CreateSut(handler);

		// Act
		bool? result = await sut.ProbeToolSupportAsync(
			               new BackendContext(BackendName),
			               ModelId,
			               CancellationToken.None);

		// Assert
		Assert.Null(result);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiCapabilityProber.ProbeToolSupportAsync"/> propagates a caller-requested
	/// cancellation instead of masking it as an inconclusive result.
	/// </summary>
	[Fact]
	public async Task ProbeToolSupportAsync_WhenCallerCancels_ThrowsOperationCanceledException()
	{
		// Arrange: an already-canceled token so the HTTP send observes cancellation immediately.
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK));
		OpenAiCapabilityProber sut = CreateSut(handler);
		using CancellationTokenSource cts = new();
		await cts.CancelAsync();

		// Act + Assert: accept any OperationCanceledException — HttpClient may surface the derived
		// TaskCanceledException or the base type depending on where cancellation is first observed.
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			sut.ProbeToolSupportAsync(new BackendContext(BackendName), ModelId, cts.Token));
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiCapabilityProber.ProbeToolSupportAsync"/> rejects a <see langword="null"/>
	/// backend.
	/// </summary>
	[Fact]
	public async Task ProbeToolSupportAsync_WhenBackendIsNull_ThrowsArgumentNullException()
	{
		// Arrange
		OpenAiCapabilityProber sut = CreateSut(new ScriptedHandler(_ => Json(HttpStatusCode.OK)));

		// Act + Assert
		var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
			                sut.ProbeToolSupportAsync(null!, ModelId, CancellationToken.None));
		Assert.Equal("backend", exception.ParamName);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiCapabilityProber.ProbeToolSupportAsync"/> rejects a blank model id.
	/// </summary>
	/// <param name="modelId">The blank model id under test.</param>
	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public async Task ProbeToolSupportAsync_WhenModelIdIsBlank_ThrowsArgumentException(string modelId)
	{
		// Arrange
		OpenAiCapabilityProber sut = CreateSut(new ScriptedHandler(_ => Json(HttpStatusCode.OK)));

		// Act + Assert
		var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
			                sut.ProbeToolSupportAsync(
				                new BackendContext(BackendName),
				                modelId,
				                CancellationToken.None));
		Assert.Equal("modelId", exception.ParamName);
	}

	#endregion

	#region ProbeVisionSupportAsync

	/// <summary>
	/// Verifies that <see cref="OpenAiCapabilityProber.ProbeVisionSupportAsync"/> posts a completion to
	/// <c>chat/completions</c> whose user turn carries a placeholder image part, and reports vision support
	/// on a 2xx.
	/// </summary>
	[Fact]
	public async Task ProbeVisionSupportAsync_WhenBackendAccepts_PostsImagePayloadAndReturnsTrue()
	{
		// Arrange
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK));
		OpenAiCapabilityProber sut = CreateSut(handler);

		// Act
		bool? result = await sut.ProbeVisionSupportAsync(
			               new BackendContext(BackendName),
			               ModelId,
			               CancellationToken.None);

		// Assert: outcome is a confirmed capability.
		Assert.True(result);

		// Assert: the request hit the completions endpoint without a token cap, carrying the probed model.
		Assert.NotNull(handler.LastRequest);
		Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
		Assert.EndsWith("chat/completions", handler.LastRequest.RequestUri!.AbsolutePath, StringComparison.Ordinal);

		JsonNode payload = JsonNode.Parse(handler.LastRequestBody!)!;
		Assert.Equal(ModelId, (string?)payload["model"]);
		Assert.Null(payload["max_tokens"]);
		Assert.Null(payload["max_completion_tokens"]);

		// Assert: the chat probe streams so it confirms on the response headers rather than waiting for the
		// model to finish generating — the fix that keeps probes from timing out on slow models.
		Assert.True((bool?)payload["stream"]);

		// Assert: the user content carries a text part and an image_url part with an inline data URI.
		JsonArray content = payload["messages"]![0]!["content"]!.AsArray();
		Assert.Equal(2, content.Count);
		Assert.Equal("text", (string?)content[0]!["type"]);
		Assert.Equal("image_url", (string?)content[1]!["type"]);
		string imageUrl = (string?)content[1]!["image_url"]!["url"] ?? string.Empty;
		Assert.StartsWith("data:image/jpeg;base64,", imageUrl, StringComparison.Ordinal);

		// Assert (negative): a vision probe must not advertise tools.
		Assert.Null(payload["tools"]);
	}

	/// <summary>
	/// Verifies that the vision probe's placeholder image is a valid JPEG carrying real visual features. The
	/// decisive constraint, verified live against several Venice vision models (venice-uncensored-1-2,
	/// qwen-3-7-plus, qwen3-5-35b-a3b, google-gemma-3-27b-it), is image CONTENT, not size: a plain
	/// single-colour / simple-geometry image was rejected with "Supplied image did not pass validation checks."
	/// at 64x64, 256x256 AND 512x512 alike, while a busier image (colour gradient plus several distinct shapes)
	/// passed on every one of those models. A too-plain placeholder therefore made vision-capable models probe
	/// as vision-less. This guards the fix by decoding the image the prober actually sends and asserting it is a
	/// real, multi-pixel JPEG whose encoded size reflects genuine visual detail rather than a flat fill.
	/// </summary>
	[Fact]
	public async Task ProbeVisionSupportAsync_PlaceholderImage_IsValidFeatureRichJpeg()
	{
		// Arrange
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK));
		OpenAiCapabilityProber sut = CreateSut(handler);

		// Act: drive a real probe so the assertion runs against the exact bytes the prober embeds.
		await sut.ProbeVisionSupportAsync(new BackendContext(BackendName), ModelId, CancellationToken.None);

		// Extract the data URI the probe attached and strip the "data:image/jpeg;base64," prefix.
		JsonNode payload = JsonNode.Parse(handler.LastRequestBody!)!;
		string imageUrl = (string?)payload["messages"]![0]!["content"]![1]!["image_url"]!["url"] ?? string.Empty;
		const string base64Marker = "base64,";
		int markerIndex = imageUrl.IndexOf(base64Marker, StringComparison.Ordinal);
		Assert.True(markerIndex >= 0, "The placeholder image must be an inline base64 data URI.");
		byte[] jpeg = Convert.FromBase64String(imageUrl[(markerIndex + base64Marker.Length)..]);

		// Assert: the bytes open with the JPEG SOI marker (FF D8 FF), proving a real decodable image was sent.
		Assert.True(jpeg.Length > 3, "The placeholder JPEG is implausibly small.");
		Assert.True(jpeg[0] == 0xFF && jpeg[1] == 0xD8 && jpeg[2] == 0xFF, "Not a valid JPEG SOI marker.");

		// Assert: scan the JPEG segments for the SOF0/SOF2 frame header and read its real height/width (both
		// big-endian, three and five bytes into the frame payload). Both dimensions must be at least 64px — a
		// degenerate placeholder would fail this.
		const int minimumDimension = 64;
		(int width, int height) = ReadJpegDimensions(jpeg);
		Assert.True(width >= minimumDimension, $"Placeholder width must be >= {minimumDimension}px; was {width}.");
		Assert.True(height >= minimumDimension, $"Placeholder height must be >= {minimumDimension}px; was {height}.");

		// Assert: the encoded image carries genuine visual detail. A flat single-colour fill of this size
		// compresses to only a few hundred bytes; the feature-rich probe image (gradient + multiple shapes)
		// that Venice's content validator actually accepts is several kilobytes. Guard the lower bound so a
		// future edit cannot silently regress to a too-plain image that the validator would reject.
		Assert.True(
			jpeg.Length > 2000,
			$"Placeholder JPEG is too plain to clear content validation; was {jpeg.Length} bytes.");
	}

	/// <summary>
	/// Reads the pixel dimensions of a baseline JPEG by walking its marker segments to the Start-Of-Frame
	/// (SOF0/SOF2) header, whose payload encodes height then width as big-endian 16-bit values.
	/// </summary>
	private static (int Width, int Height) ReadJpegDimensions(byte[] jpeg)
	{
		int offset = 2; // skip the SOI marker (FF D8)
		while (offset + 9 < jpeg.Length)
		{
			if (jpeg[offset] != 0xFF)
			{
				offset++;
				continue;
			}

			byte marker = jpeg[offset + 1];
			int segmentLength = (jpeg[offset + 2] << 8) | jpeg[offset + 3];

			// SOF0 (0xC0) and SOF2 (0xC2) carry the frame dimensions; others are skipped by their length.
			if (marker is 0xC0 or 0xC2)
			{
				int height = (jpeg[offset + 5] << 8) | jpeg[offset + 6];
				int width = (jpeg[offset + 7] << 8) | jpeg[offset + 8];
				return (width, height);
			}

			offset += 2 + segmentLength;
		}

		return (0, 0);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiCapabilityProber.ProbeVisionSupportAsync"/> treats a non-auth 4xx
	/// (including 404 on a missing capability endpoint) as a definitive absence of vision support.
	/// </summary>
	/// <param name="status">The body-rejecting client error status the backend returns.</param>
	[Theory]
	[InlineData(HttpStatusCode.BadRequest)]
	[InlineData(HttpStatusCode.NotFound)]
	[InlineData(HttpStatusCode.UnprocessableEntity)]
	public async Task ProbeVisionSupportAsync_WhenBackendRejectsBody_ReturnsFalse(HttpStatusCode status)
	{
		// Arrange: a text-only model typically answers an image part with a 400.
		ScriptedHandler handler = new(_ => Json(status, """{"error":{"message":"image input not supported"}}"""));
		OpenAiCapabilityProber sut = CreateSut(handler);

		// Act
		bool? result = await sut.ProbeVisionSupportAsync(
			               new BackendContext(BackendName),
			               ModelId,
			               CancellationToken.None);

		// Assert
		Assert.False(result);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiCapabilityProber.ProbeVisionSupportAsync"/> reports an inconclusive result
	/// for auth, throttling, and server-side statuses that say nothing about vision support.
	/// </summary>
	/// <param name="status">The non-conclusive status the backend returns.</param>
	[Theory]
	[InlineData(HttpStatusCode.Unauthorized)]
	[InlineData(HttpStatusCode.Forbidden)]
	[InlineData(HttpStatusCode.TooManyRequests)]
	[InlineData(HttpStatusCode.InternalServerError)]
	[InlineData(HttpStatusCode.BadGateway)]
	public async Task ProbeVisionSupportAsync_WhenStatusInconclusive_ReturnsNull(HttpStatusCode status)
	{
		// Arrange
		ScriptedHandler handler = new(_ => Json(status));
		OpenAiCapabilityProber sut = CreateSut(handler);

		// Act
		bool? result = await sut.ProbeVisionSupportAsync(
			               new BackendContext(BackendName),
			               ModelId,
			               CancellationToken.None);

		// Assert
		Assert.Null(result);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiCapabilityProber.ProbeVisionSupportAsync"/> swallows a transport failure and
	/// reports an inconclusive result rather than propagating the exception.
	/// </summary>
	[Fact]
	public async Task ProbeVisionSupportAsync_WhenTransportFails_ReturnsNull()
	{
		// Arrange: the handler throws before any response is produced, simulating a connection failure.
		ScriptedHandler handler = new(_ => throw new HttpRequestException("connection refused"));
		OpenAiCapabilityProber sut = CreateSut(handler);

		// Act
		bool? result = await sut.ProbeVisionSupportAsync(
			               new BackendContext(BackendName),
			               ModelId,
			               CancellationToken.None);

		// Assert
		Assert.Null(result);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiCapabilityProber.ProbeVisionSupportAsync"/> propagates a caller-requested
	/// cancellation instead of masking it as an inconclusive result.
	/// </summary>
	[Fact]
	public async Task ProbeVisionSupportAsync_WhenCallerCancels_ThrowsOperationCanceledException()
	{
		// Arrange: an already-canceled token so the HTTP send observes cancellation immediately.
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK));
		OpenAiCapabilityProber sut = CreateSut(handler);
		using CancellationTokenSource cts = new();
		await cts.CancelAsync();

		// Act + Assert: accept any OperationCanceledException — HttpClient may surface the derived
		// TaskCanceledException or the base type depending on where cancellation is first observed.
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			sut.ProbeVisionSupportAsync(new BackendContext(BackendName), ModelId, cts.Token));
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiCapabilityProber.ProbeVisionSupportAsync"/> rejects a <see langword="null"/>
	/// backend.
	/// </summary>
	[Fact]
	public async Task ProbeVisionSupportAsync_WhenBackendIsNull_ThrowsArgumentNullException()
	{
		// Arrange
		OpenAiCapabilityProber sut = CreateSut(new ScriptedHandler(_ => Json(HttpStatusCode.OK)));

		// Act + Assert
		var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
			                sut.ProbeVisionSupportAsync(null!, ModelId, CancellationToken.None));
		Assert.Equal("backend", exception.ParamName);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiCapabilityProber.ProbeVisionSupportAsync"/> rejects a blank model id.
	/// </summary>
	/// <param name="modelId">The blank model id under test.</param>
	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public async Task ProbeVisionSupportAsync_WhenModelIdIsBlank_ThrowsArgumentException(string modelId)
	{
		// Arrange
		OpenAiCapabilityProber sut = CreateSut(new ScriptedHandler(_ => Json(HttpStatusCode.OK)));

		// Act + Assert
		var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
			                sut.ProbeVisionSupportAsync(
				                new BackendContext(BackendName),
				                modelId,
				                CancellationToken.None));
		Assert.Equal("modelId", exception.ParamName);
	}

	#endregion

	#region ProbeEmbeddingSupportAsync

	/// <summary>
	/// Verifies that <see cref="OpenAiCapabilityProber.ProbeEmbeddingSupportAsync"/> posts a short input to
	/// <c>embeddings</c>, and reports embedding support on a 2xx.
	/// </summary>
	[Fact]
	public async Task ProbeEmbeddingSupportAsync_WhenBackendAccepts_PostsEmbeddingPayloadAndReturnsTrue()
	{
		// Arrange
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK));
		OpenAiCapabilityProber sut = CreateSut(handler);

		// Act
		bool? result = await sut.ProbeEmbeddingSupportAsync(
			               new BackendContext(BackendName),
			               ModelId,
			               CancellationToken.None);

		// Assert: outcome is a confirmed capability.
		Assert.True(result);

		// Assert: the request hit the embeddings endpoint with the probed model and a short input.
		Assert.NotNull(handler.LastRequest);
		Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
		Assert.EndsWith("embeddings", handler.LastRequest.RequestUri!.AbsolutePath, StringComparison.Ordinal);

		JsonNode payload = JsonNode.Parse(handler.LastRequestBody!)!;
		Assert.Equal(ModelId, (string?)payload["model"]);
		Assert.Equal("ping", (string?)payload["input"]);

		// Assert (negative): an embedding probe must not carry a chat completion's marker payloads, and it does
		// not stream — there is no generation to wait on, so the streaming fast-path applies only to chat probes.
		Assert.Null(payload["messages"]);
		Assert.Null(payload["tools"]);
		Assert.Null(payload["stream"]);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiCapabilityProber.ProbeEmbeddingSupportAsync"/> treats a body-rejecting 4xx
	/// as a definitive absence of embedding support — the expected outcome when a completion-only model
	/// rejects the embeddings request, or when the embeddings endpoint itself is not implemented.
	/// </summary>
	/// <param name="status">The body-rejecting client error status the backend returns.</param>
	[Theory]
	[InlineData(HttpStatusCode.BadRequest)]
	[InlineData(HttpStatusCode.NotFound)]
	[InlineData(HttpStatusCode.UnprocessableEntity)]
	public async Task ProbeEmbeddingSupportAsync_WhenBackendRejectsBody_ReturnsFalse(HttpStatusCode status)
	{
		// Arrange: a completion-only model typically answers the embeddings endpoint with a 400.
		ScriptedHandler handler = new(_ => Json(status, """{"error":{"message":"embeddings not supported"}}"""));
		OpenAiCapabilityProber sut = CreateSut(handler);

		// Act
		bool? result = await sut.ProbeEmbeddingSupportAsync(
			               new BackendContext(BackendName),
			               ModelId,
			               CancellationToken.None);

		// Assert
		Assert.False(result);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiCapabilityProber.ProbeEmbeddingSupportAsync"/> reports an inconclusive
	/// result for auth, throttling, and server-side statuses that say nothing about embedding support.
	/// </summary>
	/// <param name="status">The non-conclusive status the backend returns.</param>
	[Theory]
	[InlineData(HttpStatusCode.Unauthorized)]
	[InlineData(HttpStatusCode.Forbidden)]
	[InlineData(HttpStatusCode.TooManyRequests)]
	[InlineData(HttpStatusCode.InternalServerError)]
	[InlineData(HttpStatusCode.BadGateway)]
	public async Task ProbeEmbeddingSupportAsync_WhenStatusInconclusive_ReturnsNull(HttpStatusCode status)
	{
		// Arrange
		ScriptedHandler handler = new(_ => Json(status));
		OpenAiCapabilityProber sut = CreateSut(handler);

		// Act
		bool? result = await sut.ProbeEmbeddingSupportAsync(
			               new BackendContext(BackendName),
			               ModelId,
			               CancellationToken.None);

		// Assert
		Assert.Null(result);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiCapabilityProber.ProbeEmbeddingSupportAsync"/> swallows a transport failure
	/// and reports an inconclusive result rather than propagating the exception.
	/// </summary>
	[Fact]
	public async Task ProbeEmbeddingSupportAsync_WhenTransportFails_ReturnsNull()
	{
		// Arrange: the handler throws before any response is produced, simulating a connection failure.
		ScriptedHandler handler = new(_ => throw new HttpRequestException("connection refused"));
		OpenAiCapabilityProber sut = CreateSut(handler);

		// Act
		bool? result = await sut.ProbeEmbeddingSupportAsync(
			               new BackendContext(BackendName),
			               ModelId,
			               CancellationToken.None);

		// Assert
		Assert.Null(result);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiCapabilityProber.ProbeEmbeddingSupportAsync"/> propagates a caller-requested
	/// cancellation instead of masking it as an inconclusive result.
	/// </summary>
	[Fact]
	public async Task ProbeEmbeddingSupportAsync_WhenCallerCancels_ThrowsOperationCanceledException()
	{
		// Arrange: an already-canceled token so the HTTP send observes cancellation immediately.
		ScriptedHandler handler = new(_ => Json(HttpStatusCode.OK));
		OpenAiCapabilityProber sut = CreateSut(handler);
		using CancellationTokenSource cts = new();
		await cts.CancelAsync();

		// Act + Assert: accept any OperationCanceledException — HttpClient may surface the derived
		// TaskCanceledException or the base type depending on where cancellation is first observed.
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			sut.ProbeEmbeddingSupportAsync(new BackendContext(BackendName), ModelId, cts.Token));
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiCapabilityProber.ProbeEmbeddingSupportAsync"/> rejects a
	/// <see langword="null"/> backend.
	/// </summary>
	[Fact]
	public async Task ProbeEmbeddingSupportAsync_WhenBackendIsNull_ThrowsArgumentNullException()
	{
		// Arrange
		OpenAiCapabilityProber sut = CreateSut(new ScriptedHandler(_ => Json(HttpStatusCode.OK)));

		// Act + Assert
		var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
			                sut.ProbeEmbeddingSupportAsync(null!, ModelId, CancellationToken.None));
		Assert.Equal("backend", exception.ParamName);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiCapabilityProber.ProbeEmbeddingSupportAsync"/> rejects a blank model id.
	/// </summary>
	/// <param name="modelId">The blank model id under test.</param>
	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public async Task ProbeEmbeddingSupportAsync_WhenModelIdIsBlank_ThrowsArgumentException(string modelId)
	{
		// Arrange
		OpenAiCapabilityProber sut = CreateSut(new ScriptedHandler(_ => Json(HttpStatusCode.OK)));

		// Act + Assert
		var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
			                sut.ProbeEmbeddingSupportAsync(
				                new BackendContext(BackendName),
				                modelId,
				                CancellationToken.None));
		Assert.Equal("modelId", exception.ParamName);
	}

	#endregion

	#region Retry and timeout

	// The prober owns bounding and retrying each probe: a single attempt is capped by the per-attempt
	// timeout, and genuinely transient failures (HTTP 429, HTTP 5xx, transport faults) are retried up to
	// the configured budget while a timeout, permanent failures and conclusive results are not. A timeout
	// funds one adequate attempt rather than several short ones, so it ends the probe as inconclusive
	// without a retry. These tests pin that behavior end to end, since the resolver no longer owns any
	// timeout of its own.

	/// <summary>
	/// Verifies that a transient HTTP 5xx on the first attempt is retried and a subsequent success is
	/// reported as a conclusive positive, proving the retry budget is consumed by transient failures.
	/// </summary>
	[Fact]
	public async Task ProbeToolSupportAsync_WhenFirstAttemptIsTransientThenSucceeds_RetriesAndReturnsTrue()
	{
		// Arrange: the first attempt hits a transient 503, the second succeeds. One retry is allowed.
		int calls = 0;
		ScriptedHandler handler = new(_ =>
		{
			calls++;
			return calls == 1 ? Json(HttpStatusCode.ServiceUnavailable) : Json(HttpStatusCode.OK);
		});
		OpenAiCapabilityProber sut = CreateSut(handler, maxProbeRetries: 1);

		// Act
		bool? result = await sut.ProbeToolSupportAsync(
			               new BackendContext(BackendName),
			               ModelId,
			               CancellationToken.None);

		// Assert: the retry turned the transient failure into a conclusive positive after exactly two attempts.
		Assert.True(result);
		Assert.Equal(2, calls);
	}

	/// <summary>
	/// Verifies that a transient failure that persists across every attempt exhausts the retry budget and
	/// is reported as inconclusive (<see langword="null"/>), so the resolver retains the conservative default.
	/// </summary>
	[Fact]
	public async Task ProbeToolSupportAsync_WhenEveryAttemptIsTransient_ExhaustsRetriesAndReturnsNull()
	{
		// Arrange: every attempt returns a transient 503. Two retries means three attempts in total.
		int calls = 0;
		ScriptedHandler handler = new(_ =>
		{
			calls++;
			return Json(HttpStatusCode.ServiceUnavailable);
		});
		OpenAiCapabilityProber sut = CreateSut(handler, maxProbeRetries: 2);

		// Act
		bool? result = await sut.ProbeToolSupportAsync(
			               new BackendContext(BackendName),
			               ModelId,
			               CancellationToken.None);

		// Assert: the budget is exhausted (1 initial + 2 retries) and the outcome is inconclusive.
		Assert.Null(result);
		Assert.Equal(3, calls);
	}

	/// <summary>
	/// Verifies that a per-attempt timeout is <em>not</em> retried even when the retry budget would allow it:
	/// a timed-out attempt ends the probe as inconclusive (<see langword="null"/>) after exactly one attempt,
	/// because a model too slow to answer within the window will not answer a second identical attempt faster.
	/// </summary>
	[Fact]
	public async Task ProbeToolSupportAsync_WhenAttemptTimesOut_DoesNotRetryAndReturnsNull()
	{
		// Arrange: the first attempt blocks until the prober's per-attempt timeout cancels it. A retry budget of
		// one is offered on purpose — the timeout must still not consume it. A 1-second timeout keeps the cost low.
		FirstAttemptTimesOutHandler handler = new(onRetry: () => Json(HttpStatusCode.OK));
		OpenAiCapabilityProber sut = CreateSut(handler, maxProbeRetries: 1, timeoutSeconds: 1);

		// Act
		bool? result = await sut.ProbeToolSupportAsync(
			               new BackendContext(BackendName),
			               ModelId,
			               CancellationToken.None);

		// Assert: the timeout ended the probe inconclusive after a single attempt — the retry was never taken.
		Assert.Null(result);
		Assert.Equal(1, handler.Calls);
	}

	#endregion

	#region Retry-After and shared backend cooldown

	// A backend that throttles (HTTP 429) often says exactly how long to wait via a Retry-After header. The
	// prober honors it verbatim instead of its blind exponential backoff, and — because the discovery layer
	// fans several models out concurrently against one backend — publishes the resulting cooldown to a
	// per-backend store its SIBLING probes read before their next request. These two tests pin both halves:
	// that a Retry-After delta is honored, and that one probe's throttle paces an innocent concurrent sibling.

	/// <summary>
	/// Verifies that a throttling response carrying a <c>Retry-After</c> delta makes the prober wait that
	/// long before retrying, in preference to its configured exponential backoff: the backend's own pacing
	/// wins. With the base backoff set to zero, the only thing that can produce the observed delay is the
	/// honored header.
	/// </summary>
	[Fact]
	public async Task ProbeToolSupportAsync_WhenThrottledWithRetryAfter_HonorsServerDelayBeforeRetry()
	{
		// Arrange: the first attempt is throttled with "Retry-After: 1", the second succeeds. The backend's
		// base backoff is zero (CreateSut → OptionsWith), so a retry would otherwise be immediate.
		int calls = 0;
		ScriptedHandler handler = new(_ =>
		{
			calls++;
			if (calls > 1) return Json(HttpStatusCode.OK);

			HttpResponseMessage throttled = Json(HttpStatusCode.TooManyRequests);
			throttled.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
			return throttled;
		});
		OpenAiCapabilityProber sut = CreateSut(handler, maxProbeRetries: 1);

		// Act
		var stopwatch = Stopwatch.StartNew();
		bool? result = await sut.ProbeToolSupportAsync(
			               new BackendContext(BackendName),
			               ModelId,
			               CancellationToken.None);
		stopwatch.Stop();

		// Assert: the retry produced a conclusive positive after exactly two attempts.
		Assert.True(result);
		Assert.Equal(2, calls);

		// Assert: the prober waited roughly the server-requested second before retrying — proof the header was
		// honored over the zero-second configured backoff. A generous lower bound keeps the test non-flaky.
		Assert.True(
			stopwatch.Elapsed >= TimeSpan.FromMilliseconds(750),
			$"Expected the prober to honor the 1s Retry-After, but it retried after only {stopwatch.ElapsedMilliseconds} ms.");
	}

	/// <summary>
	/// Verifies that a cooldown one probe earns from an HTTP 429 is shared across the backend: a concurrent
	/// sibling probe that never hits the rate limit itself still waits the cooldown out before issuing its own
	/// request. This is what keeps a parallel model fan-out from having every sibling re-trip the same limit.
	/// </summary>
	[Fact]
	public async Task ProbeAsync_WhenSiblingHitsRateLimit_SharesCooldownAcrossBackend()
	{
		// Arrange: the tool probe is throttled once (Retry-After: 1s) then succeeds; the vision probe is always
		// answered 200. The two share one prober instance and one backend, so they share the cooldown store.
		RetryAfterSiblingHandler handler = new();
		OpenAiCapabilityProber sut = CreateSut(handler, maxProbeRetries: 1, timeoutSeconds: 10);
		BackendContext backend = new(BackendName);

		// Act: start the tool probe; it earns the 429 and publishes a backend-wide cooldown.
		Task<bool?> toolProbe = sut.ProbeToolSupportAsync(backend, ModelId, CancellationToken.None);

		// The throttle is signalled from INSIDE the handler as the 429 is built — before that response has
		// propagated back through the prober to publish the shared cooldown. Sleeping a fixed span here and
		// hoping publication has happened is exactly what made this test flaky under CI load (the sibling read
		// an empty store and fired at 0 ms). Instead, wait on the prober's own cooldown view until the 429 has
		// actually been shared. The bounded wait keeps a real regression — a cooldown that is never published —
		// fast to fail instead of hanging.
		await handler.ToolThrottled;
		var cooldownPublished = Stopwatch.StartNew();
		while (!sut.HasActiveBackendCooldown(backend.Name) && cooldownPublished.Elapsed < TimeSpan.FromSeconds(5))
			await Task.Delay(10);

		Assert.True(
			sut.HasActiveBackendCooldown(backend.Name),
			"The tool probe's 429 never published a shared backend cooldown for the sibling to observe.");

		// The vision probe never receives a 429 and the configured base backoff is zero, so the ONLY thing that
		// can delay it is the cooldown the tool probe published.
		var visionStopwatch = Stopwatch.StartNew();
		bool? visionResult = await sut.ProbeVisionSupportAsync(backend, ModelId, CancellationToken.None);
		visionStopwatch.Stop();

		bool? toolResult = await toolProbe;

		// Assert: both probes ended conclusively positive.
		Assert.True(toolResult);
		Assert.True(visionResult);

		// Assert: the innocent sibling waited out the shared cooldown rather than firing immediately, proving the
		// 429 one probe earned paced the other. A generous lower bound keeps the test non-flaky.
		Assert.True(
			visionStopwatch.Elapsed >= TimeSpan.FromMilliseconds(500),
			$"Sibling vision probe should have waited the shared cooldown but fired after only {visionStopwatch.ElapsedMilliseconds} ms.");
	}

	#endregion

	/// <summary>
	/// A handler whose first attempt blocks until the prober's per-attempt timeout cancels it, then
	/// answers promptly with the supplied response on any later attempt. The later-attempt path is the
	/// control: because a timeout is no longer retried, a correct prober never reaches it, so the test
	/// asserts the handler saw exactly one call. Avoids depending on a fake clock.
	/// </summary>
	/// <param name="onRetry">Factory producing the response a (never-taken) later attempt would receive.</param>
	private sealed class FirstAttemptTimesOutHandler(Func<HttpResponseMessage> onRetry) : HttpMessageHandler
	{
		private int mCalls;

		/// <summary>Gets the number of attempts the handler has observed.</summary>
		public int Calls => mCalls;

		/// <summary>
		/// Blocks the first attempt until its linked timeout token fires; returns the supplied response on any
		/// later attempt (which a correct prober never takes after a timeout).
		/// </summary>
		/// <param name="request">The outgoing request (ignored).</param>
		/// <param name="cancellationToken">The prober's linked per-attempt timeout token.</param>
		/// <returns>The supplied response on a second or later attempt.</returns>
		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken  cancellationToken)
		{
			int call = Interlocked.Increment(ref mCalls);

			// First attempt: wait out the per-attempt timeout so the prober observes a timeout cancellation.
			if (call == 1) await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);

			return onRetry();
		}
	}

	/// <summary>
	/// Routes by endpoint to exercise the shared backend cooldown: the FIRST tool-completion request is
	/// answered with an HTTP 429 carrying a one-second <c>Retry-After</c> (and the throttle is signalled so the
	/// test can sequence the innocent sibling), every later request — the tool retry and all vision requests —
	/// is answered 200. The embeddings endpoint is irrelevant here and also answered 200. Tools and vision both
	/// post to <c>chat/completions</c>, so they are told apart by the vision probe's image content part.
	/// </summary>
	private sealed class RetryAfterSiblingHandler : HttpMessageHandler
	{
		private readonly TaskCompletionSource mToolThrottled =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		private int mChatCalls;

		/// <summary>Completes once the backend has answered the first tool probe with its throttle.</summary>
		public Task ToolThrottled => mToolThrottled.Task;

		/// <summary>
		/// Answers the first tool-completion request with a throttling response and every later request with a
		/// success, telling the tool and vision probes apart by the vision probe's multimodal content array.
		/// </summary>
		/// <param name="request">The outgoing probe request.</param>
		/// <param name="cancellationToken">A token to cancel the read of the request body.</param>
		/// <returns>The scripted response for the request.</returns>
		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken  cancellationToken)
		{
			// A vision probe carries its image as a content array; a tool probe sends a plain string content.
			string body = request.Content is null
				              ? string.Empty
				              : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			bool isVision = body.Contains("image_url", StringComparison.Ordinal);

			// Throttle only the first tool attempt; signal it so the test can release the sibling afterwards.
			if (!isVision && Interlocked.Increment(ref mChatCalls) == 1)
			{
				HttpResponseMessage throttled = new(HttpStatusCode.TooManyRequests)
					{ Content = new StringContent("{}", Encoding.UTF8, "application/json") };
				throttled.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
				mToolThrottled.TrySetResult();
				return throttled;
			}

			return new HttpResponseMessage(HttpStatusCode.OK)
				{ Content = new StringContent("{}", Encoding.UTF8, "application/json") };
		}
	}

	/// <summary>Captures the request and returns a scripted response (or throws to simulate transport failure).</summary>
	private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
	{
		/// <summary>Gets the most recent request the handler observed.</summary>
		public HttpRequestMessage? LastRequest { get; private set; }

		/// <summary>Gets the body of the most recent request, if it carried content.</summary>
		public string? LastRequestBody { get; private set; }

		/// <summary>Records the request, captures its body, then returns the scripted response.</summary>
		/// <param name="request">The outgoing request to capture.</param>
		/// <param name="cancellationToken">A token to cancel the operation.</param>
		/// <returns>The response produced by the configured responder.</returns>
		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken  cancellationToken)
		{
			// Honor cancellation first so the caller-cancellation path is observable to the prober.
			cancellationToken.ThrowIfCancellationRequested();

			LastRequest = request;
			if (request.Content is not null)
				LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);

			return responder(request);
		}
	}

	/// <summary>Hands out a single <see cref="HttpClient"/> over the supplied handler with a test base address.</summary>
	private sealed class StubHttpClientProvider(HttpMessageHandler handler) : IBackendHttpClientProvider
	{
		/// <summary>Creates an HTTP client over the handler with a fixed test base address.</summary>
		/// <param name="backendName">The logical backend name (ignored by the stub).</param>
		/// <returns>An <see cref="HttpClient"/> backed by the handler.</returns>
		public HttpClient CreateClient(string backendName) => new(handler, disposeHandler: false)
			{ BaseAddress = new Uri("https://mock.test/v1/") };
	}

	/// <summary>A minimal <see cref="IOptionsMonitor{TOptions}"/> over a fixed snapshot.</summary>
	private sealed class StaticOptionsMonitor(ProxyOptions value) : IOptionsMonitor<ProxyOptions>
	{
		/// <summary>Gets the fixed options snapshot.</summary>
		public ProxyOptions CurrentValue { get; } = value;

		/// <summary>Returns the fixed snapshot regardless of the requested name.</summary>
		/// <param name="name">The options name (ignored).</param>
		/// <returns>The fixed options snapshot.</returns>
		public ProxyOptions Get(string? name) => CurrentValue;

		/// <summary>Returns <see langword="null"/> because the snapshot never changes.</summary>
		/// <param name="listener">The change listener (ignored).</param>
		/// <returns><see langword="null"/>; no change notifications are raised.</returns>
		public IDisposable? OnChange(Action<ProxyOptions, string?> listener) => null;
	}
}
