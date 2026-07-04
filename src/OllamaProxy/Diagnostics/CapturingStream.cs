// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text;

namespace OllamaProxy.Diagnostics;

/// <summary>
/// A pass-through <see cref="Stream"/> that forwards every write to an inner stream while teeing a
/// (optionally bounded) copy of the bytes into an in-memory buffer. The tracing middleware wraps the
/// HTTP response body in one of these so it can capture what was sent to the client without delaying or
/// buffering the response: bytes still reach the client immediately, and only a copy (capped at the
/// configured byte budget, or unbounded when no budget is set) is retained for the trace. Reads and
/// seeks delegate to the inner stream; only the write path is observed, which is all an outbound
/// response needs.
/// </summary>
sealed class CapturingStream : Stream
{
	private readonly Stream       mInner;
	private readonly MemoryStream mBuffer = new();
	private readonly int?         mMaxBytes;
	private          bool         mTruncated;

	/// <summary>
	/// Initializes a new instance of the <see cref="CapturingStream"/> class.
	/// </summary>
	/// <param name="inner">The underlying response stream writes are forwarded to.</param>
	/// <param name="maxBytes">
	/// The maximum number of bytes to retain in the capture buffer, or <see langword="null"/> to capture
	/// the response in full.
	/// </param>
	/// <exception cref="ArgumentNullException"><paramref name="inner"/> is <see langword="null"/>.</exception>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="maxBytes"/> is not greater than zero.</exception>
	public CapturingStream(Stream inner, int? maxBytes)
	{
		ArgumentNullException.ThrowIfNull(inner);

		// Pass the public parameter name explicitly: CallerArgumentExpression would otherwise capture the
		// local "limit", leaking an internal name into ParamName instead of the documented "maxBytes".
		if (maxBytes is { } limit) ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit, nameof(maxBytes));

