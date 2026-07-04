// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json.Nodes;

using OllamaProxy.Diagnostics;

namespace OllamaProxy.Tests.Diagnostics;

// Attachment redaction: from "nothing to do" through the two attachment shapes to the edges.
//
// These tests walk the sanitizer from its cheapest path to its most involved one, mirroring how a body
// actually flows through it:
//
//   1. Pass-through: a body with no attachment markers is returned byte-for-byte (WhenNoAttachments,
//      WhenNotJson, WhenEmpty). A body that trips the pre-filter but carries only real http(s) URLs is
//      also returned unchanged, never reformatted (WhenImagesContainsOnlyHttpUrls). The cheap pre-filter
//      must never parse or reformat these.
//
//   2. Inbound images[]: bare base64 becomes an image marker, a real http(s) URL is kept, and a data
//      URL inside images[] becomes a data-URL marker (WhenImagesContainsBase64, WhenImagesContainsHttpUrl,
//      WhenImagesContainsDataUrl).
//
//   3. Backend data: URLs anywhere become data-URL markers carrying the media type and size
//      (WhenBodyContainsDataUrl, WhenDataUrlNested).
//
//   4. Sizing and media-type formatting: bytes below 1024 read as "B", at or above as "KB" (1024-byte
//      units), and a missing media type degrades to "unknown" (WhenPayloadSmall, WhenMediaTypeMissing).
//
//   5. Edges: malformed JSON is left untouched, the prompt text beside an image survives, and a body
//      with many images redacts every one (WhenMalformedJson, WhenTextAccompaniesImage, WhenManyImages).
[Trait("Category", "Unit")]
public sealed class TraceBodySanitizerTests
{
	/// <summary>
	/// Builds a base64 string of a given character length (the content is irrelevant — only the length
	/// drives the size estimate, so a repeated padding-free character is sufficient).
	/// </summary>
	/// <param name="length">The number of base64 characters to produce.</param>
	/// <returns>A base64 string of the requested length.</returns>
	private static string Base64OfLength(int length) => new('A', length);

	// --- 1. Pass-through: nothing to redact ---

	/// <summary>
	/// Verifies that <see cref="TraceBodySanitizer.Redact"/> returns a body that carries no attachment
	/// markers completely unchanged, including its original whitespace, since the cheap pre-filter must
	/// skip the parse-and-reserialize path entirely.
	/// </summary>
	[Fact]
	public void Redact_WhenNoAttachments_ReturnsBodyUnchanged()
	{
		// Arrange: a normal chat body with indentation and no images[] or data: URL.
		const string body = """
		                    {
		                      "model": "llama3",
		                      "messages": [
		                    	{ "role": "user", "content": "Hello there" }
		                      ]
		                    }
		                    """;

		// Act
		string result = TraceBodySanitizer.Redact(body);

		// Assert: byte-for-byte identical — the formatting is preserved because no parse happened.
		Assert.Equal(body, result);
	}

	/// <summary>
	/// Verifies that <see cref="TraceBodySanitizer.Redact"/> returns a non-JSON body unchanged: there is
	/// no structure to redact selectively, so a captured SSE stream or plain text is left as-is.
	/// </summary>
	[Fact]
	public void Redact_WhenNotJson_ReturnsBodyUnchanged()
	{
		// Arrange: a data: marker forces the parse attempt, but the body is not JSON, so it must survive.
		const string body = "data:image/png;base64,AAAA is not valid JSON";

		// Act
		string result = TraceBodySanitizer.Redact(body);

		// Assert
		Assert.Equal(body, result);
	}

	/// <summary>
	/// Verifies that <see cref="TraceBodySanitizer.Redact"/> returns an empty body unchanged, since an
	/// empty string carries no markers and must short-circuit through the pre-filter.
	/// </summary>
	[Fact]
	public void Redact_WhenEmpty_ReturnsEmpty()
	{
		// Arrange
		string body = string.Empty;

		// Act
		string result = TraceBodySanitizer.Redact(body);

		// Assert
		Assert.Equal(string.Empty, result);
	}

	/// <summary>
	/// Verifies that <see cref="TraceBodySanitizer.Redact"/> returns a body whose <c>images</c> array holds
	/// only genuine <c>http</c>/<c>https</c> URLs byte-for-byte unchanged: the pre-filter trips on the
	/// <c>images</c> key and the body parses, but because nothing is actually redacted the original
	/// formatting must be preserved rather than re-emitted as compact JSON.
	/// </summary>
	[Fact]
	public void Redact_WhenImagesContainsOnlyHttpUrls_ReturnsBodyUnchanged()
	{
		// Arrange: an indented images[] array of real URLs — tripping the pre-filter but carrying no blob.
		const string body = """
		                    {
		                      "images": [
		                    	"https://example.com/cat.png",
		                    	"http://example.com/dog.jpg"
		                      ]
		                    }
		                    """;

		// Act
		string result = TraceBodySanitizer.Redact(body);

		// Assert: identical to the input, including indentation — a no-op redaction must not reformat.
		Assert.Equal(body, result);
	}

	// --- 2. Inbound images[] array ---

