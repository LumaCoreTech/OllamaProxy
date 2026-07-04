// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text.Json;
using System.Text.Json.Nodes;

using OllamaProxy.Admin.Config;
using OllamaProxy.Configuration;

namespace OllamaProxy.Tests.Admin.Config;

// Section-preserving config rewrite: replace OllamaProxy, keep everything else, never leak a secret.
//
// These tests follow WriteAsync as it turns a desired ProxyOptions state into the operator file's new content,
// proving the three properties the single-file persistence model depends on:
//
//   1. Section replacement: the OllamaProxy section becomes the desired state, removed backends are gone, and
//      enums round-trip by name (WhenFileIsEmpty, WhenReplacingExistingSection, WhenBackendRemoved).
//
//   2. Sibling preservation: Logging, AllowedHosts, Kestrel and any other top-level sections survive the
//      rewrite untouched — critical because the foreground operator file is the one and only appsettings.json
//      (WhenSiblingSectionsPresent).
//
//   3. Secret policy on entered keys: the admin view is file-only, so the keys reaching the writer are the
//      operator's entered values. The default WriteToFile persists them verbatim — overwriting a previous
//      on-disk key (WhenWriteToFile_PersistsEnteredKeyOverExistingKey), setting a first key where disk had none
//      (WhenWriteToFileAndNoKeyOnDisk_PersistsEnteredKey), and writing a brand-new backend's key
//      (WhenWriteToFileAndBackendIsNew_PersistsEnteredKey) — while EnvironmentOnly blanks every key
//      (WhenEnvironmentOnly_BlanksKeys). The active policy is taken from AdminOptions because it is a
//      deployment-level decision, not a per-apply choice. A corrupt file aborts the write so siblings are never
//      lost (WhenExistingFileIsMalformed).
//
// Argument guards close the file. For the shared file double and builders, see FakeWritableProxyConfigFile and
// Helpers; for the write-then-recycle orchestration that consumes this writer, see ProxyConfigApplierTests.
[Trait("Category", "Unit")]
public sealed partial class ProxyConfigWriterTests
{
	#region WriteAsync

	// --- 1. Section replacement ---

	/// <summary>
	/// Verifies that writing against an absent file (the first-write state under the Windows Service) produces a
	/// document whose <c>OllamaProxy</c> section reflects the desired state, with the mode serialized by name.
	/// </summary>
	[Fact]
	public async Task WriteAsync_WhenFileIsEmpty_WritesDesiredSectionWithEnumByName()
	{
		// Arrange: no file on disk yet; a desired state in Explicit mode with one backend.
		FakeWritableProxyConfigFile file = new(initialContent: null);
		ProxyOptions desired = OptionsWith(OperatingMode.Explicit, ("default", "disk-key-123"));
		ProxyConfigWriter sut = CreateSut(file);

		// Act
		await sut.WriteAsync(desired, CancellationToken.None);

		// Assert: exactly one write landed, and the backend carries the desired mode (as a name, not the numeric
		// enum value) and its base URL.
		Assert.Equal(1, file.WriteCount);
		JsonNode root = ParseWritten(file);
		Assert.Equal("Explicit", ModeOf(root, "default"));
		Assert.Equal(
			DefaultBaseUrl,
			root[ProxyOptions.SectionName]?["Backends"]?["default"]?["BaseUrl"]?.GetValue<string>());
	}

