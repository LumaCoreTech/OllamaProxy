// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Net.Http.Headers;

using OllamaProxy.Configuration;
using OllamaProxy.Providers.Abstractions;
using OllamaProxy.Providers.Http;

namespace OllamaProxy.Tests.Providers.Http;

/// <summary>
/// Tests for <see cref="BackendHttpClientProvider"/>, the production seam that turns a backend identity
/// into a configured <see cref="HttpClient"/>. The story follows the two shapes a request can take:
/// <list type="number">
///     <item>
///         <description>
///         The name path (<see cref="BackendHttpClientProvider.CreateClient(string)"/>) resolves a
///         pre-registered named client from the factory and rejects blank names (WhenBackendName...).
///         </description>
///     </item>
///     <item>
///         <description>
///         The context path (<see cref="BackendHttpClientProvider.CreateClient(BackendContext)"/>) routes a
///         committed context to the same named client, but builds a fresh ad-hoc client — configured from
///         the inline draft options, never the factory — for a draft context (WhenContext...).
///         </description>
///     </item>
/// </list>
/// </summary>
[Trait("Category", "Unit")]
public sealed class BackendHttpClientProviderTests
{
	private const string DraftBaseUrl = "https://draft.test/v1";

	private const string DraftApiKey = "draft-secret-key";

	#region CreateClient(string)

	/// <summary>
	/// Verifies that <see cref="BackendHttpClientProvider.CreateClient(string)"/> requests the named
	/// client whose key follows the shared <see cref="BackendHttpClientNames"/> convention.
	/// </summary>
	[Fact]
	public void CreateClient_WhenBackendNameGiven_ResolvesNamedClientFromFactory()
	{
		// Arrange
		RecordingHttpClientFactory factory = new();
		BackendHttpClientProvider sut = new(factory);

		// Act
		using HttpClient client = sut.CreateClient("cloud");

		// Assert: the factory was asked for exactly the conventional backend client name.
		Assert.Equal(BackendHttpClientNames.ForBackend("cloud"), factory.LastRequestedName);
	}

	/// <summary>
	/// Verifies that <see cref="BackendHttpClientProvider.CreateClient(string)"/> rejects an empty or
	/// whitespace backend name.
	/// </summary>
	/// <param name="backendName">The invalid backend name to resolve.</param>
	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void CreateClient_WhenBackendNameIsEmptyOrWhiteSpace_ThrowsArgumentException(string backendName)
	{
		// Arrange
		BackendHttpClientProvider sut = new(new RecordingHttpClientFactory());

		// Act + Assert
		var exception = Assert.Throws<ArgumentException>(() => sut.CreateClient(backendName));
		Assert.Equal("backendName", exception.ParamName);
	}

	#endregion

	#region CreateClient(BackendContext)

	/// <summary>
	/// Verifies that <see cref="BackendHttpClientProvider.CreateClient(BackendContext)"/> routes a
	/// committed context (no inline draft) to the pre-registered named client rather than building one.
	/// </summary>
	[Fact]
	public void CreateClient_WhenContextIsCommitted_ResolvesNamedClientFromFactory()
	{
		// Arrange
		RecordingHttpClientFactory factory = new();
		BackendHttpClientProvider sut = new(factory);

		// Act
		using HttpClient client = sut.CreateClient(new BackendContext("cloud"));

		// Assert: a committed context behaves exactly like the name path — the factory is consulted.
		Assert.Equal(BackendHttpClientNames.ForBackend("cloud"), factory.LastRequestedName);
	}

	/// <summary>
	/// Verifies that <see cref="BackendHttpClientProvider.CreateClient(BackendContext)"/> builds a fresh
	/// ad-hoc client for a draft context — never consulting the factory — and configures it from the
	/// inline draft options (base address with the required trailing slash and the bearer credential).
	/// </summary>
	[Fact]
	public void CreateClient_WhenContextIsDraft_BuildsAdHocClientFromDraftOptions()
	{
		// Arrange: a draft context carrying inline options for a backend that is not yet committed.
		RecordingHttpClientFactory factory = new();
		BackendHttpClientProvider sut = new(factory);
		BackendOptions draft = new() { BaseUrl = DraftBaseUrl, ProviderType = "openai", ApiKey = DraftApiKey };

		// Act
		using HttpClient client = sut.CreateClient(new BackendContext("(draft)", draft));

		// Assert: the factory was bypassed entirely, and the client carries the draft's wire configuration.
		Assert.Null(factory.LastRequestedName);
		Assert.Equal(new Uri(DraftBaseUrl + "/"), client.BaseAddress);
		Assert.Equal(new AuthenticationHeaderValue("Bearer", DraftApiKey), client.DefaultRequestHeaders.Authorization);
		Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
	}

	/// <summary>
	/// Verifies that <see cref="BackendHttpClientProvider.CreateClient(BackendContext)"/> rejects a
	/// <see langword="null"/> context.
	/// </summary>
	[Fact]
	public void CreateClient_WhenContextIsNull_ThrowsArgumentNullException()
	{
		// Arrange
		BackendHttpClientProvider sut = new(new RecordingHttpClientFactory());

		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() => sut.CreateClient((BackendContext)null!));
		Assert.Equal("backend", exception.ParamName);
	}

	#endregion

	/// <summary>
	/// An <see cref="IHttpClientFactory"/> stub that records the most recent requested client name and
	/// hands back a throwaway <see cref="HttpClient"/>. Used to prove whether the provider consulted the
	/// factory (committed path) or bypassed it to build an ad-hoc client (draft path).
	/// </summary>
	private sealed class RecordingHttpClientFactory : IHttpClientFactory
	{
		/// <summary>Gets the name of the most recent client request, or <see langword="null"/> if never called.</summary>
		public string? LastRequestedName { get; private set; }

		/// <summary>Records the requested name and returns a throwaway client.</summary>
		/// <param name="name">The requested client name.</param>
		/// <returns>A new, unconfigured <see cref="HttpClient"/>.</returns>
		public HttpClient CreateClient(string name)
		{
			LastRequestedName = name;
			return new HttpClient();
		}
	}
}