	/// <summary>
	/// Verifies that <see cref="TraceBodySanitizer.Redact"/> replaces a bare base64 string inside an
	/// <c>images</c> array with an image marker carrying the approximate decoded size, while leaving the
	/// surrounding message structure intact.
	/// </summary>
	[Fact]
	public void Redact_WhenImagesContainsBase64_ReplacesWithImageMarker()
	{
		// Arrange: 4096 base64 chars decode to ~3072 bytes => "~3 KB".
		string base64 = Base64OfLength(4096);
		string body = $$"""{"images":["{{base64}}"]}""";

		// Act
		string result = TraceBodySanitizer.Redact(body);

		// Assert: the array still holds exactly one element, now the marker.
		JsonNode root = JsonNode.Parse(result)!;
		JsonArray images = root["images"]!.AsArray();
		Assert.Equal("[image omitted: ~3 KB]", (string?)images.Single());
	}

	/// <summary>
	/// Verifies that <see cref="TraceBodySanitizer.Redact"/> keeps a genuine <c>http</c>/<c>https</c>
	/// image reference inside an <c>images</c> array, since a real URL is small and informative and
	/// mirrors what the upstream request mapper forwards unchanged.
	/// </summary>
	[Fact]
	public void Redact_WhenImagesContainsHttpUrl_KeepsUrl()
	{
		// Arrange: an absolute https URL is a reference worth keeping, not a blob.
		const string url = "https://example.com/cat.png";
		string body = $$"""{"images":["{{url}}"]}""";

		// Act
		string result = TraceBodySanitizer.Redact(body);

		// Assert
		JsonNode root = JsonNode.Parse(result)!;
		JsonArray images = root["images"]!.AsArray();
		Assert.Equal(url, (string?)images.Single());
	}

	/// <summary>
	/// Verifies that <see cref="TraceBodySanitizer.Redact"/> replaces a base64 data URL that appears
	/// inside an <c>images</c> array with a data-URL marker carrying the media type and size, rather than
	/// treating it as a bare base64 image.
	/// </summary>
	[Fact]
	public void Redact_WhenImagesContainsDataUrl_ReplacesWithDataUrlMarker()
	{
		// Arrange: 800 base64 chars => ~600 bytes => "~600 B" (below 1024 bytes).
		string dataUrl = $"data:image/jpeg;base64,{Base64OfLength(800)}";
		string body = $$"""{"images":["{{dataUrl}}"]}""";

		// Act
		string result = TraceBodySanitizer.Redact(body);

		// Assert
		JsonNode root = JsonNode.Parse(result)!;
		JsonArray images = root["images"]!.AsArray();
		Assert.Equal("[data URL omitted: image/jpeg, ~600 B]", (string?)images.Single());
	}

	// --- 3. Backend data: URLs anywhere ---

	/// <summary>
	/// Verifies that <see cref="TraceBodySanitizer.Redact"/> replaces a base64 data URL carried in a
	/// nested <c>image_url</c> object — the shape the backend request mapper produces — with a data-URL
	/// marker, while leaving the sibling <c>type</c> field untouched.
	/// </summary>
	[Fact]
	public void Redact_WhenBodyContainsDataUrl_ReplacesWithDataUrlMarker()
	{
		// Arrange: 4096 base64 chars => ~3072 bytes => "~3 KB".
		string dataUrl = $"data:image/png;base64,{Base64OfLength(4096)}";
		// Triple-brace interpolation: the JSON's literal "}}" must not be read as an interpolation delimiter.
		string body =
			$$$"""{"messages":[{"content":[{"type":"image_url","image_url":{"url":"{{{dataUrl}}}"}}]}]}""";

		// Act
		string result = TraceBodySanitizer.Redact(body);

		// Assert: the url became a marker; the type tag survived.
		JsonNode root = JsonNode.Parse(result)!;
		JsonNode part = root["messages"]!.AsArray()[0]!["content"]!.AsArray()[0]!;
		Assert.Equal("image_url", (string?)part["type"]);
		Assert.Equal("[data URL omitted: image/png, ~3 KB]", (string?)part["image_url"]!["url"]);
	}

	/// <summary>
	/// Verifies that <see cref="TraceBodySanitizer.Redact"/> finds and replaces a base64 data URL nested
	/// several levels deep in arbitrary objects and arrays, confirming the walk is fully recursive.
	/// </summary>
	[Fact]
	public void Redact_WhenDataUrlNested_ReplacesDeepValue()
	{
		// Arrange: a data URL buried under nested objects/arrays unrelated to the images[] shape.
		string dataUrl = $"data:image/webp;base64,{Base64OfLength(1400)}";
		// Triple-brace interpolation: the JSON's trailing "}}" must not be read as an interpolation delimiter.
		string body = $$$"""{"outer":{"inner":[{"blob":"{{{dataUrl}}}"}]}}""";

		// Act
		string result = TraceBodySanitizer.Redact(body);

		// Assert: 1400 base64 chars => 1050 bytes => "~1 KB".
		JsonNode root = JsonNode.Parse(result)!;
		JsonNode blob = root["outer"]!["inner"]!.AsArray()[0]!["blob"]!;
		Assert.Equal("[data URL omitted: image/webp, ~1 KB]", (string?)blob);
	}