	/// <summary>
	/// Verifies that an existing <c>OllamaProxy</c> section is replaced wholesale by the desired state rather than
	/// merged, so a desired state's mode overwrites the previous one.
	/// </summary>
	[Fact]
	public async Task WriteAsync_WhenReplacingExistingSection_OverwritesPreviousProxySection()
	{
		// Arrange: an on-disk backend in PlugAndPlay mode; the desired state switches that backend to Hybrid. The
		// key value is irrelevant here — this test only asserts the mode — so a neutral placeholder is used.
		FakeWritableProxyConfigFile file = new(DiskJsonWithBackend("default", "old-disk-key-123"));
		ProxyOptions desired = OptionsWith(OperatingMode.Hybrid, ("default", "some-key-123"));
		ProxyConfigWriter sut = CreateSut(file);

		// Act
		await sut.WriteAsync(desired, CancellationToken.None);

		// Assert: the persisted backend mode is the desired Hybrid, not the previous PlugAndPlay.
		JsonNode root = ParseWritten(file);
		Assert.Equal("Hybrid", ModeOf(root, "default"));
	}

	/// <summary>
	/// Verifies that a backend present on disk but absent from the desired state is genuinely gone after the
	/// write — the whole-section replacement is what lets the admin UI delete a backend, which a merge could not.
	/// </summary>
	[Fact]
	public async Task WriteAsync_WhenBackendRemoved_DropsItFromPersistedSection()
	{
		// Arrange: two backends on disk, but the desired state keeps only one.
		string twoBackends = """
		                     {
		                       "OllamaProxy": {
		                     	"Backends": {
		                     	  "keep": { "BaseUrl": "https://keep.example.com/v1", "Mode": "PlugAndPlay", "ApiKey": "keep-key-123" },
		                     	  "drop": { "BaseUrl": "https://drop.example.com/v1", "Mode": "PlugAndPlay", "ApiKey": "drop-key-123" }
		                     	}
		                       }
		                     }
		                     """;
		FakeWritableProxyConfigFile file = new(twoBackends);
		ProxyOptions desired = OptionsWith(OperatingMode.PlugAndPlay, ("keep", "some-key-123"));
		ProxyConfigWriter sut = CreateSut(file);

		// Act
		await sut.WriteAsync(desired, CancellationToken.None);

		// Assert: only the kept backend remains; the dropped one is gone (not merely blanked).
		JsonObject backends = BackendsOf(ParseWritten(file));
		Assert.Equal(["keep"], backends.Select(pair => pair.Key));
	}

	// --- 2. Sibling preservation ---

	/// <summary>
	/// Verifies that every top-level section other than <c>OllamaProxy</c> survives the rewrite byte-for-byte —
	/// the property that makes it safe for the single foreground <c>appsettings.json</c> to also hold
	/// <c>Logging</c>, <c>AllowedHosts</c>, or legacy <c>Kestrel</c> configuration.
	/// </summary>
	[Fact]
	public async Task WriteAsync_WhenSiblingSectionsPresent_PreservesThemUntouched()
	{
		// Arrange: a file that mixes the proxy section with unrelated sibling sections, including a legacy
		// Kestrel endpoint that the writer must not disturb.
		string mixed = """
		               {
		                 "Logging": { "LogLevel": { "Default": "Warning" } },
		                 "Kestrel": { "Endpoints": { "Http": { "Url": "http://localhost:11434" } } },
		                 "AllowedHosts": "proxy.example.com",
		                 "OllamaProxy": {
		                   "ListenUrl": "http://localhost:11434",
		                   "Backends": { "default": { "BaseUrl": "https://old.example.com/v1", "Mode": "PlugAndPlay", "ApiKey": "disk-key-123" } }
		                 }
		               }
		               """;
		FakeWritableProxyConfigFile file = new(mixed);
		ProxyOptions desired = OptionsWith(OperatingMode.Explicit, ("default", "some-key-123"));
		ProxyConfigWriter sut = CreateSut(file);

		// Act
		await sut.WriteAsync(desired, CancellationToken.None);

		// Assert: the three sibling sections are preserved exactly, while the proxy section reflects the change.
		JsonNode root = ParseWritten(file);
		Assert.Equal("Warning", root["Logging"]?["LogLevel"]?["Default"]?.GetValue<string>());
		Assert.Equal(
			"http://localhost:11434",
			root["Kestrel"]?["Endpoints"]?["Http"]?["Url"]?.GetValue<string>());
		Assert.Equal("proxy.example.com", root["AllowedHosts"]?.GetValue<string>());
		Assert.Equal("Explicit", ModeOf(root, "default"));
		Assert.Equal("http://127.0.0.1:49999", ListenUrlOf(root));
	}

