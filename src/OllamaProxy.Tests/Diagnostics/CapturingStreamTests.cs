// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text;

using OllamaProxy.Diagnostics;

namespace OllamaProxy.Tests.Diagnostics;

/// <summary>
/// Tests for <see cref="CapturingStream"/> response-body teeing: the capture buffer next to the live forward
/// path.
/// </summary>
/// <remarks>
/// <see cref="CapturingStream"/> forwards every write to the inner stream and tees a copy into a buffer that is
/// either bounded (a positive cap) or unbounded (null). The sections below cover both the forwarding contract and
/// the capture/truncation behavior:
/// <list type="number">
///     <item>
///         <description>
///         Forwarding: every byte written reaches the inner stream regardless of the cap (WritesReachInner).
///         </description>
///     </item>
///     <item>
///         <description>
///         Unbounded: a null cap captures the whole response and never flags truncation
///         (WhenNoLimit_CapturesEverything).
///         </description>
///     </item>
///     <item>
///         <description>
///         Bounded: a write within budget is captured whole (WhenWithinLimit); a write past budget is cut and
///         flagged (WhenExceedsLimit); a cut that splits a multi-byte code point drops the dangling bytes on
///         decode (WhenCutSplitsCodePoint); a later write after the budget is exhausted flags truncation without
///         growing the buffer (WhenAlreadyFull).
///         </description>
///     </item>
///     <item>
///         <description>Construction: a non-positive cap is rejected; a null inner stream is rejected.</description>
///     </item>
///     <item>
///         <description>
///         Delegation: the read/seek/query surface (CanRead/CanSeek/CanWrite, Length, Position, Flush, FlushAsync,
///         Read, Seek, SetLength) forwards to the inner stream, since only the write path is observed
///         (*_DelegatesToInner).
///         </description>
///     </item>
///     <item>
///         <description>
///         Synchronous write: the byte[]-offset-count overload forwards and captures the addressed slice
///         (Write_WhenCalled_ForwardsAndCapturesSlice).
///         </description>
///     </item>
///     <item>
///         <description>
///         Disposal: disposing the wrapper releases only its capture buffer and leaves the inner stream — owned
///         by the HTTP server — usable (Dispose_WhenCalled_LeavesInnerUsable).
///         </description>
///     </item>
///     <item>
///         <description>
///         UTF-8 trim edges (truncated captures only): a complete final multi-byte sequence at the cut is kept
///         whole (WhenCutKeepsCompleteSequence); an invalid lead byte at the cut is kept verbatim
///         (WhenCutEndsInInvalidLead); a bare continuation run with no lead byte is kept verbatim
///         (WhenCutIsAllContinuationBytes).
///         </description>
///     </item>
/// </list>
/// </remarks>
[Trait("Category", "Unit")]
public sealed class CapturingStreamTests
{
	/// <summary>
	/// Wraps a fresh <see cref="MemoryStream"/> as the inner (forward) target and returns both it and the
	/// capturing stream over it, so a test can assert on what was forwarded and what was captured.
	/// </summary>
	/// <param name="maxBytes">The capture cap, or <see langword="null"/> for unbounded capture.</param>
	/// <returns>The inner stream and the capturing stream wrapping it.</returns>
	private static (MemoryStream Inner, CapturingStream Capturing) CreateStream(int? maxBytes)
	{
		MemoryStream inner = new();
		return (inner, new CapturingStream(inner, maxBytes));
	}

	// --- 1. Forwarding ---

	/// <summary>
	/// Verifies that <see cref="CapturingStream"/> forwards every written byte to the inner stream
	/// unchanged, since capturing must never alter or withhold what the client receives.
	/// </summary>
	[Fact]
	public async Task WriteAsync_WhenCalled_WritesReachInnerVerbatim()
	{
		// Arrange
		(MemoryStream inner, CapturingStream capturing) = CreateStream(maxBytes: 4);
		byte[] payload = "hello world"u8.ToArray();

		// Act: the payload is larger than the capture cap, but forwarding is independent of the cap.
		await capturing.WriteAsync(payload);

		// Assert: the inner stream received the full payload even though capture was bounded.
		Assert.Equal(payload, inner.ToArray());
	}

