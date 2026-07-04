// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.ComponentModel.DataAnnotations;

namespace OllamaProxy.Configuration;

/// <summary>
/// Configures the optional per-request trace: a diagnostic, file-per-flow record of everything the
/// proxy "speaks". This covers the inbound client request, the translated backend request (including why a
/// reasoning effort was chosen), the backend response, and the outbound client response. Tracing is a
/// debugging aid, disabled by default; when enabled it writes one indented-JSON file per request-
/// response flow into <see cref="Directory"/>, redacting credentials, optionally capping each captured
/// body at <see cref="MaxBodyBytes"/>, replacing inline attachments with metadata when
/// <see cref="RedactAttachments"/> is set, and never keeping more than <see cref="MaxFiles"/> files (the
/// oldest are evicted first so the directory cannot grow without bound).
/// </summary>
public sealed class RequestTracingOptions : IValidatableObject
{
	/// <summary>
	/// The configuration section name this options object binds to, relative to <see cref="ProxyOptions"/>.
	/// </summary>
	public const string SectionName = "RequestTracing";

	/// <summary>
	/// Gets or sets a value indicating whether request tracing is active. Defaults to
	/// <see langword="false"/> so the proxy carries zero tracing overhead unless tracing is explicitly
	/// requested for a debugging session.
	/// </summary>
	public bool Enabled { get; set; }

	/// <summary>
	/// Gets or sets the directory the trace files are written to. A relative path (the default
	/// <c>traces</c>) is resolved against the host's data directory: beside the executable in a foreground
	/// run, or <c>%ProgramData%\OllamaProxy\data</c> under the Windows Service. An absolute path is honored
	/// verbatim, which is how a container deployment pins traces onto a mounted volume (e.g.
	/// <c>/data/traces</c>). Created on first write if it does not exist. Must be non-blank when tracing is
	/// enabled.
	/// </summary>
	public string Directory { get; set; } = "traces";

	/// <summary>
	/// Gets or sets the maximum number of trace files retained in <see cref="Directory"/>. Once the cap
	/// is reached, the oldest files are deleted to make room for new ones, so the trace directory
	/// behaves as a bounded ring buffer. Must be greater than zero.
	/// </summary>
	public int MaxFiles { get; set; } = 10000;

	/// <summary>
	/// Gets or sets the optional cap, in bytes, on each captured body (inbound request, backend request,
	/// backend response, outbound response). A body larger than the cap is truncated in the trace and
	/// marked as truncated. <see langword="null"/> (the default) means no cap: bodies are captured in
	/// full, which is usually what a debugging session wants since tracing is enabled deliberately and
	/// briefly. Set a positive value to bound trace size in a disk- or memory-constrained environment.
	/// Must be greater than zero when set.
	/// </summary>
	public int? MaxBodyBytes { get; set; }

	/// <summary>
	/// Gets or sets a value indicating whether inline attachments are replaced with compact metadata
	/// placeholders in the trace instead of being captured verbatim. Defaults to <see langword="true"/>:
	/// a base64 image embedded in a request body becomes a marker such as <c>[image omitted: ~245 KB]</c>,
	/// keeping the trace small, readable, and free of the uploaded bytes. Set to <see langword="false"/>
	/// to capture attachments verbatim, useful only when the exact bytes must be inspected.
	/// </summary>
	public bool RedactAttachments { get; set; } = true;

	/// <inheritdoc/>
	public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
	{
		// The remaining rules only matter when tracing is on; a disabled tracer ignores its settings.
		if (!Enabled) yield break;

		if (string.IsNullOrWhiteSpace(Directory))
		{
			yield return new ValidationResult(
				"Request tracing directory must be non-blank when tracing is enabled.",
				[nameof(Directory)]);
		}
		else if (!IsPathSyntacticallyValid(Directory))
		{
			yield return new ValidationResult(
				"Request tracing directory is not a valid path.",
				[nameof(Directory)]);
		}

		if (MaxFiles <= 0)
		{
			yield return new ValidationResult(
				"Request tracing file limit must be greater than zero.",
				[nameof(MaxFiles)]);
		}

		// A null cap means "no limit" and is valid; only a present, non-positive cap is a misconfiguration.
		if (MaxBodyBytes is <= 0)
		{
			yield return new ValidationResult(
				"Request tracing body byte limit must be greater than zero when set.",
				[nameof(MaxBodyBytes)]);
		}
	}

	/// <summary>
	/// Creates a deep copy of these tracing settings. Every property is value-typed or an immutable
	/// <see cref="string"/>, so the copy shares no mutable state with this instance and the editor can edit the
	/// copy without touching the live snapshot.
	/// </summary>
	/// <returns>A standalone copy carrying every tracing setting.</returns>
	public RequestTracingOptions DeepClone() => new()
	{
		Enabled = Enabled,
		Directory = Directory,
		MaxFiles = MaxFiles,
		MaxBodyBytes = MaxBodyBytes,
		RedactAttachments = RedactAttachments
	};

	/// <summary>
	/// Performs a lightweight syntax check on the configured directory by asking the runtime to resolve it to
	/// a full path. The call throws on characters illegal on the current OS, on unsupported path shapes, or when
	/// the resolved path would exceed the platform's maximum length, without requiring file-system access or
	/// write permissions.
	/// </summary>
	/// <param name="directory">The directory value to check.</param>
	/// <returns>
	/// <see langword="true"/> when the value can be resolved to a full path; otherwise <see langword="false"/>.
	/// </returns>
	private static bool IsPathSyntacticallyValid(string directory)
	{
		try
		{
			_ = Path.GetFullPath(directory);
			return true;
		}
		catch (ArgumentException)
		{
			return false;
		}
		catch (NotSupportedException)
		{
			return false;
		}
		catch (PathTooLongException)
		{
			return false;
		}
	}
}