	/// <summary>
	/// Verifies that the proxy listener URL is serialized as part of the <c>OllamaProxy</c> section when the
	/// writer rewrites the file.
	/// </summary>
	[Fact]
	public async Task WriteAsync_PersistsListenUrlInProxySection()
	{
		// Arrange
		FakeWritableProxyConfigFile file = new(initialContent: null);
		ProxyOptions desired = OptionsWith(OperatingMode.PlugAndPlay, ("default", "some-key-123"));
		ProxyConfigWriter sut = CreateSut(file);

		// Act
		await sut.WriteAsync(desired, CancellationToken.None);

		// Assert
		Assert.Equal("http://127.0.0.1:49999", ListenUrlOf(ParseWritten(file)));
	}

	// --- 3. Secret policy on entered keys ---

	/// <summary>
	/// Verifies that under <see cref="ApiKeyPersistencePolicy.WriteToFile"/> the operator-entered key is persisted
	/// verbatim, overwriting a different key already on disk — the admin surface edits a file-only view, so the
	/// entered value is authoritative.
	/// </summary>
	[Fact]
	public async Task WriteAsync_WhenWriteToFile_PersistsEnteredKeyOverExistingKey()
	{
		// Arrange: a backend with an old key on disk; the desired state carries a newly entered, different key.
		FakeWritableProxyConfigFile file = new(DiskJsonWithBackend("default", "old-disk-key-123"));
		ProxyOptions desired = OptionsWith(OperatingMode.PlugAndPlay, ("default", "entered-key-999"));
		ProxyConfigWriter sut = CreateSut(file);

		// Act
		await sut.WriteAsync(desired, CancellationToken.None);

		// Assert: the persisted key is the entered one, proving it overwrites the previous on-disk value.
		Assert.Equal("entered-key-999", ApiKeyOf(ParseWritten(file), "default"));
	}

	/// <summary>
	/// Verifies that under <see cref="ApiKeyPersistencePolicy.WriteToFile"/> an entered key is persisted even when
	/// the file previously held none — the admin surface's first-time key entry for an existing backend.
	/// </summary>
	[Fact]
	public async Task WriteAsync_WhenWriteToFileAndNoKeyOnDisk_PersistsEnteredKey()
	{
		// Arrange: the backend has no key on disk yet; the operator enters one through the admin surface.
		FakeWritableProxyConfigFile file = new(DiskJsonWithBackend("default", apiKey: ""));
		ProxyOptions desired = OptionsWith(OperatingMode.PlugAndPlay, ("default", "first-entered-key-999"));
		ProxyConfigWriter sut = CreateSut(file);

		// Act
		await sut.WriteAsync(desired, CancellationToken.None);

		// Assert: the entered key is now persisted, where the file previously carried an empty key.
		Assert.Equal("first-entered-key-999", ApiKeyOf(ParseWritten(file), "default"));
	}

	/// <summary>
	/// Verifies that a brand-new backend (absent from the on-disk file) is written with its entered key under
	/// <see cref="ApiKeyPersistencePolicy.WriteToFile"/>, alongside an edited key for the existing backend — the
	/// admin surface adds backends together with their keys in a single write.
	/// </summary>
	[Fact]
	public async Task WriteAsync_WhenWriteToFileAndBackendIsNew_PersistsEnteredKey()
	{
		// Arrange: disk has only "existing"; the desired state edits its key and adds a brand-new backend "fresh".
		FakeWritableProxyConfigFile file = new(DiskJsonWithBackend("existing", "old-disk-key-123"));
		ProxyOptions desired = OptionsWith(
			OperatingMode.PlugAndPlay,
			("existing", "existing-entered-key-999"),
			("fresh", "fresh-entered-key-999"));
		ProxyConfigWriter sut = CreateSut(file);

		// Act
		await sut.WriteAsync(desired, CancellationToken.None);

		// Assert: both backends carry their entered keys — the new backend is no longer written keyless.
		JsonNode root = ParseWritten(file);
		Assert.Equal("existing-entered-key-999", ApiKeyOf(root, "existing"));
		Assert.Equal("fresh-entered-key-999", ApiKeyOf(root, "fresh"));
	}

