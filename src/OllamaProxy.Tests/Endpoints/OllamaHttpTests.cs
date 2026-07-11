// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Net;

using Microsoft.AspNetCore.Http;

using OllamaProxy.Endpoints;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Tests.Endpoints;

/// <summary>
/// Tests for the shared HTTP helpers on <see cref="OllamaHttp"/>: the pure status-mapping rule
/// (<see cref="OllamaHttp.MapProviderStatus"/>) that decides whether an upstream failure keeps the backend's
/// status or normalizes to a gateway error, the Ollama-shaped error writer
/// (<see cref="OllamaHttp.WriteErrorAsync"/>) including its already-started skip, and the newline-delimited
/// JSON stream writer (<see cref="OllamaHttp.WriteJsonLineAsync"/>).
/// </summary>
[Trait("Category", "Unit")]
public sealed class OllamaHttpTests
{
	#region MapProviderStatus

	/// <summary>
	/// Verifies that <see cref="OllamaHttp.MapProviderStatus"/> passes a genuine client error (4xx)
	/// through unchanged so the caller can correct its request.
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
		HttpStatusCode mapped = OllamaHttp.MapProviderStatus(exception);

		// Assert
		Assert.Equal(status, mapped);
	}

	/// <summary>
	/// Verifies that <see cref="OllamaHttp.MapProviderStatus"/> normalizes a server-side or otherwise
	/// non-client status to <see cref="HttpStatusCode.BadGateway"/>, signaling an upstream problem.
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
		ProviderException exception = new(status, "upstream broke");

		// Act
		HttpStatusCode mapped = OllamaHttp.MapProviderStatus(exception);

		// Assert
		Assert.Equal(HttpStatusCode.BadGateway, mapped);
	}

	/// <summary>
	/// Verifies that <see cref="OllamaHttp.MapProviderStatus"/> rejects a <see langword="null"/>
	/// exception.
	/// </summary>
	[Fact]
	public void MapProviderStatus_WhenExceptionIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() => OllamaHttp.MapProviderStatus(null!));
		Assert.Equal("exception", exception.ParamName);
	}

	#endregion

	#region WriteErrorAsync

	/// <summary>
	/// Verifies that <see cref="OllamaHttp.WriteErrorAsync"/> sets the status and writes an Ollama-shaped
	/// <c>{ "error": ... }</c> body when the response has not yet started.
	/// </summary>
	[Fact]
	public async Task WriteErrorAsync_WhenResponseNotStarted_WritesStatusAndBody()
	{
		// Arrange
		using MemoryStream body = new();
		DefaultHttpContext context = HttpTestContext.Create(body);

		// Act
		await OllamaHttp.WriteErrorAsync(
			context,
			HttpStatusCode.BadGateway,
			"upstream unavailable",
			CancellationToken.None);

		// Assert: the status, content type, and Ollama error envelope all land as written.
		Assert.Equal((int)HttpStatusCode.BadGateway, context.Response.StatusCode);
		Assert.StartsWith("application/json", context.Response.ContentType);
		Assert.Equal("""{"error":"upstream unavailable"}""", HttpTestContext.ReadBody(body));
	}

	/// <summary>
	/// Verifies that <see cref="OllamaHttp.WriteErrorAsync"/> writes nothing once the response has started,
	/// so a mid-stream failure does not attempt to rewrite headers already on the wire.
	/// </summary>
	[Fact]
	public async Task WriteErrorAsync_WhenResponseAlreadyStarted_WritesNothing()
	{
		// Arrange: a response feature that reports HasStarted, the mid-stream condition.
		using MemoryStream body = new();
		DefaultHttpContext context = HttpTestContext.CreateStarted(body);

		// Act
		await OllamaHttp.WriteErrorAsync(
			context,
			HttpStatusCode.BadGateway,
			"too late",
			CancellationToken.None);

		// Assert: nothing was written to the body since headers were already committed.
		Assert.Empty(body.ToArray());
	}

	/// <summary>
	/// Verifies that <see cref="OllamaHttp.WriteErrorAsync"/> rejects a <see langword="null"/> context.
	/// </summary>
	[Fact]
	public async Task WriteErrorAsync_WhenContextIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
			                OllamaHttp.WriteErrorAsync(null!, HttpStatusCode.BadGateway, "x", CancellationToken.None));
		Assert.Equal("context", exception.ParamName);
	}

	#endregion

	#region WriteJsonLineAsync

	/// <summary>
	/// Verifies that <see cref="OllamaHttp.WriteJsonLineAsync"/> serializes the value and terminates it with a
	/// single newline, the framing an Ollama NDJSON stream client parses one object per line.
	/// </summary>
	[Fact]
	public async Task WriteJsonLineAsync_WhenCalled_WritesJsonFollowedByNewline()
	{
		// Arrange
		using MemoryStream body = new();
		DefaultHttpContext context = HttpTestContext.Create(body);

		// Act
		await OllamaHttp.WriteJsonLineAsync(context, new { done = true }, CancellationToken.None);

		// Assert: exactly the serialized object plus one trailing newline.
		Assert.Equal("{\"done\":true}\n", HttpTestContext.ReadBody(body));
	}

	/// <summary>
	/// Verifies that <see cref="OllamaHttp.WriteJsonLineAsync"/> rejects a <see langword="null"/> context.
	/// </summary>
	[Fact]
	public async Task WriteJsonLineAsync_WhenContextIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
			                OllamaHttp.WriteJsonLineAsync<object>(null!, new { }, CancellationToken.None));
		Assert.Equal("context", exception.ParamName);
	}

	#endregion
}
