// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using OllamaProxy.Configuration;
using OllamaProxy.Diagnostics;
using OllamaProxy.Hosting;

namespace OllamaProxy.Tests.Diagnostics;

/// <summary>
/// Tests for <see cref="FileRequestTraceSink"/> trace persistence: the file the operator actually reads, and the
/// names that keep it from clobbering itself.
/// </summary>
/// <remarks>
/// <see cref="FileRequestTraceSink"/> serializes one completed trace per file into the configured directory. Each
/// test writes through the real file system (an isolated temp directory torn down afterwards) and asserts on the
/// bytes that land on disk:
/// <list type="number">
///     <item>
///         <description>
///         Happy path: a completed trace is written as one indented-JSON file carrying the flow metadata and its
///         entries (WhenCalled_PersistsTraceAsJsonFile).
///         </description>
///     </item>
///     <item>
///         <description>
///         File naming: the name disambiguates flows by the *whole* correlation id, not a short prefix, so two
///         requests on one keep-alive connection in the same millisecond do not collide and overwrite each other
///         (WhenCorrelationIdsShareEightCharPrefix); a correlation id carrying a character invalid in a file name
///         (Kestrel's "{conn}:{request}") is sanitized rather than dropped on an IO error
///         (WhenCorrelationIdContainsPathSeparator).
///         </description>
///     </item>
///     <item>
///         <description>
///         Retention (ring buffer): once the directory exceeds <c>MaxFiles</c> the oldest traces are evicted so
///         exactly the newest N survive (WhenDirectoryExceedsMaxFiles); a directory holding exactly the cap is
///         left untouched (WhenDirectoryAtMaxFiles).
///         </description>
///     </item>
///     <item>
///         <description>
///         Argument guards: the constructor rejects null options/dataDirectory/logger, and WriteAsync rejects a
///         null trace (Constructor_When*IsNull, WriteAsync_WhenTraceIsNull).
///         </description>
///     </item>
/// </list>
/// </remarks>
[Trait("Category", "Unit")]
public sealed class FileRequestTraceSinkTests : IDisposable
{
	/// <summary>
	/// The UTC timestamp prefix every file written from a <see cref="DateTimeOffset.UnixEpoch"/>-started
	/// trace carries, in the sink's <c>yyyyMMdd-HHmmssfff</c> format. Fixing the start instant makes the
	/// produced file names fully deterministic, so a test can assert on the exact name.
	/// </summary>
	private const string EpochPrefix = "19700101-000000000";

	private readonly string mDirectory =
		Path.Combine(Path.GetTempPath(), $"ollamaproxy-tracesink-{Guid.NewGuid():N}");

	/// <summary>
	/// Removes the isolated temp directory and every trace file written into it once a test completes.
	/// </summary>
	public void Dispose()
	{
		if (Directory.Exists(mDirectory)) Directory.Delete(mDirectory, recursive: true);
	}

	/// <summary>
	/// Builds a sink writing into this test's isolated temp directory (configured as an absolute path so it
	/// is used verbatim, independent of the data directory base).
	/// </summary>
	/// <param name="maxFiles">The retention cap; large by default so a test never trips eviction unintentionally.</param>
	/// <returns>The configured sink.</returns>
	private FileRequestTraceSink CreateSink(int maxFiles = 10000)
	{
		ProxyOptions proxy = new()
		{
			RequestTracing =
			{
				Directory = mDirectory,
				MaxFiles = maxFiles
			}
		};

		return new FileRequestTraceSink(
			Options.Create(proxy),
			new DataDirectory(AppContext.BaseDirectory),
			NullLogger<FileRequestTraceSink>.Instance);
	}

	/// <summary>
	/// Builds a trace started at the Unix epoch (so its file-name timestamp is the fixed
	/// <see cref="EpochPrefix"/>) carrying the given correlation id.
	/// </summary>
	/// <param name="correlationId">The correlation id that becomes the file-name suffix.</param>
	/// <returns>The trace.</returns>
	private static RequestTrace CreateTrace(string correlationId) => new(
		correlationId,
		DateTimeOffset.UnixEpoch,
		"POST",
		"/api/chat");

	/// <summary>
	/// Builds a trace started at a specific instant, so its file-name timestamp prefix can be ordered relative
	/// to other traces — used by the retention tests, where eviction order is driven by the ordinal name sort.
	/// </summary>
	/// <param name="correlationId">The correlation id that becomes the file-name suffix.</param>
	/// <param name="startedUtc">The start instant that becomes the file-name timestamp prefix.</param>
	/// <returns>The trace.</returns>
	private static RequestTrace CreateTrace(string correlationId, DateTimeOffset startedUtc) => new(
		correlationId,
		startedUtc,
		"POST",
		"/api/chat");

