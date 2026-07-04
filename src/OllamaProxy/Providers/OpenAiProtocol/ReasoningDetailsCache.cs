// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json.Nodes;

using Microsoft.Extensions.Options;

using OllamaProxy.Configuration;

namespace OllamaProxy.Providers.OpenAiProtocol;

/// <summary>
/// The default <see cref="IReasoningDetailsCache"/>: an in-memory, sliding-expiration, size-capped LRU
/// store shared as a singleton across all requests. Because every entry shares the same configured sliding
/// window, recency order and expiry order coincide, so a single intrusive linked list (most-recently-used
/// at the front) serves both LRU eviction, which drops the least-recently-used entry from the back when the
/// cap is reached, and expiry, which prunes the oldest entries from the back until a live one is met. A
/// single lock guards both the map and the list; the stored nodes are small and the critical sections are
/// O(1), so contention is negligible for the proxy's request rate.
/// <para>
/// Timing uses <see cref="TimeProvider.GetTimestamp"/>/<see cref="TimeProvider.GetElapsedTime(long)"/> so a
/// wall-clock adjustment cannot prematurely expire or indefinitely pin an entry, and so tests can drive
/// expiry deterministically with a fake <see cref="TimeProvider"/>.
/// </para>
/// </summary>
sealed class ReasoningDetailsCache : IReasoningDetailsCache
{
	private readonly bool         mEnabled;
	private readonly int          mMaxEntries;
	private readonly TimeSpan     mSlidingExpiration;
	private readonly TimeProvider mTimeProvider;

	private readonly object                                         mGate = new();
	private readonly Dictionary<string, LinkedListNode<CacheEntry>> mEntries;
	private readonly LinkedList<CacheEntry>                         mRecency = [];

	/// <summary>
	/// Initializes a new instance of the <see cref="ReasoningDetailsCache"/> class.
	/// </summary>
	/// <param name="options">The proxy options carrying the reasoning-details cache settings.</param>
	/// <param name="timeProvider">The clock used for sliding-expiration measurement.</param>
	/// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
	public ReasoningDetailsCache(IOptions<ProxyOptions> options, TimeProvider timeProvider)
	{
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(timeProvider);

		ReasoningDetailsCacheOptions settings = options.Value.ReasoningDetailsCache;
		mEnabled = settings.Enabled;
		mMaxEntries = settings.MaxEntries;
		mSlidingExpiration = TimeSpan.FromSeconds(settings.SlidingExpirationSeconds);
		mTimeProvider = timeProvider;

		// Sized to the cap so a steady-state full cache never rehashes; ordinal comparison because the key is
		// an opaque content hash, never user-facing text.
		mEntries = new Dictionary<string, LinkedListNode<CacheEntry>>(mMaxEntries, StringComparer.Ordinal);
	}

	/// <inheritdoc/>
	public void Store(string correlationKey, JsonNode details)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(correlationKey);
		ArgumentNullException.ThrowIfNull(details);

		if (!mEnabled) return;

		// Detach from any document the caller still holds, so a later mutation on their side cannot reach
		// into the cached copy.
		JsonNode detached = details.DeepClone();
		// ReSharper disable once InconsistentlySynchronizedField
		long timestamp = mTimeProvider.GetTimestamp();

		lock (mGate)
		{
			PurgeExpired();

			if (mEntries.TryGetValue(correlationKey, out LinkedListNode<CacheEntry>? existing))
			{
				// Refresh the value and renew the lifetime, then promote to most-recently-used.
				existing.Value.Details = detached;
				existing.Value.Timestamp = timestamp;
				mRecency.Remove(existing);
				mRecency.AddFirst(existing);
				return;
			}

			LinkedListNode<CacheEntry> node = new(new CacheEntry(correlationKey, detached, timestamp));
			mRecency.AddFirst(node);
			mEntries.Add(correlationKey, node);

			// Enforce the cap by dropping the least-recently-used entry (the tail).
			if (mEntries.Count > mMaxEntries) EvictLeastRecentlyUsed();
		}
	}

	/// <inheritdoc/>
	public JsonNode? Retrieve(string correlationKey)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(correlationKey);

		if (!mEnabled) return null;

		lock (mGate)
		{
			PurgeExpired();

			if (!mEntries.TryGetValue(correlationKey, out LinkedListNode<CacheEntry>? node)) return null;

			// Renew the lifetime (sliding) and promote to most-recently-used so an active conversation keeps
			// the entry warm across its rounds.
			node.Value.Timestamp = mTimeProvider.GetTimestamp();
			mRecency.Remove(node);
			mRecency.AddFirst(node);

			// Hand back a detached copy so the caller can parent it onto an outgoing request without ever
			// mutating the retained one (the same entry may be re-read on a later round).
			return node.Value.Details.DeepClone();
		}
	}

	/// <summary>
	/// Drops expired entries from the tail of the recency list. Because every entry shares one sliding
	/// window, the list is ordered newest-to-oldest, so the first non-expired entry encountered from the
	/// back guarantees everything ahead of it is live too, so the scan can stop there. Callers must hold
	/// <see cref="mGate"/>.
	/// </summary>
	private void PurgeExpired()
	{
		for (LinkedListNode<CacheEntry>? tail = mRecency.Last;
		     tail is not null && mTimeProvider.GetElapsedTime(tail.Value.Timestamp) > mSlidingExpiration;
		     tail = mRecency.Last)
		{
			mRecency.RemoveLast();
			mEntries.Remove(tail.Value.Key);
		}
	}

	/// <summary>
	/// Evicts the least-recently-used entry (the tail of the recency list).
	/// Callers must hold <see cref="mGate"/>.
	/// </summary>
	private void EvictLeastRecentlyUsed()
	{
		LinkedListNode<CacheEntry>? lru = mRecency.Last;
		if (lru is null) return;

		mRecency.RemoveLast();
		mEntries.Remove(lru.Value.Key);
	}

	/// <summary>
	/// A single cached reasoning-details blob with its correlation key and last-access timestamp.
	/// </summary>
	private sealed class CacheEntry(string key, JsonNode details, long timestamp)
	{
		public string Key { get; } = key;

		public JsonNode Details { get; set; } = details;

		public long Timestamp { get; set; } = timestamp;
	}
}
