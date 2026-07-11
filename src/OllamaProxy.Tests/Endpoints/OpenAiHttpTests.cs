// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Net;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Http;

using OllamaProxy.Endpoints;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Tests.Endpoints;

/// <summary>
/// Tests for the shared HTTP helpers on <see cref="OpenAiHttp"/>, the OpenAI-compatible counterparts to
/// <see cref="OllamaHttp"/>: the status-mapping rule (<see cref="OpenAiHttp.MapProviderStatus"/>) that reports a
/// genuine client error with the backend's own status while normalizing everything else to a gateway error, the
/// OpenAI-shaped error envelope writer (<see cref="OpenAiHttp.WriteErrorAsync"/>) including its already-started
/// skip, and the Server-Sent-Events frame writers (<see cref="OpenAiHttp.WriteSseFrameAsync"/> and
/// <see cref="OpenAiHttp.WriteSseDoneAsync"/>).
/// </summary>
[Trait("Category", "Unit")]
public sealed class OpenAiHttpTests
{
	#region MapProviderStatus

	/// <summary>
	/// Verifies that <see cref="OpenAiHttp.MapProviderStatus"/> passes a genuine client error (4xx)
	/// through unchanged.
	/// </summary>
	/// <param name="status">The upstream client-error status.</param>
	[Theory]
	[InlineData(HttpStatusCode.BadRequest)]
	[InlineData(HttpStatusCode.Unauthorized)]
	[InlineData(HttpStatusCode.NotFound)]
	[InlineData((HttpStatusCode)429)]
	public void MapProviderStatus_WhenClientError_PassesStatusThrough(HttpStatusCode status)
	{
		// Arrange
		ProviderException exception = new(status, "upstream said no");

		// Act
		HttpStatusCode mapped = OpenAiHttp.MapProviderStatus(exception);

		// Assert
		Assert.Equal(status, mapped);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiHttp.MapProviderStatus"/> normalizes a server-side or otherwise
	/// non-client status to <see cref="HttpStatusCode.BadGateway"/>.
	/// </summary>
	/// <param name="status">The upstream non-client status.</param>
	[Theory]
	[InlineData(HttpStatusCode.InternalServerError)]
	[InlineData(HttpStatusCode.BadGateway)]
	[InlineData(HttpStatusCode.ServiceUnavailable)]
	[InlineData(HttpStatusCode.OK)]
	public void MapProviderStatus_WhenNonClientError_NormalizesToBadGateway(HttpStatusCode status)
	{
		// Arrange
		ProviderException exception = new(status, "upstream trouble");

		// Act
		HttpStatusCode mapped = OpenAiHttp.MapProviderStatus(exception);

		// Assert
		Assert.Equal(HttpStatusCode.BadGateway, mapped);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiHttp.MapProviderStatus"/> rejects a <see langword="null"/> exception.
	/// </summary>
	[Fact]
	public void MapProviderStatus_WhenExceptionIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() => OpenAiHttp.MapProviderStatus(null!));
		Assert.Equal("exception", exception.ParamName);
	}

	#endregion

	#region WriteErrorAsync

	/// <summary>
	/// Verifies that <see cref="OpenAiHttp.WriteErrorAsync"/> sets the status and writes the OpenAI-shaped
	/// <c>{ "error": { "message", "type" } }</c> envelope when the response has not yet started.
	/// </summary>
	[Fact]
	public async Task WriteErrorAsync_WhenResponseNotStarted_WritesStatusAndEnvelope()
	{
		// Arrange
		using MemoryStream body = new();
		DefaultHttpContext context = HttpTestContext.Create(body);

		// Act
		await OpenAiHttp.WriteErrorAsync(
			context,
			HttpStatusCode.BadGateway,
			"upstream unavailable",
			"api_error",
			CancellationToken.None);

		// Assert: status, content type, and the nested error envelope all land as written.
		Assert.Equal((int)HttpStatusCode.BadGateway, context.Response.StatusCode);
		Assert.Equal("application/json", context.Response.ContentType);

		JsonObject error = JsonNode.Parse(HttpTestContext.ReadBody(body))!.AsObject()["error"]!.AsObject();
		Assert.Equal("upstream unavailable", (string?)error["message"]);
		Assert.Equal("api_error", (string?)error["type"]);
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiHttp.WriteErrorAsync"/> writes nothing once the response has started, so a
	/// mid-stream failure does not attempt to rewrite headers already on the wire.
	/// </summary>
	[Fact]
	public async Task WriteErrorAsync_WhenResponseAlreadyStarted_WritesNothing()
	{
		// Arrange: a response feature that reports HasStarted, the mid-stream condition.
		using MemoryStream body = new();
		DefaultHttpContext context = HttpTestContext.CreateStarted(body);

		// Act
		await OpenAiHttp.WriteErrorAsync(
			context,
			HttpStatusCode.BadGateway,
			"too late",
			"api_error",
			CancellationToken.None);

		// Assert
		Assert.Empty(body.ToArray());
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiHttp.WriteErrorAsync"/> rejects a <see langword="null"/> context.
	/// </summary>
	[Fact]
	public async Task WriteErrorAsync_WhenContextIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = await Assert.ThrowsAsync<ArgumentNullException>(() => OpenAiHttp.WriteErrorAsync(
			                null!,
			                HttpStatusCode.BadGateway,
			                "x",
			                "api_error",
			                CancellationToken.None));
		Assert.Equal("context", exception.ParamName);
	}

	#endregion

	#region WriteSseFrameAsync

	/// <summary>
	/// Verifies that <see cref="OpenAiHttp.WriteSseFrameAsync"/> wraps the payload in a <c>data: </c> prefix and
	/// the blank-line frame terminator, the SSE framing an OpenAI stream client parses.
	/// </summary>
	[Fact]
	public async Task WriteSseFrameAsync_WhenCalled_WritesPrefixedPayloadAndSeparator()
	{
		// Arrange
		using MemoryStream body = new();
		DefaultHttpContext context = HttpTestContext.Create(body);

		// Act
		await OpenAiHttp.WriteSseFrameAsync(context, """{"id":"c1"}""", CancellationToken.None);

		// Assert
		Assert.Equal("data: {\"id\":\"c1\"}\n\n", HttpTestContext.ReadBody(body));
	}

	/// <summary>
	/// Verifies that <see cref="OpenAiHttp.WriteSseFrameAsync"/> rejects a <see langword="null"/> context.
	/// </summary>
	[Fact]
	public async Task WriteSseFrameAsync_WhenContextIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
			                OpenAiHttp.WriteSseFrameAsync(null!, "x", CancellationToken.None));
		Assert.Equal("context", exception.ParamName);
	}

	#endregion

	#region WriteSseDoneAsync

	/// <summary>
	/// Verifies that <see cref="OpenAiHttp.WriteSseDoneAsync"/> writes the terminating <c>data: [DONE]</c> frame
	/// that signals the end of an OpenAI stream.
	/// </summary>
	[Fact]
	public async Task WriteSseDoneAsync_WhenCalled_WritesDoneSentinelFrame()
	{
		// Arrange
		using MemoryStream body = new();
		DefaultHttpContext context = HttpTestContext.Create(body);

		// Act
		await OpenAiHttp.WriteSseDoneAsync(context, CancellationToken.None);

		// Assert
		Assert.Equal("data: [DONE]\n\n", HttpTestContext.ReadBody(body));
	}

	#endregion
}
