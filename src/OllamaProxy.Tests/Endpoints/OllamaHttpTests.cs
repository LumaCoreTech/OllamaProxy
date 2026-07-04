// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Net;

using OllamaProxy.Endpoints;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Tests.Endpoints;

/// <summary>
/// Tests for the pure status-mapping helper on <see cref="OllamaHttp"/>, which decides whether an
/// upstream provider failure is reported to the client with the backend's own status or normalized to
/// a gateway error. The story covers the pass-through window (genuine 4xx) and the normalization of
/// everything else to <see cref="HttpStatusCode.BadGateway"/>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class OllamaHttpTests
{
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
}
