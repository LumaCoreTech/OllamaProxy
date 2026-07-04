// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Net;

using OllamaProxy.Endpoints;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Tests.Endpoints;

/// <summary>
/// Tests for the pure status-mapping helper on <see cref="OpenAiHttp"/>, which mirrors the Ollama
/// surface's rule for the OpenAI-compatible endpoints: a genuine client error (4xx) is reported with
/// the backend's own status, while everything else is normalized to a gateway error so the client can
/// tell an upstream problem apart from a bad request.
/// </summary>
[Trait("Category", "Unit")]
public sealed class OpenAiHttpTests
{
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
}
