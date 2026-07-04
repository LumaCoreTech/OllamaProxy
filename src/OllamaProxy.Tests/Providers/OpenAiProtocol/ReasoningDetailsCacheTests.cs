// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json.Nodes;

using Microsoft.Extensions.Options;

using OllamaProxy.Configuration;
using OllamaProxy.Providers.OpenAiProtocol;

namespace OllamaProxy.Tests.Providers.OpenAiProtocol;

/// <summary>
/// Tests for <see cref="ReasoningDetailsCache"/>, the in-memory sliding-expiration LRU store that carries an
/// opaque <c>reasoning_details</c> blob across a multi-turn tool-call conversation. The story covers the
/// store/retrieve round-trip, defensive isolation of the cached copy from caller mutation, the sliding-TTL
/// expiry (driven deterministically through a manual <see cref="TimeProvider"/>), the least-recently-used
/// eviction once the entry cap is reached, the recency renewal that keeps an actively re-read entry warm, and
/// the disabled-switch escape hatch that turns the cache into a no-op.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ReasoningDetailsCacheTests
{
	private static ReasoningDetailsCache CreateCache(
		TimeProvider timeProvider,
		bool         enabled                  = true,
		int          maxEntries               = 1024,
		int          slidingExpirationSeconds = 300)
	{
		ProxyOptions options = new()
		{
			ReasoningDetailsCache = new ReasoningDetailsCacheOptions
			{
				Enabled = enabled,
				MaxEntries = maxEntries,
				SlidingExpirationSeconds = slidingExpirationSeconds
			}
		};

		return new ReasoningDetailsCache(Options.Create(options), timeProvider);
	}

	private static JsonObject Blob(string marker) => new() { ["signature"] = marker };

	/// <summary>
	/// Verifies that a stored blob is returned by a subsequent retrieve under the same key, value-equal to
	/// what was stored.
	/// </summary>
	[Fact]
	public void Retrieve_AfterStore_ReturnsStoredBlob()
	{
		// Arrange
		ReasoningDetailsCache cache = CreateCache(TimeProvider.System);
		cache.Store("k1", Blob("abc"));

		// Act
		JsonNode? retrieved = cache.Retrieve("k1");

		// Assert
		Assert.NotNull(retrieved);
		Assert.Equal("abc", retrieved["signature"]!.GetValue<string>());
	}

	/// <summary>
	/// Verifies that retrieving a key that was never stored returns <see langword="null"/>, the graceful
	/// miss the re-attach path relies on (restart, different instance, or never-captured turn).
	/// </summary>
	[Fact]
	public void Retrieve_WhenKeyAbsent_ReturnsNull()
	{
		// Arrange
		ReasoningDetailsCache cache = CreateCache(TimeProvider.System);

		// Act / Assert
		Assert.Null(cache.Retrieve("missing"));
	}

	/// <summary>
	/// Verifies that the cache detaches the stored blob from the caller's node, so a later mutation on the
	/// caller's side cannot reach into the retained copy.
	/// </summary>
	[Fact]
	public void Store_DetachesFromCallerNode_SoLaterMutationIsNotObserved()
	{
		// Arrange
		ReasoningDetailsCache cache = CreateCache(TimeProvider.System);
		JsonObject original = new() { ["signature"] = "v1" };
		cache.Store("k1", original);

		// Act: mutate the caller's node after storing.
		original["signature"] = "tampered";

		// Assert: the cached copy is unaffected.
		JsonNode? retrieved = cache.Retrieve("k1");
		Assert.Equal("v1", retrieved!["signature"]!.GetValue<string>());
	}

	/// <summary>
	/// Verifies that each retrieve hands back an independent clone, so a caller mutating the returned node
	/// cannot corrupt the value a later round re-reads.
	/// </summary>
	[Fact]
	public void Retrieve_ReturnsIndependentClone_OnEachCall()
	{
		// Arrange
		ReasoningDetailsCache cache = CreateCache(TimeProvider.System);
		cache.Store("k1", Blob("v1"));

		// Act: mutate the first retrieved copy, then read again.
		JsonNode first = cache.Retrieve("k1")!;
		first["signature"] = "mutated";
		JsonNode? second = cache.Retrieve("k1");

		// Assert
		Assert.Equal("v1", second!["signature"]!.GetValue<string>());
	}

	/// <summary>
	/// Verifies that an entry ages out once the sliding window elapses with no access, so an abandoned
	/// conversation does not pin memory.
	/// </summary>
	[Fact]
	public void Retrieve_AfterSlidingWindowElapses_ReturnsNull()
	{
		// Arrange
		ManualTimeProvider time = new();
		ReasoningDetailsCache cache = CreateCache(time, slidingExpirationSeconds: 60);
		cache.Store("k1", Blob("v1"));

		// Act: advance just past the window without touching the entry.
		time.Advance(TimeSpan.FromSeconds(61));

		// Assert
		Assert.Null(cache.Retrieve("k1"));
	}

	/// <summary>
	/// Verifies that retrieving an entry renews its lifetime (sliding), so an actively re-read entry stays
	/// alive across rounds that each fall within the window.
	/// </summary>
	[Fact]
	public void Retrieve_RenewsSlidingLifetime_KeepingActiveEntryWarm()
	{
		// Arrange
		ManualTimeProvider time = new();
		ReasoningDetailsCache cache = CreateCache(time, slidingExpirationSeconds: 60);
		cache.Store("k1", Blob("v1"));

		// Act: three reads, each 40s apart — never idle for the full 60s window.
		time.Advance(TimeSpan.FromSeconds(40));
		Assert.NotNull(cache.Retrieve("k1"));
		time.Advance(TimeSpan.FromSeconds(40));
		Assert.NotNull(cache.Retrieve("k1"));
		time.Advance(TimeSpan.FromSeconds(40));

		// Assert: still alive 120s after the store because each read renewed the window.
		Assert.NotNull(cache.Retrieve("k1"));
	}

	/// <summary>
	/// Verifies that storing the same key twice refreshes the value rather than adding a second entry.
	/// </summary>
	[Fact]
	public void Store_SameKeyTwice_RefreshesValueInPlace()
	{
		// Arrange
		ReasoningDetailsCache cache = CreateCache(TimeProvider.System);
		cache.Store("k1", Blob("v1"));

		// Act
		cache.Store("k1", Blob("v2"));

		// Assert
		Assert.Equal("v2", cache.Retrieve("k1")!["signature"]!.GetValue<string>());
	}

	/// <summary>
	/// Verifies that once the entry cap is reached, admitting a new entry evicts the least-recently-used
	/// one, bounding memory under a burst of distinct conversations.
	/// </summary>
	[Fact]
	public void Store_WhenCapReached_EvictsLeastRecentlyUsed()
	{
		// Arrange: a cap of two entries.
		ManualTimeProvider time = new();
		ReasoningDetailsCache cache = CreateCache(time, maxEntries: 2);
		cache.Store("k1", Blob("v1"));
		time.Advance(TimeSpan.FromSeconds(1));
		cache.Store("k2", Blob("v2"));
		time.Advance(TimeSpan.FromSeconds(1));

		// Act: a third store overflows the cap; k1 is the least-recently-used and is evicted.
		cache.Store("k3", Blob("v3"));

		// Assert
		Assert.Null(cache.Retrieve("k1"));
		Assert.NotNull(cache.Retrieve("k2"));
		Assert.NotNull(cache.Retrieve("k3"));
	}

	/// <summary>
	/// Verifies that a recent retrieve promotes an entry to most-recently-used, so it survives an eviction
	/// that instead drops the entry which has actually gone cold.
	/// </summary>
	[Fact]
	public void Store_WhenCapReached_KeepsRecentlyReadEntry()
	{
		// Arrange: cap of two, both filled.
		ManualTimeProvider time = new();
		ReasoningDetailsCache cache = CreateCache(time, maxEntries: 2);
		cache.Store("k1", Blob("v1"));
		time.Advance(TimeSpan.FromSeconds(1));
		cache.Store("k2", Blob("v2"));
		time.Advance(TimeSpan.FromSeconds(1));

		// Act: read k1 so it becomes most-recently-used, then overflow with k3.
		Assert.NotNull(cache.Retrieve("k1"));
		time.Advance(TimeSpan.FromSeconds(1));
		cache.Store("k3", Blob("v3"));

		// Assert: k2 (now coldest) was evicted, not the freshly read k1.
		Assert.NotNull(cache.Retrieve("k1"));
		Assert.Null(cache.Retrieve("k2"));
		Assert.NotNull(cache.Retrieve("k3"));
	}

	/// <summary>
	/// Verifies that a disabled cache is a no-op: a store is ignored and a retrieve always misses, so the
	/// escape-hatch switch fully suppresses the round-trip.
	/// </summary>
	[Fact]
	public void DisabledCache_StoreAndRetrieve_AreNoOps()
	{
		// Arrange
		ReasoningDetailsCache cache = CreateCache(TimeProvider.System, enabled: false);

		// Act
		cache.Store("k1", Blob("v1"));

		// Assert
		Assert.Null(cache.Retrieve("k1"));
	}

	/// <summary>
	/// A manually advanced <see cref="TimeProvider"/> so the sliding-expiration and eviction tests drive the
	/// clock deterministically. Only <see cref="GetTimestamp"/> and the timestamp frequency are used by the
	/// cache, so just those are overridden; elapsed time is computed by the base from the two.
	/// </summary>
	private sealed class ManualTimeProvider : TimeProvider
	{
		private long mTimestamp;

		public override long TimestampFrequency => TimeSpan.TicksPerSecond;

		public override long GetTimestamp() => mTimestamp;

		public void Advance(TimeSpan delta) => mTimestamp += delta.Ticks;
	}
}
