// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Configuration;
using OllamaProxy.Providers.Abstractions;
using OllamaProxy.Providers.Http;

namespace OllamaProxy.Tests.Providers.Http;

/// <summary>
/// Tests for the default <see cref="IBackendHttpClientProvider.CreateClient(BackendContext)"/>
/// implementation — the behavior an implementer inherits when it only supplies the name overload. The
/// default exists so existing committed-only providers keep working unchanged, while a draft context
/// fails loudly rather than being silently misrouted to a committed backend's client.
/// </summary>
[Trait("Category", "Unit")]
public sealed class IBackendHttpClientProviderTests
{
	/// <summary>
	/// Verifies that the default <see cref="IBackendHttpClientProvider.CreateClient(BackendContext)"/>
	/// routes a committed context (no inline draft) to the name overload an implementer supplies.
	/// </summary>
	[Fact]
	public void CreateClient_WhenContextIsCommitted_DelegatesToNameOverload()
	{
		// Arrange: an implementer that only supplies the name overload, inheriting the default context one.
		// The SUT is typed as the interface so the default CreateClient(BackendContext) is in scope.
		NameOnlyProvider provider = new();
		IBackendHttpClientProvider sut = provider;

		// Act
		using HttpClient client = sut.CreateClient(new BackendContext("cloud"));

		// Assert: the default unwrapped the context to its name and called the name overload with it.
		Assert.Equal("cloud", provider.LastBackendName);
	}

	/// <summary>
	/// Verifies that the default <see cref="IBackendHttpClientProvider.CreateClient(BackendContext)"/>
	/// throws for a draft context, since an implementer that has not opted in cannot build an ad-hoc
	/// client and must not silently fall back to the name path.
	/// </summary>
	[Fact]
	public void CreateClient_WhenContextIsDraft_ThrowsNotSupportedException()
	{
		// Arrange: typed as the interface so the inherited default context overload is in scope.
		NameOnlyProvider provider = new();
		IBackendHttpClientProvider sut = provider;
		BackendOptions draft = new() { BaseUrl = "https://draft.test/v1", ProviderType = "openai" };

		// Act + Assert: the draft is rejected, and the name overload was never consulted.
		var exception =
			Assert.Throws<NotSupportedException>(() => sut.CreateClient(new BackendContext("(draft)", draft)));
		Assert.Equal(
			"This IBackendHttpClientProvider does not support draft backend contexts. " +
			"Override CreateClient(BackendContext) to build an ad-hoc client from the inline options.",
			exception.Message);
		Assert.Null(provider.LastBackendName);
	}

	/// <summary>
	/// Verifies that the default <see cref="IBackendHttpClientProvider.CreateClient(BackendContext)"/>
	/// rejects a <see langword="null"/> context.
	/// </summary>
	[Fact]
	public void CreateClient_WhenContextIsNull_ThrowsArgumentNullException()
	{
		// Arrange: typed as the interface so the inherited default context overload is in scope.
		IBackendHttpClientProvider sut = new NameOnlyProvider();

		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() => sut.CreateClient((BackendContext)null!));
		Assert.Equal("backend", exception.ParamName);
	}

	/// <summary>
	/// A minimal <see cref="IBackendHttpClientProvider"/> that implements only the name overload and
	/// inherits the default context overload. It records the last name it was asked for so tests can
	/// prove whether the default delegated to it or short-circuited.
	/// </summary>
	private sealed class NameOnlyProvider : IBackendHttpClientProvider
	{
		/// <summary>Gets the most recent backend name the name overload received, or <see langword="null"/>.</summary>
		public string? LastBackendName { get; private set; }

		/// <summary>Records the requested name and returns a throwaway client.</summary>
		/// <param name="backendName">The requested backend name.</param>
		/// <returns>A new, unconfigured <see cref="HttpClient"/>.</returns>
		public HttpClient CreateClient(string backendName)
		{
			LastBackendName = backendName;
			return new HttpClient();
		}
	}
}
