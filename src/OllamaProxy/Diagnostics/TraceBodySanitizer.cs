// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OllamaProxy.Diagnostics;

/// <summary>
/// Replaces inline attachment payloads in a captured JSON body with compact metadata placeholders so a
/// trace stays small and readable without persisting the blob itself. Two shapes are recognized: bare
/// base64 strings inside an <c>images</c> array (the inbound Ollama multimodal form) and
/// <c>data:&lt;mime&gt;;base64,…</c> URLs anywhere in the document (the translated backend form). Each
/// match becomes a marker such as <c>[image omitted: ~245 KB]</c> or
/// <c>[data URL omitted: image/png, ~245 KB]</c> that records the attachment's type and approximate
/// size while dropping its bytes. A genuine <c>http</c>/<c>https</c> image URL is small and informative,
/// so it is left untouched. The sanitizer never decodes a payload; it estimates the size from the
/// base64 length, so the memory the blob would have cost is never paid.
/// </summary>
/// <remarks>
/// The sanitizer is deliberately decoupled from the request contract types: it keys on the JSON shape
/// (<c>images</c> arrays and <c>data:</c> URLs), not on a specific endpoint's model, so it sanitizes any
/// traced body, current or future, without the diagnostics layer depending on the chat contracts. A
/// body that is not JSON, or that contains no attachment markers, is returned byte-for-byte unchanged.
/// </remarks>
static class TraceBodySanitizer
{
	/// <summary>The token that separates a data URL's media type from its base64 payload.</summary>
	private const string Base64Marker = ";base64,";

	/// <summary>The length of the literal <c>data:</c> scheme prefix, used to slice out the media type.</summary>
	private const int DataSchemeLength = 5;

	/// <summary>
	/// Returns a copy of <paramref name="body"/> with every inline attachment payload replaced by a
	/// compact metadata placeholder, leaving all other content intact. The result is compact (single-line)
	/// JSON; copy it out and pretty-print it to inspect a specific value. A body that carries no
	/// attachment markers, or that is not valid JSON, is returned unchanged.
	/// </summary>
	/// <param name="body">The captured body text to sanitize.</param>
	/// <returns>
	/// The sanitized, compact JSON when attachments were found and the body parsed; otherwise the original
	/// <paramref name="body"/> unchanged.
	/// </returns>
	/// <exception cref="ArgumentNullException"><paramref name="body"/> is <see langword="null"/>.</exception>
	public static string Redact(string body)
	{
		ArgumentNullException.ThrowIfNull(body);

		// Cheap pre-filter: without an images array or a base64 data URL there is nothing to redact, so
		// skip the parse entirely and return the body verbatim. This is the overwhelmingly common case
		// (most bodies carry no attachment), and it also preserves the original formatting byte-for-byte.
		// The "images" probe is case-insensitive because the proxy binds the request case-insensitively
		// (JsonSerializerDefaults.Web), so an "Images" key is just as much an attachment array as "images".
		if (!body.Contains("\"images\"", StringComparison.OrdinalIgnoreCase) &&
		    !body.Contains(Base64Marker, StringComparison.Ordinal))
			return body;

		JsonNode? root;
		try
		{
			root = JsonNode.Parse(body);
		}
		catch (JsonException)
		{
			// Not JSON (for example an SSE stream or plain text captured on a non-JSON endpoint). There is
			// no structure to redact selectively, so the body is left exactly as captured.
			return body;
		}

		if (root is null) return body;

		// Only re-serialize when a payload was actually replaced. A body that tripped the cheap pre-filter
		// but carried no real attachment (for example an images[] array of plain http URLs) is returned
		// verbatim, so its original formatting is preserved exactly; re-emitting it as compact JSON would
		// be a needless, visible change for a no-op redaction.
		return RedactNode(root) ? root.ToJsonString() : body;
	}

	/// <summary>
	/// Recursively walks a JSON node, replacing base64 data URLs wherever they appear and handing any
	/// <c>images</c> array to <see cref="RedactImagesArray"/> for bare-base64 handling.
	/// </summary>
	/// <param name="node">The node to walk, or <see langword="null"/>.</param>
	/// <returns><see langword="true"/> when at least one value was replaced; otherwise <see langword="false"/>.</returns>
	private static bool RedactNode(JsonNode? node)
	{
		bool changed = false;

		switch (node)
		{
			case JsonObject obj:
				// Snapshot the keys first: the loop reassigns values through the indexer, and enumerating a
				// snapshot keeps that mutation clear of the live collection.
				foreach (string key in obj.Select(pair => pair.Key).ToArray())
				{
					JsonNode? value = obj[key];

					if (string.Equals(key, "images", StringComparison.OrdinalIgnoreCase) &&
					    value is JsonArray images)
					{
						changed |= RedactImagesArray(images);
						continue;
					}

					if (value is JsonValue scalar && TryGetString(scalar, out string? text) &&
					    IsBase64DataUrl(text))
					{
						obj[key] = DataUrlPlaceholder(text);
						changed = true;
						continue;
					}

					changed |= RedactNode(value);
				}

				break;

			case JsonArray array:
				for (int index = 0; index < array.Count; index++)
				{
					JsonNode? element = array[index];

					if (element is JsonValue scalar && TryGetString(scalar, out string? text) &&
					    IsBase64DataUrl(text))
					{
						array[index] = DataUrlPlaceholder(text);
						changed = true;
						continue;
					}

					changed |= RedactNode(element);
				}

				break;
		}

		return changed;
	}