	/// <summary>
	/// Returns the names (without directory) of every <c>.json</c> file in the test directory, ordered
	/// ordinally so a multi-file assertion is deterministic.
	/// </summary>
	/// <returns>The ordered trace file names.</returns>
	private string[] TraceFileNames() => Directory.GetFiles(mDirectory, "*.json")
		.Select(Path.GetFileName)
		.OrderBy(name => name, StringComparer.Ordinal)
		.ToArray()!;

	// --- 1. Happy path ---

	/// <summary>
	/// Verifies that <see cref="FileRequestTraceSink.WriteAsync"/> persists a completed trace as a single
	/// JSON file carrying the top-level flow metadata and its ordered entries, the artifact an operator
	/// opens to inspect a request.
	/// </summary>
	[Fact]
	public async Task WriteAsync_WhenCalled_PersistsTraceAsJsonFile()
	{
		// Arrange: a trace with one recorded entry.
		FileRequestTraceSink sink = CreateSink();
		RequestTrace trace = CreateTrace("corr-basic");
		trace.Add(new TraceEntry(TraceStage.Note, DateTimeOffset.UnixEpoch, "backend selected: ollama"));

		// Act
		await sink.WriteAsync(trace, CancellationToken.None);

		// Assert: exactly one file, holding the flow metadata and the single entry (Stage rendered as its name).
		string path = Assert.Single(Directory.GetFiles(mDirectory, "*.json"));
		using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
		JsonElement root = document.RootElement;
		Assert.Equal("corr-basic", root.GetProperty("CorrelationId").GetString());
		Assert.Equal("POST", root.GetProperty("Method").GetString());
		Assert.Equal("/api/chat", root.GetProperty("Path").GetString());

		JsonElement entries = root.GetProperty("Entries");
		Assert.Equal(1, entries.GetArrayLength());
		Assert.Equal("Note", entries[0].GetProperty("Stage").GetString());
		Assert.Equal("backend selected: ollama", entries[0].GetProperty("Summary").GetString());
	}

	// --- 2. File naming ---

	/// <summary>
	/// Verifies that <see cref="FileRequestTraceSink.WriteAsync"/> names files by the whole correlation id,
	/// so two flows sharing an eight-character prefix and the same start millisecond — the keep-alive
	/// connection case, where Kestrel's id differs only in its trailing request counter — are written to
	/// distinct files instead of the second overwriting the first.
	/// </summary>
	[Fact]
	public async Task WriteAsync_WhenCorrelationIdsShareEightCharPrefix_WritesDistinctFiles()
	{
		// Arrange: identical 8-char prefix ("0HND1234") and identical epoch millisecond; only the suffix differs.
		FileRequestTraceSink sink = CreateSink();
		RequestTrace first = CreateTrace("0HND1234-REQ1");
		RequestTrace second = CreateTrace("0HND1234-REQ2");

		// Act
		await sink.WriteAsync(first, CancellationToken.None);
		await sink.WriteAsync(second, CancellationToken.None);

		// Assert: both traces survive under their own full-id name; a short-prefix name would have collided.
		string[] names = TraceFileNames();
		Assert.Equal(2, names.Length);
		Assert.Equal($"{EpochPrefix}_0HND1234-REQ1.json", names[0]);
		Assert.Equal($"{EpochPrefix}_0HND1234-REQ2.json", names[1]);
	}

	/// <summary>
	/// Verifies that <see cref="FileRequestTraceSink.WriteAsync"/> replaces a character invalid in a file
	/// name — the <c>:</c> Kestrel places between the connection id and the request number — with an
	/// underscore, so the trace is written under a portable name rather than lost to an IO error.
	/// </summary>
	[Fact]
	public async Task WriteAsync_WhenCorrelationIdContainsPathSeparator_SanitizesFileName()
	{
		// Arrange: a Kestrel-shaped id "{connectionId}:{requestNumber}" whose ':' is invalid on Windows.
		FileRequestTraceSink sink = CreateSink();
		RequestTrace trace = CreateTrace("0HND1234:00000001");

		// Act
		await sink.WriteAsync(trace, CancellationToken.None);

		// Assert: one file, written under the sanitized name with the ':' turned into '_'.
		string name = Assert.Single(TraceFileNames());
		Assert.Equal($"{EpochPrefix}_0HND1234_00000001.json", name);
	}

	// --- 3. Retention (ring buffer) ---