	// --- 4. Size and media-type formatting ---

	/// <summary>
	/// Verifies that <see cref="TraceBodySanitizer.Redact"/> reports a sub-kilobyte payload in bytes,
	/// confirming the size formatter switches units at the 1024-byte boundary.
	/// </summary>
	[Fact]
	public void Redact_WhenPayloadSmall_ReportsBytes()
	{
		// Arrange: 100 base64 chars => 75 bytes => "~75 B".
		string body = $$"""{"images":["{{Base64OfLength(100)}}"]}""";

		// Act
		string result = TraceBodySanitizer.Redact(body);

		// Assert
		JsonNode root = JsonNode.Parse(result)!;
		Assert.Equal("[image omitted: ~75 B]", (string?)root["images"]!.AsArray().Single());
	}

	/// <summary>
	/// Verifies that <see cref="TraceBodySanitizer.Redact"/> degrades a data URL with no media-type
	/// segment to <c>unknown</c> rather than emitting a malformed marker, so the placeholder is always
	/// well-formed.
	/// </summary>
	[Fact]
	public void Redact_WhenMediaTypeMissing_ReportsUnknown()
	{
		// Arrange: "data:;base64,..." has the base64 marker but an empty media-type segment.
		string dataUrl = $"data:;base64,{Base64OfLength(40)}";
		string body = $$"""{"images":["{{dataUrl}}"]}""";

		// Act
		string result = TraceBodySanitizer.Redact(body);

		// Assert: 40 base64 chars => 30 bytes => "~30 B"; media type reads "unknown".
		JsonNode root = JsonNode.Parse(result)!;
		Assert.Equal("[data URL omitted: unknown, ~30 B]", (string?)root["images"]!.AsArray().Single());
	}

	// --- 5. Edges ---

	/// <summary>
	/// Verifies that <see cref="TraceBodySanitizer.Redact"/> returns a malformed JSON body unchanged
	/// instead of throwing, since the sanitizer is a best-effort diagnostic transform that must never
	/// break trace persistence.
	/// </summary>
	[Fact]
	public void Redact_WhenMalformedJson_ReturnsBodyUnchanged()
	{
		// Arrange: the data: marker forces a parse attempt, but the JSON is truncated mid-object.
		const string body = """{"images":["data:image/png;base64,AAAA""";

		// Act
		string result = TraceBodySanitizer.Redact(body);

		// Assert
		Assert.Equal(body, result);
	}

	/// <summary>
	/// Verifies that <see cref="TraceBodySanitizer.Redact"/> preserves the prompt text that accompanies
	/// an image, replacing only the attachment, so the readable part of a multimodal request survives.
	/// </summary>
	[Fact]
	public void Redact_WhenTextAccompaniesImage_KeepsText()
	{
		// Arrange: a typical multimodal message — prompt plus one base64 image.
		string base64 = Base64OfLength(4096);
		string body =
			$$"""{"messages":[{"role":"user","content":"What is this?","images":["{{base64}}"]}]}""";

		// Act
		string result = TraceBodySanitizer.Redact(body);

		// Assert: the content text is intact and the image is a marker.
		JsonNode root = JsonNode.Parse(result)!;
		JsonNode message = root["messages"]!.AsArray()[0]!;
		Assert.Equal("What is this?", (string?)message["content"]);
		Assert.Equal("[image omitted: ~3 KB]", (string?)message["images"]!.AsArray().Single());
	}

	/// <summary>
	/// Verifies that <see cref="TraceBodySanitizer.Redact"/> replaces every image when an
	/// <c>images</c> array holds several, confirming the array walk visits all elements rather than
	/// stopping at the first.
	/// </summary>
	[Fact]
	public void Redact_WhenManyImages_ReplacesEveryImage()
	{
		// Arrange: three bare base64 images of different sizes in one array.
		string body =
			$$"""{"images":["{{Base64OfLength(4096)}}","{{Base64OfLength(8192)}}","{{Base64OfLength(100)}}"]}""";

		// Act
		string result = TraceBodySanitizer.Redact(body);

		// Assert: all three became markers with their own sizes (4096=>~3 KB, 8192=>~6 KB, 100=>~75 B).
		JsonNode root = JsonNode.Parse(result)!;
		JsonArray images = root["images"]!.AsArray();
		Assert.Equal(3, images.Count);
		Assert.Equal("[image omitted: ~3 KB]", (string?)images[0]);
		Assert.Equal("[image omitted: ~6 KB]", (string?)images[1]);
		Assert.Equal("[image omitted: ~75 B]", (string?)images[2]);
	}

	/// <summary>
	/// Verifies that <see cref="TraceBodySanitizer.Redact"/> throws <see cref="ArgumentNullException"/>
	/// when handed a <see langword="null"/> body, since the body is a required input.
	/// </summary>
	[Fact]
	public void Redact_WhenBodyNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var ex = Assert.Throws<ArgumentNullException>(() => TraceBodySanitizer.Redact(null!));
		Assert.Equal("body", ex.ParamName);
	}
}