	/// <summary>
	/// Verifies that under <see cref="ApiKeyPersistencePolicy.EnvironmentOnly"/> every backend's key is blanked in
	/// the written file — even an entered key — forcing the secret to be supplied through an environment variable.
	/// </summary>
	[Fact]
	public async Task WriteAsync_WhenEnvironmentOnly_BlanksKeys()
	{
		// Arrange: a backend carrying an entered key; the policy demands it be scrubbed from the file so the secret
		// must come from an environment variable at runtime.
		FakeWritableProxyConfigFile file = new(DiskJsonWithBackend("default", "old-disk-key-123"));
		ProxyOptions desired = OptionsWith(OperatingMode.PlugAndPlay, ("default", "entered-key-999"));
		ProxyConfigWriter sut = CreateSut(file, ApiKeyPersistencePolicy.EnvironmentOnly);

		// Act
		await sut.WriteAsync(desired, CancellationToken.None);

		// Assert: the persisted key is empty even though both disk and the desired state carried one.
		Assert.Equal(string.Empty, ApiKeyOf(ParseWritten(file), "default"));
	}

	/// <summary>
	/// Verifies that a malformed existing file aborts the write with a <see cref="JsonException"/> and leaves the
	/// file untouched, so a corrupt file never causes the preserved sibling sections to be lost.
	/// </summary>
	[Fact]
	public async Task WriteAsync_WhenExistingFileIsMalformed_ThrowsAndDoesNotWrite()
	{
		// Arrange: the on-disk content is not valid JSON. The key value is irrelevant — the write aborts before
		// any key is persisted — so a neutral placeholder is used.
		FakeWritableProxyConfigFile file = new("{ this is not json");
		ProxyOptions desired = OptionsWith(OperatingMode.PlugAndPlay, ("default", "some-key-123"));
		ProxyConfigWriter sut = CreateSut(file);

		// Act + Assert: the parse failure surfaces and nothing is written. ThrowsAny accepts the derived
		// JsonReaderException that JsonNode.Parse raises, matching the documented JsonException contract.
		await Assert.ThrowsAnyAsync<JsonException>(() => sut.WriteAsync(
			desired,
			CancellationToken.None));
		Assert.Equal(0, file.WriteCount);
	}

	// --- Argument guards ---

	/// <summary>
	/// Verifies that <see cref="ProxyConfigWriter.WriteAsync"/> rejects a <see langword="null"/> desired state.
	/// </summary>
	[Fact]
	public async Task WriteAsync_WhenDesiredStateIsNull_ThrowsArgumentNullException()
	{
		// Arrange
		FakeWritableProxyConfigFile file = new();
		ProxyConfigWriter sut = CreateSut(file);

		// Act + Assert
		var exception = await Assert.ThrowsAsync<ArgumentNullException>(() => sut.WriteAsync(
			                null!,
			                CancellationToken.None));
		Assert.Equal("desiredState", exception.ParamName);
	}

	/// <summary>
	/// Verifies that the constructor rejects a <see langword="null"/> file.
	/// </summary>
	[Fact]
	public void Constructor_WhenFileIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception =
			Assert.Throws<ArgumentNullException>(() => new ProxyConfigWriter(null!, OptionsHelper.DefaultAdminOptions));
		Assert.Equal("file", exception.ParamName);
	}

	#endregion
}
