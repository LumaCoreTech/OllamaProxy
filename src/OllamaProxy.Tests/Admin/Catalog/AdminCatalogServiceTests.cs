// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Admin.Catalog;
using OllamaProxy.Core;
using OllamaProxy.Hosting.Cascade;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Tests.Admin.Catalog;

/// <summary>
/// Unit tests for <see cref="AdminCatalogService"/>, the chassis-side reader that turns the supervisor's
/// live-catalog read into the UI's ready/not-ready <see cref="LiveCatalog"/> result.
/// </summary>
public sealed class AdminCatalogServiceTests
{
	/// <summary>
	/// Verifies the constructor rejects a <see langword="null"/> supervisor.
	/// </summary>
	[Fact]
	public void Constructor_WhenSupervisorIsNull_ThrowsArgumentNullException()
	{
		var exception = Assert.Throws<ArgumentNullException>(() => new AdminCatalogService(null!));
		Assert.Equal("supervisor", exception.ParamName);
	}

	/// <summary>
	/// Verifies that a <see langword="null"/> live-model read (no inner host serving) maps to the not-ready
	/// state with an empty model list — the signal the UI shows as a transient message.
	/// </summary>
	[Fact]
	public void GetLiveCatalog_WhenNoHostServing_ReturnsNotReady()
	{
		// Arrange: a supervisor reporting no live catalog.
		AdminCatalogService sut = new(new FakeSupervisor(liveModels: null));

		// Act
		LiveCatalog catalog = sut.GetLiveCatalog();

		// Assert: not ready, empty list.
		Assert.False(catalog.ProxyReady);
		Assert.Empty(catalog.Models);
	}

	/// <summary>
	/// Verifies that a non-null live-model read maps to the ready state carrying those models verbatim.
	/// </summary>
	[Fact]
	public void GetLiveCatalog_WhenHostServing_ReturnsReadyWithModels()
	{
		// Arrange: a supervisor reporting a live catalog.
		RegisteredModel model = new("gpt-4o", "cloud", "gpt-4o", ModelCapabilities.CompletionOnly, 128_000);
		AdminCatalogService sut = new(new FakeSupervisor(liveModels: [model]));

		// Act
		LiveCatalog catalog = sut.GetLiveCatalog();

		// Assert: ready, models surfaced verbatim.
		Assert.True(catalog.ProxyReady);
		Assert.Equal(model, Assert.Single(catalog.Models));
	}

	/// <summary>
	/// A test double for <see cref="IProxyHostSupervisor"/> that returns a fixed live-catalog read; its
	/// lifecycle members are never exercised by <see cref="AdminCatalogService"/>.
	/// </summary>
	private sealed class FakeSupervisor : IProxyHostSupervisor
	{
		private readonly IReadOnlyList<RegisteredModel>? mLiveModels;

		public FakeSupervisor(IReadOnlyList<RegisteredModel>? liveModels) => mLiveModels = liveModels;

		public bool IsInnerHostActive => mLiveModels is not null;

		public IReadOnlyList<RegisteredModel>? GetLiveModels() => mLiveModels;

		public Task<RecycleResult> RecycleAsync(CancellationToken cancellationToken) =>
			throw new NotSupportedException("RecycleAsync is not exercised by AdminCatalogService.");

		public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

		public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
	}
}