	// --- 2. Unbounded capture ---

	/// <summary>
	/// Verifies that <see cref="CapturingStream"/> with a <see langword="null"/> cap captures the entire
	/// written response and never flags truncation, the opt-in "no limit" behavior.
	/// </summary>
	[Fact]
	public async Task GetCapturedText_WhenNoLimit_CapturesEverything()
	{
		// Arrange: a payload far larger than any default body cap, with no cap configured.
		(MemoryStream _, CapturingStream capturing) = CreateStream(maxBytes: null);
		string text = new('x', 50_000);
		await capturing.WriteAsync(Encoding.UTF8.GetBytes(text));

		// Act
		string captured = capturing.GetCapturedText();

		// Assert: the whole response is retained and truncation is never reported.
		Assert.Equal(text, captured);
		Assert.False(capturing.Truncated);
	}

	// --- 3. Bounded capture ---

	/// <summary>
	/// Verifies that <see cref="CapturingStream"/> captures a write that fits within the byte budget in
	/// full, without flagging truncation.
	/// </summary>
	[Fact]
	public async Task GetCapturedText_WhenWithinLimit_CapturesWhole()
	{
		// Arrange: 5 bytes written under an 8-byte cap.
		(MemoryStream _, CapturingStream capturing) = CreateStream(maxBytes: 8);
		await capturing.WriteAsync("hello"u8.ToArray());

		// Act
		string captured = capturing.GetCapturedText();

		// Assert
		Assert.Equal("hello", captured);
		Assert.False(capturing.Truncated);
	}

	/// <summary>
	/// Verifies that <see cref="CapturingStream"/> captures only the budgeted prefix of a write that
	/// exceeds the cap and flags the capture as truncated.
	/// </summary>
	[Fact]
	public async Task GetCapturedText_WhenExceedsLimit_CapturesPrefixAndFlagsTruncated()
	{
		// Arrange: 11 bytes written under a 4-byte cap.
		(MemoryStream _, CapturingStream capturing) = CreateStream(maxBytes: 4);
		await capturing.WriteAsync("hello world"u8.ToArray());

		// Act
		string captured = capturing.GetCapturedText();

		// Assert: only the first four bytes are retained, and truncation is reported.
		Assert.Equal("hell", captured);
		Assert.True(capturing.Truncated);
	}

	/// <summary>
	/// Verifies that <see cref="CapturingStream.GetCapturedText"/> drops a trailing partial UTF-8 sequence
	/// left dangling by the raw-byte cut, so a truncated multi-byte capture decodes to whole characters
	/// instead of ending in a replacement character.
	/// </summary>
	[Fact]
	public async Task GetCapturedText_WhenCutSplitsCodePoint_DropsDanglingBytes()
	{
		// Arrange: "€" is 3 UTF-8 bytes (E2 82 AC). A 4-byte cap keeps the first euro whole (3 bytes) and
		// one stray lead byte of the second; the dangling partial sequence must be dropped on decode.
		(MemoryStream _, CapturingStream capturing) = CreateStream(maxBytes: 4);
		await capturing.WriteAsync("€€"u8.ToArray());

		// Act
		string captured = capturing.GetCapturedText();

		// Assert: exactly one intact euro sign survives (the half-captured second one is dropped), and the
		// capture is still flagged truncated.
		Assert.Equal("€", captured);
		Assert.Equal(3, Encoding.UTF8.GetByteCount(captured));
		Assert.True(capturing.Truncated);
	}

	/// <summary>
	/// Verifies that <see cref="CapturingStream"/> flags truncation for a non-empty write that arrives
	/// after the budget is already exhausted, without growing the capture buffer beyond the cap.
	/// </summary>
	[Fact]
	public async Task GetCapturedText_WhenAlreadyFull_FlagsTruncatedWithoutGrowing()
	{
		// Arrange: fill the 4-byte budget exactly, then write more.
		(MemoryStream _, CapturingStream capturing) = CreateStream(maxBytes: 4);
		await capturing.WriteAsync("abcd"u8.ToArray());

		// Act: a second write once the budget is spent must be dropped from the capture but still flagged.
		await capturing.WriteAsync("efgh"u8.ToArray());
		string captured = capturing.GetCapturedText();

		// Assert
		Assert.Equal("abcd", captured);
		Assert.True(capturing.Truncated);
	}