	/// <summary>
	/// Verifies that once the directory exceeds <see cref="RequestTracingOptions.MaxFiles"/>,
	/// <see cref="FileRequestTraceSink.WriteAsync"/> evicts the oldest traces so exactly the newest
	/// <c>MaxFiles</c> survive, keeping a long-running proxy from filling the disk.
	/// </summary>
	[Fact]
	public async Task WriteAsync_WhenDirectoryExceedsMaxFiles_EvictsOldestTraces()
	{
		// Arrange: a cap of 2, then five traces one second apart so their timestamp-prefixed names sort by age.
		FileRequestTraceSink sink = CreateSink(maxFiles: 2);
		for (int index = 0; index < 5; index++)
		{
			DateTimeOffset startedUtc = DateTimeOffset.UnixEpoch.AddSeconds(index);
			RequestTrace trace = CreateTrace($"corr-{index}", startedUtc);

			// Act: each write triggers a retention scan; the last three writes must evict the three oldest files.
			await sink.WriteAsync(trace, CancellationToken.None);
		}

		// Assert: only the two newest traces (indexes 3 and 4) remain; the three oldest were evicted.
		string[] names = TraceFileNames();
		Assert.Equal(2, names.Length);
		Assert.Equal($"{DateTimeOffset.UnixEpoch.AddSeconds(3).UtcDateTime:yyyyMMdd-HHmmssfff}_corr-3.json", names[0]);
		Assert.Equal($"{DateTimeOffset.UnixEpoch.AddSeconds(4).UtcDateTime:yyyyMMdd-HHmmssfff}_corr-4.json", names[1]);
	}

	/// <summary>
	/// Verifies that a directory holding exactly <see cref="RequestTracingOptions.MaxFiles"/> traces is left
	/// untouched by <see cref="FileRequestTraceSink.WriteAsync"/> — eviction begins only when the cap is
	/// exceeded, not when it is merely reached.
	/// </summary>
	[Fact]
	public async Task WriteAsync_WhenDirectoryAtMaxFiles_KeepsAllTraces()
	{
		// Arrange: a cap of 3 and exactly three traces one second apart.
		FileRequestTraceSink sink = CreateSink(maxFiles: 3);
		for (int index = 0; index < 3; index++)
		{
			RequestTrace trace = CreateTrace($"corr-{index}", DateTimeOffset.UnixEpoch.AddSeconds(index));

			// Act
			await sink.WriteAsync(trace, CancellationToken.None);
		}

		// Assert: all three survive — the count equals the cap, so nothing is over the limit to evict.
		Assert.Equal(3, TraceFileNames().Length);
	}

	// --- 4. Argument guards ---

	/// <summary>
	/// Verifies that the constructor rejects a <see langword="null"/> <c>options</c> argument, since the sink
	/// cannot resolve its directory or file cap without it.
	/// </summary>
	[Fact]
	public void Constructor_WhenOptionsIsNull_ThrowsArgumentNullException()
	{
		// Arrange + Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() => new FileRequestTraceSink(
			null!,
			new DataDirectory(AppContext.BaseDirectory),
			NullLogger<FileRequestTraceSink>.Instance));
		Assert.Equal("options", exception.ParamName);
	}

	/// <summary>
	/// Verifies that the constructor rejects a <see langword="null"/> <c>dataDirectory</c> argument, since it
	/// resolves a relative trace directory to its absolute path.
	/// </summary>
	[Fact]
	public void Constructor_WhenDataDirectoryIsNull_ThrowsArgumentNullException()
	{
		// Arrange
		ProxyOptions proxy = new() { RequestTracing = { Directory = mDirectory } };

		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() => new FileRequestTraceSink(
			Options.Create(proxy),
			null!,
			NullLogger<FileRequestTraceSink>.Instance));
		Assert.Equal("dataDirectory", exception.ParamName);
	}

	/// <summary>
	/// Verifies that the constructor rejects a <see langword="null"/> <c>logger</c> argument.
	/// </summary>
	[Fact]
	public void Constructor_WhenLoggerIsNull_ThrowsArgumentNullException()
	{
		// Arrange
		ProxyOptions proxy = new() { RequestTracing = { Directory = mDirectory } };

		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() => new FileRequestTraceSink(
			Options.Create(proxy),
			new DataDirectory(AppContext.BaseDirectory),
			null!));
		Assert.Equal("logger", exception.ParamName);
	}

	/// <summary>
	/// Verifies that <see cref="FileRequestTraceSink.WriteAsync"/> rejects a <see langword="null"/> trace, so a
	/// programming error surfaces immediately rather than producing an empty or malformed file.
	/// </summary>
	[Fact]
	public async Task WriteAsync_WhenTraceIsNull_ThrowsArgumentNullException()
	{
		// Arrange
		FileRequestTraceSink sink = CreateSink();

		// Act + Assert
		var exception =
			await Assert.ThrowsAsync<ArgumentNullException>(() => sink.WriteAsync(null!, CancellationToken.None));
		Assert.Equal("trace", exception.ParamName);
	}
}
