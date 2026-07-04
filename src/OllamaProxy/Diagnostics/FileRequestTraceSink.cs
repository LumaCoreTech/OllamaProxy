// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Options;

using OllamaProxy.Configuration;
using OllamaProxy.Hosting;

namespace OllamaProxy.Diagnostics;

/// <summary>
/// Persists each completed <see cref="RequestTrace"/> as one indented-JSON file in the configured
/// directory, naming files <c>yyyyMMdd-HHmmssfff_{correlationId}.json</c> so they sort chronologically. The
/// directory behaves as a bounded ring buffer: once it holds <see cref="RequestTracingOptions.MaxFiles"/>
/// traces, the oldest files are deleted to make room for new ones, so a long-running proxy cannot fill
/// the disk. The retention scan is serialized with a lock so concurrent flows cannot both prune and
/// race each other into an inconsistent file count.
/// </summary>
sealed partial class FileRequestTraceSink : IRequestTraceSink
{
	private static readonly JsonSerializerOptions WriteOptions = new()
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

		// A trace file is read by a human, never re-served over the wire, so the strict HTML-safe escaping
		// (which renders '<', '>', '&', '+', and every non-ASCII rune as a \uXXXX sequence) only obscures
		// prompt text. Relaxed escaping keeps angle brackets, ampersands, and Unicode legible; the output
		// is still valid JSON. The redaction pass has already stripped attachment payloads, so there is no
		// blob here that the laxer encoder could bloat.
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};

	private readonly string                        mDirectory;
	private readonly int                           mMaxFiles;
	private readonly ILogger<FileRequestTraceSink> mLogger;
	private readonly Lock                          mRetentionGate = new();

	/// <summary>
	/// Initializes a new instance of the <see cref="FileRequestTraceSink"/> class, resolving the output
	/// directory against the writable data directory when it is configured as a relative path.
	/// </summary>
	/// <param name="options">The proxy options carrying the tracing directory and file cap.</param>
	/// <param name="dataDirectory">Resolves a relative trace directory to its writable absolute path.</param>
	/// <param name="logger">Records the resolved directory and any write/eviction failures.</param>
	/// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
	public FileRequestTraceSink(
		IOptions<ProxyOptions>        options,
		IDataDirectory                dataDirectory,
		ILogger<FileRequestTraceSink> logger)
	{
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(dataDirectory);
		ArgumentNullException.ThrowIfNull(logger);

		RequestTracingOptions tracing = options.Value.RequestTracing;

		mDirectory = dataDirectory.Resolve(tracing.Directory);
		mMaxFiles = tracing.MaxFiles;
		mLogger = logger;
	}

	/// <inheritdoc/>
	public async Task WriteAsync(RequestTrace trace, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(trace);

		try
		{
			Directory.CreateDirectory(mDirectory);

			string fileName = BuildFileName(trace);
			string path = Path.Combine(mDirectory, fileName);

			TraceDocument document = TraceDocument.From(trace);

			FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
			await using (stream.ConfigureAwait(false))
			{
				await JsonSerializer
					.SerializeAsync(stream, document, WriteOptions, cancellationToken)
					.ConfigureAwait(false);
			}

			EnforceRetention();
		}
		// The filter intentionally does not include OperationCanceledException: cancellation is the
		// caller's prerogative, not a write failure to be logged, and the caller's pipeline already
		// knows how to react to it. IO- and permission-level errors are logged and swallowed because
		// tracing is a best-effort diagnostic and must never break the request it traces.
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// ReSharper disable once InconsistentlySynchronizedField
			LogWriteFailed(mLogger, mDirectory, exception);
		}
	}

	/// <summary>
	/// Builds the chronologically sortable file name for a trace: a UTC timestamp prefix plus the full
	/// correlation id (sanitized of characters not allowed in a file name) to disambiguate flows that start
	/// within the same millisecond. The whole id is used rather than a short slice because the id's
	/// uniqueness lives in its later characters too: Kestrel's identifier is <c>{connectionId}:{request}</c>,
	/// so two requests on one keep-alive connection share a common prefix and differ only at the end; a
	/// truncated slice could collide and, with <see cref="FileMode.Create"/>, silently overwrite the older
	/// trace.
	/// </summary>
	/// <param name="trace">The trace to name.</param>
	/// <returns>The file name, including the <c>.json</c> extension.</returns>
	private static string BuildFileName(RequestTrace trace)
	{
		string timestamp = trace.StartedUtc.UtcDateTime.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
		string id = SanitizeForFileName(trace.CorrelationId);

		return $"{timestamp}_{id}.json";
	}

	/// <summary>
	/// Replaces every character that is invalid in a file name with an underscore, so a correlation id
	/// carrying a path separator or other reserved character (Kestrel's <c>:</c> between connection and
	/// request, for instance) still yields a valid, collision-stable file name.
	/// </summary>
	/// <param name="value">The correlation id to sanitize.</param>
	/// <returns>The id with every invalid file-name character replaced by an underscore.</returns>
	private static string SanitizeForFileName(string value)
	{
		char[] invalid = Path.GetInvalidFileNameChars();
		return value.IndexOfAny(invalid) < 0 ? value : string.Concat(value.Select(Replace));

		char Replace(char candidate) => Array.IndexOf(invalid, candidate) < 0 ? candidate : '_';
	}

	/// <summary>
	/// Deletes the oldest trace files until the directory holds no more than
	/// <see cref="RequestTracingOptions.MaxFiles"/>. Serialized so two concurrent writers cannot both
	/// enumerate, miscount, and over- or under-delete. Files that vanish mid-scan (deleted by a racing
	/// scan) are ignored.
	/// </summary>
	private void EnforceRetention()
	{
		lock (mRetentionGate)
		{
			string[] files = Directory.GetFiles(mDirectory, "*.json");
			if (files.Length <= mMaxFiles) return;

			// The timestamp-first name sorts oldest-first lexicographically, so the surplus at the front
			// of the ordered list is exactly the set to evict.
			Array.Sort(files, StringComparer.Ordinal);

			int surplus = files.Length - mMaxFiles;
			for (int index = 0; index < surplus; index++)
			{
				try
				{
					File.Delete(files[index]);
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
				{
					// A file held open or already gone is not fatal to retention; log and move on so a
					// single locked file cannot stall eviction of the rest.
					LogEvictionFailed(mLogger, files[index], exception);
				}
			}
		}
	}

	/// <summary>
	/// The pre-compiled error logged when a trace file cannot be written. Defined via
	/// <see cref="LoggerMessageAttribute"/> because the sink runs on every traced request (CA1848).
	/// </summary>
	[LoggerMessage(Level = LogLevel.Error, Message = "Failed to write request trace to directory {Directory}.")]
	private static partial void LogWriteFailed(ILogger logger, string directory, Exception exception);

	/// <summary>
	/// The pre-compiled warning logged when an old trace file cannot be evicted during retention.
	/// </summary>
	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to evict old request trace file {File}.")]
	private static partial void LogEvictionFailed(ILogger logger, string file, Exception exception);
}