	// --- 4. Construction ---

	/// <summary>
	/// Verifies that <see cref="CapturingStream"/> rejects a present, non-positive byte cap, since a zero
	/// or negative budget would capture nothing yet is not the explicit "unbounded" (null) choice.
	/// </summary>
	/// <param name="maxBytes">The invalid cap to construct with.</param>
	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	public void Constructor_WhenMaxBytesNotPositive_ThrowsArgumentOutOfRangeException(int maxBytes)
	{
		// Arrange
		using MemoryStream inner = new();

		// Act + Assert
		var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new CapturingStream(inner, maxBytes));
		Assert.Equal("maxBytes", ex.ParamName);
	}

	/// <summary>
	/// Verifies that <see cref="CapturingStream"/> rejects a <see langword="null"/> inner stream, since
	/// it must always have a target to forward writes to.
	/// </summary>
	[Fact]
	public void Constructor_WhenInnerNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var ex = Assert.Throws<ArgumentNullException>(() => new CapturingStream(null!, maxBytes: 4));
		Assert.Equal("inner", ex.ParamName);
	}

	// --- 5. Delegation to the inner stream ---

	/// <summary>
	/// Verifies that <see cref="CapturingStream.CanRead"/>, <see cref="CapturingStream.CanSeek"/>, and
	/// <see cref="CapturingStream.CanWrite"/> report the inner stream's capabilities, since capability
	/// queries are pure pass-through.
	/// </summary>
	[Fact]
	public void Capabilities_WhenQueried_DelegateToInner()
	{
		// Arrange
		(MemoryStream inner, CapturingStream capturing) = CreateStream(maxBytes: 4);

		// Act + Assert: a MemoryStream is readable, seekable, and writable, and the wrapper mirrors that.
		Assert.Equal(inner.CanRead, capturing.CanRead);
		Assert.Equal(inner.CanSeek, capturing.CanSeek);
		Assert.Equal(inner.CanWrite, capturing.CanWrite);
		Assert.True(capturing.CanRead);
		Assert.True(capturing.CanSeek);
		Assert.True(capturing.CanWrite);
	}

	/// <summary>
	/// Verifies that <see cref="CapturingStream.Length"/> reports the inner stream's length, since the
	/// wrapper keeps no length of its own.
	/// </summary>
	[Fact]
	public async Task Length_WhenQueried_DelegatesToInner()
	{
		// Arrange
		(MemoryStream inner, CapturingStream capturing) = CreateStream(maxBytes: 4);
		await capturing.WriteAsync("hello world"u8.ToArray());

		// Act + Assert: the forwarded payload defines the inner length regardless of the capture cap.
		Assert.Equal(11, capturing.Length);
		Assert.Equal(inner.Length, capturing.Length);
	}

	/// <summary>
	/// Verifies that <see cref="CapturingStream.Position"/> reads and writes the inner stream's position,
	/// since seeking is delegated wholesale.
	/// </summary>
	[Fact]
	public async Task Position_WhenSet_DelegatesToInner()
	{
		// Arrange
		(MemoryStream inner, CapturingStream capturing) = CreateStream(maxBytes: 4);
		await capturing.WriteAsync("hello"u8.ToArray());

		// Act: rewinding the wrapper must rewind the inner stream.
		capturing.Position = 1;

		// Assert
		Assert.Equal(1, capturing.Position);
		Assert.Equal(inner.Position, capturing.Position);
	}

	/// <summary>
	/// Verifies that <see cref="CapturingStream.Read"/> pulls bytes from the inner stream, since the read
	/// path is unobserved pass-through.
	/// </summary>
	[Fact]
	public async Task Read_WhenCalled_DelegatesToInner()
	{
		// Arrange
		(MemoryStream _, CapturingStream capturing) = CreateStream(maxBytes: 4);
		await capturing.WriteAsync("hello"u8.ToArray());
		capturing.Position = 0;
		byte[] destination = new byte[5];

		// Act
		int read = capturing.Read(destination, 0, destination.Length);

		// Assert: the bytes just written are read straight back out of the inner stream.
		Assert.Equal(5, read);
		Assert.Equal("hello"u8.ToArray(), destination);
	}

	/// <summary>
	/// Verifies that <see cref="CapturingStream.Seek"/> repositions the inner stream, since seeking is a
	/// direct delegation.
	/// </summary>
	[Fact]
	public async Task Seek_WhenCalled_DelegatesToInner()
	{
		// Arrange
		(MemoryStream inner, CapturingStream capturing) = CreateStream(maxBytes: 4);
		await capturing.WriteAsync("hello"u8.ToArray());

		// Act
		long position = capturing.Seek(2, SeekOrigin.Begin);

		// Assert
		Assert.Equal(2, position);
		Assert.Equal(inner.Position, capturing.Position);
	}

	/// <summary>
	/// Verifies that <see cref="CapturingStream.SetLength"/> resizes the inner stream, since length changes
	/// are delegated without affecting the capture buffer.
	/// </summary>
	[Fact]
	public async Task SetLength_WhenCalled_DelegatesToInner()
	{
		// Arrange
		(MemoryStream inner, CapturingStream capturing) = CreateStream(maxBytes: 4);
		await capturing.WriteAsync("hello"u8.ToArray());

		// Act: truncating the inner stream must not disturb what was already captured.
		capturing.SetLength(2);

		// Assert
		Assert.Equal(2, capturing.Length);
		Assert.Equal(2, inner.Length);
		Assert.Equal("hell", capturing.GetCapturedText());
	}

	/// <summary>
	/// Verifies that <see cref="CapturingStream.Flush"/> forwards to the inner stream and leaves both the
	/// forwarded and captured bytes intact, since flushing is pure delegation.
	/// </summary>
	[Fact]
	public async Task Flush_WhenCalled_DelegatesToInner()
	{
		// Arrange
		(MemoryStream inner, CapturingStream capturing) = CreateStream(maxBytes: 8);
		await capturing.WriteAsync("hello"u8.ToArray());

		// Act
		capturing.Flush();

		// Assert: flushing is a no-op for content on a MemoryStream, so nothing is lost either side.
		Assert.Equal("hello"u8.ToArray(), inner.ToArray());
		Assert.Equal("hello", capturing.GetCapturedText());
	}

	/// <summary>
	/// Verifies that <see cref="CapturingStream.FlushAsync"/> forwards to the inner stream and leaves both
	/// the forwarded and captured bytes intact, since the async flush is pure delegation.
	/// </summary>
	[Fact]
	public async Task FlushAsync_WhenCalled_DelegatesToInner()
	{
		// Arrange
		(MemoryStream inner, CapturingStream capturing) = CreateStream(maxBytes: 8);
		await capturing.WriteAsync("hello"u8.ToArray());

		// Act
		await capturing.FlushAsync();

		// Assert
		Assert.Equal("hello"u8.ToArray(), inner.ToArray());
		Assert.Equal("hello", capturing.GetCapturedText());
	}

	// --- 6. Synchronous write overload ---

	/// <summary>
	/// Verifies that the synchronous <see cref="CapturingStream.Write(byte[], int, int)"/> overload
	/// forwards the addressed slice to the inner stream and captures only that slice, mirroring the async
	/// path but exercising the offset/count arithmetic.
	/// </summary>
	[Fact]
	public void Write_WhenCalled_ForwardsAndCapturesSlice()
	{
		// Arrange: write only the middle "llo" slice of the payload via offset/count.
		(MemoryStream inner, CapturingStream capturing) = CreateStream(maxBytes: 8);
		byte[] payload = "hello"u8.ToArray();

		// Act
		capturing.Write(payload, 2, 3);

		// Assert: both the inner stream and the capture see the addressed slice, not the whole array.
		Assert.Equal("llo"u8.ToArray(), inner.ToArray());
		Assert.Equal("llo", capturing.GetCapturedText());
		Assert.False(capturing.Truncated);
	}

	// --- 7. Disposal ---

	/// <summary>
	/// Verifies that disposing a <see cref="CapturingStream"/> releases only its own capture buffer and
	/// leaves the inner stream usable, since the inner stream is owned by the HTTP server, not the wrapper.
	/// </summary>
	[Fact]
	public async Task Dispose_WhenCalled_LeavesInnerUsable()
	{
		// Arrange
		(MemoryStream inner, CapturingStream capturing) = CreateStream(maxBytes: 8);
		await capturing.WriteAsync("hello"u8.ToArray());

		// Act: dispose the wrapper only.
		capturing.Dispose();
		inner.Write("!"u8.ToArray());

		// Assert: the inner stream is not disposed, so it remains writable after the wrapper is gone.
		Assert.Equal("hello!"u8.ToArray(), inner.ToArray());
	}

	// --- 8. UTF-8 trim edges (truncated captures) ---

	/// <summary>
	/// Verifies that <see cref="CapturingStream.GetCapturedText"/> keeps a complete multi-byte sequence
	/// that happens to end exactly at the truncation boundary, since only a genuinely split tail should be
	/// dropped.
	/// </summary>
	[Fact]
	public async Task GetCapturedText_WhenCutKeepsCompleteSequence_RetainsWholeCharacter()
	{
		// Arrange: "€" is 3 UTF-8 bytes (E2 82 AC); a 3-byte cap captures exactly one whole euro sign, and
		// the following bytes are dropped, so the final sequence at the cut is complete.
		(MemoryStream _, CapturingStream capturing) = CreateStream(maxBytes: 3);
		await capturing.WriteAsync("€€"u8.ToArray());

		// Act
		string captured = capturing.GetCapturedText();

		// Assert: the complete euro sign survives untrimmed even though the capture is truncated.
		Assert.Equal("€", captured);
		Assert.True(capturing.Truncated);
	}

	/// <summary>
	/// Verifies that <see cref="CapturingStream.GetCapturedText"/> keeps an invalid UTF-8 lead byte at the
	/// truncation boundary verbatim, since a byte that announces no valid sequence length is left in place
	/// rather than guessed at.
	/// </summary>
	[Fact]
	public async Task GetCapturedText_WhenCutEndsInInvalidLead_KeepsBytesVerbatim()
	{
		// Arrange: 0xFF is not a valid UTF-8 lead byte. Under a 3-byte cap the capture is "ab" + 0xFF, so
		// the trim walk finds an invalid lead at the tail and must leave all three bytes in place.
		(MemoryStream _, CapturingStream capturing) = CreateStream(maxBytes: 3);
		await capturing.WriteAsync(new byte[] { (byte)'a', (byte)'b', 0xFF, 0xFF });

		// Act
		string captured = capturing.GetCapturedText();

		// Assert: the two ASCII bytes plus one replacement char for the retained 0xFF prove nothing was
		// trimmed, and truncation is still flagged.
		Assert.Equal("ab\uFFFD", captured);
		Assert.True(capturing.Truncated);
	}

	/// <summary>
	/// Verifies that <see cref="CapturingStream.GetCapturedText"/> keeps a run of continuation bytes with
	/// no preceding lead byte verbatim, since such corrupt input is not a clean cut and must not be
	/// silently dropped.
	/// </summary>
	[Fact]
	public async Task GetCapturedText_WhenCutIsAllContinuationBytes_KeepsBytesVerbatim()
	{
		// Arrange: 0x80/0x81 are continuation bytes (0b10xxxxxx) with no lead. Under a 2-byte cap the whole
		// capture is continuation bytes, so the trim walk runs off the start and must keep them verbatim.
		(MemoryStream _, CapturingStream capturing) = CreateStream(maxBytes: 2);
		await capturing.WriteAsync(new byte[] { 0x80, 0x81, 0x82 });

		// Act
		string captured = capturing.GetCapturedText();

		// Assert: both dangling continuation bytes are decoded (as replacement chars), proving neither was
		// trimmed, and truncation is flagged.
		Assert.Equal("\uFFFD\uFFFD", captured);
		Assert.True(capturing.Truncated);
	}
}