	/// <summary>
	/// Redacts the string elements of an <c>images</c> array: a base64 data URL becomes a data-URL marker,
	/// a genuine <c>http</c>/<c>https</c> reference is kept (small and informative), and anything else is
	/// treated as inline base64 and replaced with an image marker. Non-string elements are walked for
	/// nested data URLs as a defensive measure against malformed input.
	/// </summary>
	/// <param name="images">The <c>images</c> array to redact in place.</param>
	/// <returns><see langword="true"/> when at least one element was replaced; otherwise <see langword="false"/>.</returns>
	private static bool RedactImagesArray(JsonArray images)
	{
		bool changed = false;

		for (int index = 0; index < images.Count; index++)
		{
			if (images[index] is not JsonValue scalar || !TryGetString(scalar, out string? text))
			{
				changed |= RedactNode(images[index]);
				continue;
			}

			if (IsBase64DataUrl(text))
			{
				images[index] = DataUrlPlaceholder(text);
				changed = true;
				continue;
			}

			// A genuine http(s) URL is kept verbatim; anything else is treated as inline base64.
			if (!IsAbsoluteHttpUrl(text))
			{
				images[index] = ImagePlaceholder(text);
				changed = true;
			}
		}

		return changed;
	}

	/// <summary>
	/// Reads the underlying string of a <see cref="JsonValue"/>, returning <see langword="false"/> for a
	/// value that wraps a non-string token (number, boolean, <see langword="null"/>).
	/// </summary>
	/// <param name="value">The JSON value to read.</param>
	/// <param name="text">The extracted string when the value is a string; otherwise <see langword="null"/>.</param>
	/// <returns><see langword="true"/> when the value wraps a string; otherwise <see langword="false"/>.</returns>
	private static bool TryGetString(JsonValue value, [NotNullWhen(true)] out string? text) =>
		value.TryGetValue(out text);

	/// <summary>
	/// Determines whether a string is a base64-encoded data URL (<c>data:&lt;mime&gt;;base64,…</c>), the
	/// shape the backend request mapper produces for an inline image.
	/// </summary>
	/// <param name="value">The string to test.</param>
	/// <returns><see langword="true"/> when the value is a base64 data URL; otherwise <see langword="false"/>.</returns>
	private static bool IsBase64DataUrl(string value) =>
		value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
		value.Contains(Base64Marker, StringComparison.Ordinal);

	/// <summary>
	/// Determines whether a string is an absolute <c>http</c> or <c>https</c> URL, a reference worth
	/// keeping in the trace rather than redacting. Mirrors the upstream request mapper's own test so the
	/// trace classifies an image reference exactly as the proxy does.
	/// </summary>
	/// <param name="value">The string to test.</param>
	/// <returns><see langword="true"/> when the value is an absolute http(s) URL; otherwise <see langword="false"/>.</returns>
	private static bool IsAbsoluteHttpUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
	                                                       (uri.Scheme == Uri.UriSchemeHttp ||
	                                                        uri.Scheme == Uri.UriSchemeHttps);

	/// <summary>
	/// Builds the placeholder for a base64 data URL, carrying its media type and approximate decoded size.
	/// </summary>
	/// <param name="dataUrl">The data URL being replaced.</param>
	/// <returns>The metadata marker, for example <c>[data URL omitted: image/png, ~245 KB]</c>.</returns>
	private static string DataUrlPlaceholder(string dataUrl) =>
		$"[data URL omitted: {ExtractMediaType(dataUrl)}, ~{DescribeApproximateSize(Base64PayloadLength(dataUrl))}]";

	/// <summary>
	/// Builds the placeholder for a bare base64 image string, carrying its approximate decoded size.
	/// </summary>
	/// <param name="base64">The base64 payload being replaced.</param>
	/// <returns>The metadata marker, for example <c>[image omitted: ~245 KB]</c>.</returns>
	private static string ImagePlaceholder(string base64) =>
		$"[image omitted: ~{DescribeApproximateSize(base64.Length)}]";

	/// <summary>
	/// Extracts the media type from a data URL: the segment between the <c>data:</c> scheme and the
	/// <c>;base64,</c> marker, falling back to <c>unknown</c> when it cannot be located.
	/// </summary>
	/// <param name="dataUrl">The data URL to read.</param>
	/// <returns>The media type, or <c>unknown</c> when none is present.</returns>
	private static string ExtractMediaType(string dataUrl)
	{
		int separator = dataUrl.IndexOf(';', StringComparison.Ordinal);
		return separator > DataSchemeLength ? dataUrl[DataSchemeLength..separator] : "unknown";
	}

	/// <summary>
	/// Computes the length of the base64 payload that follows the <c>;base64,</c> marker in a data URL.
	/// </summary>
	/// <param name="dataUrl">The data URL to measure.</param>
	/// <returns>The number of base64 characters in the payload.</returns>
	private static int Base64PayloadLength(string dataUrl)
	{
		int marker = dataUrl.IndexOf(Base64Marker, StringComparison.Ordinal);
		int start = marker + Base64Marker.Length;
		return dataUrl.Length - start;
	}

	/// <summary>
	/// Estimates the decoded size of a base64 payload and formats it as a human-readable string. Base64
	/// encodes three bytes per four characters; padding is ignored, which is why the caller prefixes the
	/// result with a <c>~</c>. Sizes at or above 1024 bytes are divided by 1024 and labeled <c>KB</c>
	/// (the binary-divisor, decimal-label convention common to file managers) so the marker reads the way
	/// an operator expects rather than as strict SI <c>KiB</c>.
	/// </summary>
	/// <param name="base64Length">The number of base64 characters in the payload.</param>
	/// <returns>The approximate size, in bytes below 1024 and in 1024-byte units (labeled KB) at or above it.</returns>
	private static string DescribeApproximateSize(int base64Length)
	{
		long bytes = (long)base64Length / 4 * 3;
		return bytes < 1024 ? $"{bytes} B" : $"{bytes / 1024} KB";
	}
}