		mInner = inner;
		mMaxBytes = maxBytes;
	}

	/// <inheritdoc/>
	public override bool CanRead => mInner.CanRead;

	/// <inheritdoc/>
	public override bool CanSeek => mInner.CanSeek;

	/// <inheritdoc/>
	public override bool CanWrite => mInner.CanWrite;

	/// <inheritdoc/>
	public override long Length => mInner.Length;

	/// <inheritdoc/>
	public override long Position
	{
		get => mInner.Position;
		set => mInner.Position = value;
	}

	/// <summary>
	/// Gets a value indicating whether the captured copy was truncated at the byte budget. When
	/// <see langword="true"/>, the bytes returned by <see cref="GetCapturedText"/> are a prefix of what
	/// was actually written to the client.
	/// </summary>
	public bool Truncated => mTruncated;

	/// <inheritdoc/>
	public override void Flush() => mInner.Flush();

	/// <inheritdoc/>
	public override Task FlushAsync(CancellationToken cancellationToken) => mInner.FlushAsync(cancellationToken);

	/// <inheritdoc/>
	public override int Read(byte[] buffer, int offset, int count) => mInner.Read(buffer, offset, count);

	/// <inheritdoc/>
	public override long Seek(long offset, SeekOrigin origin) => mInner.Seek(offset, origin);

	/// <inheritdoc/>
	public override void SetLength(long value) => mInner.SetLength(value);

	/// <inheritdoc/>
	public override void Write(byte[] buffer, int offset, int count)
	{
		Capture(buffer.AsSpan(offset, count));
		mInner.Write(buffer, offset, count);
	}

	/// <inheritdoc/>
	public override ValueTask WriteAsync(
		ReadOnlyMemory<byte> buffer,
		CancellationToken    cancellationToken = default)
	{
		Capture(buffer.Span);
		return mInner.WriteAsync(buffer, cancellationToken);
	}

	/// <inheritdoc/>
	public override Task WriteAsync(
		byte[]            buffer,
		int               offset,
		int               count,
		CancellationToken cancellationToken) => WriteAsync(
			buffer.AsMemory(offset, count),
			cancellationToken)
		.AsTask();

	/// <summary>
	/// Decodes the captured bytes as UTF-8 text for inclusion in the trace. When the capture was truncated
	/// at the byte budget, a trailing partial UTF-8 sequence left dangling by the raw-byte cut is dropped
	/// first, so the decoded text never ends in a replacement character.
	/// </summary>
	/// <returns>The captured response text, truncated when <see cref="Truncated"/> is <see langword="true"/>.</returns>
	public string GetCapturedText()
	{
		int length = (int)mBuffer.Length;

		// A complete capture is decoded as-is; only a budget cut can split a multi-byte code point, so the
		// back-off is confined to the truncated case (a full response that legitimately ends mid-stream is
		// never trimmed here).
		if (mTruncated) length = TrimDanglingUtf8(mBuffer.GetBuffer(), length);

		return Encoding.UTF8.GetString(mBuffer.GetBuffer(), 0, length);
	}

	/// <inheritdoc/>
	protected override void Dispose(bool disposing)
	{
		if (disposing) mBuffer.Dispose();

		// The inner stream is owned by the HTTP server, not by this wrapper, so it is intentionally not
		// disposed here; only the local capture buffer is.
		base.Dispose(disposing);
	}

	/// <summary>
	/// Copies as many of the written bytes into the capture buffer as the remaining budget allows,
	/// flagging truncation once the budget is exhausted. When no budget is set the whole span is always
	/// captured. Cutting on a raw byte boundary is acceptable here because the captured text is a
	/// diagnostic artifact, not a contract surface.
	/// </summary>
	/// <param name="written">The span of bytes just written to the inner stream.</param>
	private void Capture(ReadOnlySpan<byte> written)
	{
		if (mMaxBytes is not { } limit)
		{
			mBuffer.Write(written);
			return;
		}

		int remaining = limit - (int)mBuffer.Length;
		if (remaining <= 0)
		{
			if (!written.IsEmpty) mTruncated = true;
			return;
		}

		if (written.Length > remaining)
		{
			mBuffer.Write(written[..remaining]);
			mTruncated = true;
			return;
		}

		mBuffer.Write(written);
	}

	/// <summary>
	/// Returns the length, at or below <paramref name="length"/>, that excludes a trailing incomplete UTF-8
	/// sequence, the partial code point a raw-byte budget cut can leave dangling. A complete final
	/// sequence (or a body that is wholly ASCII) is returned unchanged; only a genuinely truncated tail is
	/// dropped. An invalid lead byte is left in place rather than guessed at.
	/// </summary>
	/// <param name="buffer">The capture buffer's backing array.</param>
	/// <param name="length">The number of valid bytes in <paramref name="buffer"/>.</param>
	/// <returns>The byte count to decode, with any dangling partial sequence excluded.</returns>
	private static int TrimDanglingUtf8(byte[] buffer, int length)
	{
		if (length == 0) return 0;

		// Walk back over UTF-8 continuation bytes (0b10xxxxxx) to the lead byte of the final code point.
		int start = length - 1;
		while (start >= 0 && (buffer[start] & 0xC0) == 0x80) start--;

		// A continuation run with no lead byte means corrupt input, not a clean cut; keep it verbatim.
		if (start < 0) return length;

		byte lead = buffer[start];

		// The high bits of the lead byte announce the sequence length (1 for ASCII through 4 for the widest
		// code point); an invalid lead announces nothing and is left in place.
		int expected =
			(lead & 0x80) == 0x00 ? 1 :
			(lead & 0xE0) == 0xC0 ? 2 :
			(lead & 0xF0) == 0xE0 ? 3 :
			(lead & 0xF8) == 0xF0 ? 4 :
			                        0;

		if (expected == 0) return length;

		// All announced bytes are present: the final sequence is whole, keep it. Otherwise drop the tail.
		int available = length - start;
		return available >= expected ? length : start;
	}
}
