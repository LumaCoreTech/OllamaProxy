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
		var ex =
			Assert.Throws<ArgumentOutOfRangeException>(() => new CapturingStream(inner, maxBytes));
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
		var ex =
			Assert.Throws<ArgumentNullException>(() => new CapturingStream(null!, maxBytes: 4));
		Assert.Equal("inner", ex.ParamName);
	}
}
