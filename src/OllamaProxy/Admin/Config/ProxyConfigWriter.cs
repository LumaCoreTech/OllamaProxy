// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Options;

using OllamaProxy.Configuration;
using OllamaProxy.Hosting;

namespace OllamaProxy.Admin.Config;

/// <summary>
/// The default <see cref="IProxyConfigWriter"/>: rewrites the operator configuration file so its
/// <c>OllamaProxy</c> section becomes the supplied desired state, while preserving every sibling section
/// and applying the <see cref="ApiKeyPersistencePolicy"/> to the entered keys it carries.
/// </summary>
/// <remarks>
///     <para>
///     The file is treated as a parsed JSON document, not as text: the existing content is read, parsed into
///     a node tree (tolerating the <c>//</c> comments and trailing commas the .NET configuration provider
///     allows), its <c>OllamaProxy</c> node is replaced, and the whole tree is reserialized. This is what
///     preserves unrelated sections. Under foreground hosting the operator file is the one and only
///     <c>appsettings.json</c>, so it also carries <c>Logging</c>, <c>Kestrel</c>, and <c>AllowedHosts</c>,
///     none of which may be lost when the proxy section is rewritten.
///     </para>
///     <para>
///     Comments are skipped during parsing and therefore do not survive the rewrite; that loss is by design
///     and is offset by the shipped, never-loaded <c>appsettings.reference.json</c>.
///     </para>
/// </remarks>
sealed class ProxyConfigWriter : IProxyConfigWriter
{
	/// <summary>
	/// The parse options for the existing file. Comments and trailing commas are valid in the live
	/// configuration (the .NET configuration provider accepts them), so reading the file back must skip them
	/// rather than fail; they are intentionally not carried into the rewrite.
	/// </summary>
	private static readonly JsonDocumentOptions ParseOptions = new()
	{
		CommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true
	};

	/// <summary>
	/// The options used to project the desired <see cref="ProxyOptions"/> into the section node: enums are
	/// written as their names (so <c>Mode</c> and <c>ReasoningEffort</c> read as <c>"Explicit"</c> /
	/// <c>"Max"</c>) and nulls are omitted to keep the section lean. Indentation is applied later, when the
	/// whole document is serialized, so it is not configured here.
	/// </summary>
	private static readonly JsonSerializerOptions SectionSerializeOptions = new()
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Converters = { new JsonStringEnumConverter() }
	};

	/// <summary>
	/// The options used to serialize the final document. Only indentation is set: critically, null-ignoring is
	/// <em>not</em> applied here, so an explicit JSON null an operator placed in a preserved sibling section is
	/// written back verbatim rather than silently dropped.
	/// </summary>
	private static readonly JsonSerializerOptions OutputOptions = new()
	{
		WriteIndented = true
	};

	private readonly IWritableProxyConfigFile mFile;
	private readonly IOptions<AdminOptions>   mAdminOptions;

	/// <summary>
	/// Initializes a new instance of the <see cref="ProxyConfigWriter"/> class.
	/// </summary>
	/// <param name="file">The operator configuration file this writer reads existing state from and rewrites.</param>
	/// <param name="adminOptions">The admin-surface options that govern the API-key persistence policy.</param>
	/// <exception cref="ArgumentNullException">
	/// <paramref name="file"/> or <paramref name="adminOptions"/> is <see langword="null"/>.
	/// </exception>
	public ProxyConfigWriter(
		IWritableProxyConfigFile file,
		IOptions<AdminOptions>   adminOptions)
	{
		ArgumentNullException.ThrowIfNull(file);
		ArgumentNullException.ThrowIfNull(adminOptions);

		mFile = file;
		mAdminOptions = adminOptions;
	}

	/// <inheritdoc/>
	public async Task WriteAsync(
		ProxyOptions      desiredState,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(desiredState);

		// Read the current on-disk file as raw text, so every sibling section (Logging, Kestrel, AllowedHosts)
		// is preserved across the rewrite. It is absent under the Windows Service before the first write.
		string? existingContent = await mFile.ReadAsync(cancellationToken).ConfigureAwait(false);

		JsonObject root = ParseRootOrEmpty(existingContent);

		// The admin configuration view is file-only (see BuildProxyOptionsConfiguration), so the keys carried by
		// desiredState are the operator's entered, file-sourced keys, never an environment-only secret. They are
		// therefore safe to persist verbatim; the configured policy only decides whether to keep or blank them.
		JsonNode section = JsonSerializer.SerializeToNode(desiredState, SectionSerializeOptions) ??
		                   throw new JsonException("Serializing the desired proxy configuration produced no JSON.");

		ApplyApiKeyPolicy(section, mAdminOptions.Value.ApiKeyPersistencePolicy);

		// Replace ONLY the OllamaProxy section; every sibling section is left exactly as it was on disk.
		root[ProxyOptions.SectionName] = section;

		string output = root.ToJsonString(OutputOptions);
		await mFile.WriteAsync(output, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Parses the existing file content into the document root, or yields an empty object when there is no file
	/// yet (the normal first-write state under the Windows Service).
	/// </summary>
	/// <param name="existingContent">The raw on-disk content, or <see langword="null"/> when the file is absent.</param>
	/// <returns>The mutable document root the rewritten section is grafted onto.</returns>
	/// <exception cref="JsonException">
	/// <paramref name="existingContent"/> is present but is not a JSON object (a bare array, string, or null),
	/// which is malformed for an <c>appsettings</c> file; failing here leaves the file untouched rather than
	/// discarding its real content.
	/// </exception>
	private static JsonObject ParseRootOrEmpty(string? existingContent)
	{
		if (string.IsNullOrWhiteSpace(existingContent))
		{
			return new JsonObject();
		}

		JsonNode? parsed = JsonNode.Parse(existingContent, nodeOptions: null, documentOptions: ParseOptions);

		return parsed as JsonObject ??
		       throw new JsonException("The operator configuration file's root must be a JSON object.");
	}

	/// <summary>
	/// Applies the secret policy to the freshly serialized section. Under
	/// <see cref="ApiKeyPersistencePolicy.WriteToFile"/> this is a no-op: the keys the operator entered are
	/// already in the section and are persisted verbatim. Under <see cref="ApiKeyPersistencePolicy.EnvironmentOnly"/>
	/// every backend's <c>ApiKey</c> is blanked, forcing the secret to be supplied through an environment
	/// variable that only the running proxy reads.
	/// </summary>
	/// <param name="section">The serialized <c>OllamaProxy</c> section whose backend keys are rewritten in place.</param>
	/// <param name="apiKeyPolicy">Whether to persist the entered keys or blank them in favor of environment variables.</param>
	private static void ApplyApiKeyPolicy(JsonNode section, ApiKeyPersistencePolicy apiKeyPolicy)
	{
		// WriteToFile keeps the entered keys exactly as serialized, so there is nothing to do. Only EnvironmentOnly
		// mutates the section, so the WriteToFile path skips the backend walk entirely.
		if (apiKeyPolicy == ApiKeyPersistencePolicy.WriteToFile)
		{
			return;
		}

		if (section["Backends"] is not JsonObject backends)
		{
			return;
		}

		foreach (KeyValuePair<string, JsonNode?> backend in backends)
		{
			if (backend.Value is JsonObject backendObject)
			{
				// EnvironmentOnly: scrub the key from the file so the secret must come from an environment
				// variable at runtime. This is the only path that blanks a key, including for a new backend.
				backendObject["ApiKey"] = string.Empty;
			}
		}
	}
}
