// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json.Nodes;

using OllamaProxy.Admin.Config;
using OllamaProxy.Configuration;
using OllamaProxy.Hosting;

namespace OllamaProxy.Tests.Admin.Config;

/// <summary>
/// Shared setup, builder, and assertion helpers for <see cref="ProxyConfigWriterTests"/>. The desired-state
/// builders produce <see cref="ProxyOptions"/> instances, while the JSON helpers parse the file content the
/// writer produced so tests can assert against the persisted document tree rather than a brittle raw string.
/// </summary>
public sealed partial class ProxyConfigWriterTests
{
	/// <summary>
	/// A stand-in backend base URL used throughout the writer tests; its concrete value is irrelevant because
	/// the writer never validates it, it only round-trips it through the desired state.
	/// </summary>
	private const string DefaultBaseUrl = "https://api.example.com/v1";

	/// <summary>
	/// Creates a writer wired to the supplied in-memory file double and the default admin options.
	/// </summary>
	/// <param name="file">The file double the writer reads existing state from and rewrites.</param>
	/// <returns>A configured <see cref="ProxyConfigWriter"/> ready to drive in a test.</returns>
	private static ProxyConfigWriter CreateSut(FakeWritableProxyConfigFile file) =>
		new(file, OptionsHelper.DefaultAdminOptions);

	/// <summary>
	/// Creates a writer wired to the supplied file double and an explicit API-key persistence policy.
	/// </summary>
	/// <param name="file">The file double the writer reads existing state from and rewrites.</param>
	/// <param name="policy">The persistence policy to expose through <see cref="AdminOptions"/>.</param>
	/// <returns>A configured <see cref="ProxyConfigWriter"/> ready to drive in a test.</returns>
	private static ProxyConfigWriter CreateSut(FakeWritableProxyConfigFile file, ApiKeyPersistencePolicy policy) =>
		new(file, OptionsHelper.AdminOptionsWith(policy));

	/// <summary>
	/// Builds a desired <see cref="ProxyOptions"/> state whose backends each declare the given mode, one backend
	/// per supplied (name, API key) pair. Each backend owns its mode, so the mode is applied per backend rather
	/// than to a section-level field that no longer exists. The API keys are deliberately easy to spot in
	/// assertions: under <see cref="ApiKeyPersistencePolicy.WriteToFile"/> the writer persists them verbatim,
	/// under <see cref="ApiKeyPersistencePolicy.EnvironmentOnly"/> it blanks them.
	/// </summary>
	/// <param name="mode">The operating mode each desired backend declares.</param>
	/// <param name="backends">The backends to add, each as a (name, API key) pair.</param>
	/// <returns>The assembled desired state.</returns>
	private static ProxyOptions OptionsWith(OperatingMode mode, params (string Name, string ApiKey)[] backends)
	{
		ProxyOptions options = new()
		{
			// A non-default listener URL makes it easy to assert that the property round-trips through the
			// writer without being silently dropped or reset.
			ListenUrl = "http://127.0.0.1:49999"
		};
		foreach ((string name, string apiKey) in backends)
		{
			options.Backends[name] = Backend(apiKey, mode);
		}

		return options;
	}

	/// <summary>
	/// Reads the persisted listener URL from a parsed document, or <see langword="null"/> when it is absent.
	/// </summary>
	/// <param name="root">The parsed document root.</param>
	/// <returns>The persisted listener URL string, or <see langword="null"/> when not present.</returns>
	private static string? ListenUrlOf(JsonNode root) =>
		root[ProxyOptions.SectionName]?["ListenUrl"]?.GetValue<string>();

	/// <summary>
	/// Builds a single backend carrying the supplied API key and mode, plus the shared default base URL.
	/// </summary>
	/// <param name="apiKey">
	/// The API key the desired backend carries; persisted verbatim under
	/// <see cref="ApiKeyPersistencePolicy.WriteToFile"/> and blanked under
	/// <see cref="ApiKeyPersistencePolicy.EnvironmentOnly"/>.
	/// </param>
	/// <param name="mode">The operating mode the backend declares.</param>
	/// <param name="baseUrl">The backend base URL; defaults to <see cref="DefaultBaseUrl"/>.</param>
	/// <returns>The assembled backend options.</returns>
	private static BackendOptions Backend(string apiKey, OperatingMode mode, string baseUrl = DefaultBaseUrl) => new()
	{
		BaseUrl = baseUrl,
		ProviderType = "openai",
		ApiKey = apiKey,
		Mode = mode
	};

	/// <summary>
	/// Builds a minimal on-disk file content carrying a single backend with the given key, used to seed the
	/// "previous state" the writer reads before rewriting.
	/// </summary>
	/// <param name="backendName">The backend key to seed.</param>
	/// <param name="apiKey">The API key value to store on disk for that backend.</param>
	/// <returns>A JSON document string suitable as the file double's initial content.</returns>
	private static string DiskJsonWithBackend(string backendName, string apiKey) => $$"""
	                                                                                  {
	                                                                                    "OllamaProxy": {
	                                                                                  	"Backends": {
	                                                                                  	  "{{backendName}}": {
	                                                                                  		"BaseUrl": "{{DefaultBaseUrl}}",
	                                                                                  		"Mode": "PlugAndPlay",
	                                                                                  		"ApiKey": "{{apiKey}}"
	                                                                                  	  }
	                                                                                  	}
	                                                                                    }
	                                                                                  }
	                                                                                  """;

	/// <summary>
	/// Parses the content of the writer's most recent successful write into a document tree, asserting that a
	/// write actually occurred.
	/// </summary>
	/// <param name="file">The file double whose written content is parsed.</param>
	/// <returns>The parsed document root.</returns>
	private static JsonNode ParseWritten(FakeWritableProxyConfigFile file)
	{
		Assert.NotNull(file.LastWrittenContent);

		JsonNode? parsed = JsonNode.Parse(file.LastWrittenContent!);
		Assert.NotNull(parsed);

		return parsed;
	}

	/// <summary>
	/// Reads the persisted API key of the named backend from a parsed document, or <see langword="null"/> when
	/// the backend or its key is absent.
	/// </summary>
	/// <param name="root">The parsed document root.</param>
	/// <param name="backendName">The backend whose persisted key is read.</param>
	/// <returns>The persisted API key string, or <see langword="null"/> when not present.</returns>
	private static string? ApiKeyOf(JsonNode root, string backendName) =>
		root[ProxyOptions.SectionName]?["Backends"]?[backendName]?["ApiKey"]?.GetValue<string>();

	/// <summary>
	/// Reads the persisted operating mode of the named backend from a parsed document, or <see langword="null"/>
	/// when the backend or its mode is absent. Each backend now owns its mode, so the writer serializes it under
	/// the backend node rather than at the section level.
	/// </summary>
	/// <param name="root">The parsed document root.</param>
	/// <param name="backendName">The backend whose persisted mode is read.</param>
	/// <returns>The persisted mode name, or <see langword="null"/> when not present.</returns>
	private static string? ModeOf(JsonNode root, string backendName) =>
		root[ProxyOptions.SectionName]?["Backends"]?[backendName]?["Mode"]?.GetValue<string>();

	/// <summary>
	/// Reads the persisted backends map from a parsed document, asserting the proxy section and its backends
	/// object are present.
	/// </summary>
	/// <param name="root">The parsed document root.</param>
	/// <returns>The persisted backends object.</returns>
	private static JsonObject BackendsOf(JsonNode root) =>
		Assert.IsType<JsonObject>(root[ProxyOptions.SectionName]?["Backends"]);
}
