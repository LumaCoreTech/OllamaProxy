// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.ComponentModel.DataAnnotations;

using OllamaProxy.Configuration;

namespace OllamaProxy.Tests.Configuration;

/// <summary>
/// Tests for <see cref="ProxyOptions.Validate"/>, the startup fail-fast rules for the root options.
/// The story moves from a fully valid configuration through the cross-cutting rules: at least one
/// backend, the recursion that surfaces a nested backend's own validation failures with a
/// path-qualified member, and the further recursion into each backend's registry entries. An
/// Explicit-mode backend with an empty registry is intentionally allowed — it simply contributes no
/// models — so no registry-required rule fires.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ProxyOptionsTests
{
	private static BackendOptions ValidBackend() => new()
		{ BaseUrl = "https://api.openai.com/v1", ProviderType = "openai", ApiKey = "sk-abcdefgh" };

	private static List<ValidationResult> Validate(ProxyOptions options)
	{
		List<ValidationResult> results = [];
		Validator.TryValidateObject(
			options,
			new ValidationContext(options),
			results,
			validateAllProperties: true);
		return results;
	}

	/// <summary>
	/// Verifies that <see cref="ProxyOptions.Validate"/> reports no errors for a minimal valid
	/// configuration with one well-formed backend.
	/// </summary>
	[Fact]
	public void Validate_WhenSingleValidBackend_ReturnsNoErrors()
	{
		// Arrange
		ProxyOptions options = new()
		{
			Backends =
			{
				["default"] = ValidBackend()
			}
		};

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		Assert.Empty(results);
	}

	/// <summary>
	/// Verifies that <see cref="ProxyOptions.Validate"/> accepts an empty <see cref="ProxyOptions.Backends"/>
	/// map. This is the valid initial state after a fresh install: the proxy starts with no models and
	/// the admin UI can be used to add backends without the process crashing.
	/// </summary>
	[Fact]
	public void Validate_WhenNoBackends_ReturnsNoErrors()
	{
		// Arrange
		ProxyOptions options = new();

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		Assert.Empty(results);
	}

	/// <summary>
	/// Verifies that <see cref="ProxyOptions.Validate"/> accepts an <see cref="OperatingMode.Explicit"/>
	/// backend with an empty registry: this is intentionally permissive — the backend simply contributes
	/// no models rather than failing startup.
	/// </summary>
	[Fact]
	public void Validate_WhenExplicitModeAndEmptyRegistry_ReturnsNoErrors()
	{
		// Arrange: a backend whose own mode is Explicit, with no registry entries.
		BackendOptions backend = ValidBackend();
		backend.Mode = OperatingMode.Explicit;
		ProxyOptions options = new()
		{
			Backends =
			{
				["default"] = backend
			}
		};

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		Assert.Empty(results);
	}

	/// <summary>
	/// Verifies that <see cref="ProxyOptions.Validate"/> accepts an embedding-only registry entry that
	/// disables completion but enables embeddings, since such a model still exposes a usable endpoint.
	/// </summary>
	[Fact]
	public void Validate_WhenEmbeddingOnlyModel_ReturnsNoCapabilityError()
	{
		// Arrange: a model that opts out of completion but into embeddings — a valid embedding-only shape —
		// pinned in its backend's own registry.
		BackendOptions backend = ValidBackend();
		backend.Models.Add(
			new ModelRegistrationOptions
			{
				Name = "nomic-embed-text",
				ContextLength = 8192,
				SupportsCompletion = false,
				SupportsEmbeddings = true
			});
		ProxyOptions options = new()
		{
			Backends =
			{
				["default"] = backend
			}
		};

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		Assert.Empty(results);
	}

	/// <summary>
	/// Verifies that <see cref="ProxyOptions.Validate"/> rejects a registry entry that disables
	/// completion without enabling embeddings, since the resulting model would expose no usable Ollama
	/// endpoint, and that the failure points at both capability members under the entry's backend-qualified path.
	/// </summary>
	[Fact]
	public void Validate_WhenModelDisablesCompletionWithoutEmbeddings_ReportsNoUsableEndpoint()
	{
		// Arrange: completion off and embeddings left unset (defaults to false) — no usable endpoint — pinned
		// in its backend's own registry.
		BackendOptions backend = ValidBackend();
		backend.Models.Add(
			new ModelRegistrationOptions
			{
				Name = "broken",
				ContextLength = 4096,
				SupportsCompletion = false
			});
		ProxyOptions options = new()
		{
			Backends =
			{
				["default"] = backend
			}
		};

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert: the entry's own rule fires, path-qualified through the backend to both capability members.
		Assert.Contains(
			results,
			r =>
				r.ErrorMessage ==
				"Model must support completion or embeddings; an entry that disables completion must set " +
				"'SupportsEmbeddings' to true." &&
				r.MemberNames.Contains(
					$"Backends[default].{nameof(BackendOptions.Models)}[0].{nameof(ModelRegistrationOptions.SupportsCompletion)}") &&
				r.MemberNames.Contains(
					$"Backends[default].{nameof(BackendOptions.Models)}[0].{nameof(ModelRegistrationOptions.SupportsEmbeddings)}"));
	}

	/// <summary>
	/// Verifies that <see cref="ProxyOptions.Validate"/> surfaces a nested backend's own validation
	/// failure, qualifying the member name with the backend's configuration path.
	/// </summary>
	[Fact]
	public void Validate_WhenNestedBackendInvalid_SurfacesPathQualifiedFailure()
	{
		// Arrange: a backend missing its API key fails the nested secret rule.
		ProxyOptions options = new()
		{
			Backends =
			{
				["default"] = new BackendOptions
				{
					BaseUrl = "https://x/v1",
					ProviderType = "openai"
				}
			}
		};

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		Assert.Contains(results, r => r.MemberNames.Contains("Backends[default].ApiKey"));
	}

	/// <summary>
	/// Verifies that a backend accepts a well-formed <see cref="BackendOptions.ModelPrefix"/>, since a
	/// simple non-empty token without the separator is the intended opt-in shape.
	/// </summary>
	[Fact]
	public void BackendValidate_WhenModelPrefixWellFormed_ReturnsNoPrefixError()
	{
		// Arrange
		BackendOptions backend = ValidBackend();
		backend.ModelPrefix = "vllm";

		// Act
		List<ValidationResult> results = backend.Validate(new ValidationContext(backend)).ToList();

		// Assert
		Assert.DoesNotContain(results, r => r.MemberNames.Contains(nameof(BackendOptions.ModelPrefix)));
	}

	/// <summary>
	/// Verifies that a backend rejects a blank or separator-bearing
	/// <see cref="BackendOptions.ModelPrefix"/>, since either would yield a confusing or ambiguous
	/// client-facing name.
	/// </summary>
	/// <param name="prefix">The malformed prefix to validate.</param>
	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("vllm/extra")]
	public void BackendValidate_WhenModelPrefixMalformed_ReportsPrefixError(string prefix)
	{
		// Arrange
		BackendOptions backend = ValidBackend();
		backend.ModelPrefix = prefix;

		// Act
		List<ValidationResult> results = backend.Validate(new ValidationContext(backend)).ToList();

		// Assert
		Assert.Contains(
			results,
			r =>
				r.MemberNames.Contains(nameof(BackendOptions.ModelPrefix)) &&
				r.ErrorMessage == "Backend model prefix must be non-blank and must not contain '/' when specified.");
	}

	// --- Model registry duplicate-name validation ---

	/// <summary>
	/// Verifies that a backend accepts two registry entries that resolve to the <em>same upstream model</em>
	/// under <em>distinct</em> client-facing names — the reasoning-variant shape (one upstream id exposed at two
	/// fixed efforts) — since only the exposed names must be unique, not the upstream ids.
	/// </summary>
	[Fact]
	public void BackendValidate_WhenSameUpstreamUnderDistinctNames_ReturnsNoDuplicateError()
	{
		// Arrange: two entries share the upstream "gpt-5" but expose distinct names — the supported variant case.
		BackendOptions backend = ValidBackend();
		backend.Models.Add(
			new ModelRegistrationOptions { Name = "gpt-5-low", UpstreamModel = "gpt-5", ContextLength = 8192 });
		backend.Models.Add(
			new ModelRegistrationOptions { Name = "gpt-5-high", UpstreamModel = "gpt-5", ContextLength = 8192 });

		// Act
		List<ValidationResult> results = backend.Validate(new ValidationContext(backend)).ToList();

		// Assert: distinct exposed names are valid even though the upstream id is shared.
		Assert.DoesNotContain(results, r => r.MemberNames.Contains(nameof(BackendOptions.Models)));
	}

	/// <summary>
	/// Verifies that a backend rejects two registry entries that expose the <em>same</em> client-facing name,
	/// since the catalog keys exposed models by name and the second entry would silently shadow the first. The
	/// failure names the duplicated value and points at <see cref="BackendOptions.Models"/>.
	/// </summary>
	[Fact]
	public void BackendValidate_WhenModelNameDuplicated_ReportsDuplicateError()
	{
		// Arrange: two entries expose the identical name "gpt-5" — a silent catalog collision.
		BackendOptions backend = ValidBackend();
		backend.Models.Add(new ModelRegistrationOptions { Name = "gpt-5", ContextLength = 8192 });
		backend.Models.Add(new ModelRegistrationOptions { Name = "gpt-5", ContextLength = 4096 });

		// Act
		List<ValidationResult> results = backend.Validate(new ValidationContext(backend)).ToList();

		// Assert: exactly one duplicate error, naming the clashing value and pointing at the Models collection.
		ValidationResult error = Assert.Single(
			results,
			r => r.MemberNames.Contains(nameof(BackendOptions.Models)));
		Assert.Equal(
			"Model name 'gpt-5' is registered more than once on this backend; each entry must expose a distinct " +
			"name. Rename one of the entries — for example to offer the same upstream model at a different fixed " +
			"reasoning effort.",
			error.ErrorMessage);
	}

	/// <summary>
	/// Verifies that the duplicate-name check is case-insensitive and trims surrounding whitespace, matching the
	/// catalog's <see cref="StringComparer.OrdinalIgnoreCase"/> key comparer, so names differing only in case or
	/// padding are treated as the same exposed name. The reported value is the trimmed key.
	/// </summary>
	[Fact]
	public void BackendValidate_WhenModelNamesDifferOnlyByCaseOrWhitespace_ReportsDuplicateError()
	{
		// Arrange: "GPT-5" and " gpt-5 " collide once trimmed and compared case-insensitively.
		BackendOptions backend = ValidBackend();
		backend.Models.Add(new ModelRegistrationOptions { Name = "GPT-5", ContextLength = 8192 });
		backend.Models.Add(new ModelRegistrationOptions { Name = " gpt-5 ", ContextLength = 4096 });

		// Act
		List<ValidationResult> results = backend.Validate(new ValidationContext(backend)).ToList();

		// Assert: one duplicate error whose reported value is the trimmed first-seen key.
		ValidationResult error = Assert.Single(
			results,
			r => r.MemberNames.Contains(nameof(BackendOptions.Models)));
		Assert.Equal(
			"Model name 'GPT-5' is registered more than once on this backend; each entry must expose a distinct " +
			"name. Rename one of the entries — for example to offer the same upstream model at a different fixed " +
			"reasoning effort.",
			error.ErrorMessage);
	}

	/// <summary>
	/// Verifies that blank-named entries are not grouped into a collection-level duplicate error: a missing name
	/// is already reported per entry by the <see cref="ModelRegistrationOptions.Name"/> required rule, so the
	/// duplicate scan must skip blanks rather than report two empty strings as a spurious "duplicate". The
	/// per-entry failures stay path-qualified to each entry's own <c>Name</c> member; none point at the
	/// <see cref="BackendOptions.Models"/> collection itself.
	/// </summary>
	[Fact]
	public void BackendValidate_WhenMultipleNamesBlank_ReportsNoDuplicateError()
	{
		// Arrange: two entries with blank names — each fails its own required-name rule, but they must not be
		// grouped into a collection-level "duplicate" error.
		BackendOptions backend = ValidBackend();
		backend.Models.Add(new ModelRegistrationOptions { Name = "", ContextLength = 8192 });
		backend.Models.Add(new ModelRegistrationOptions { Name = "   ", ContextLength = 4096 });

		// Act
		List<ValidationResult> results = backend.Validate(new ValidationContext(backend)).ToList();

		// Assert: each blank entry fails per-entry, path-qualified to its own Name member (asserting on the
		// member path, not the [Required] message, which .NET localizes via satellite assemblies). Crucially,
		// the duplicate scan skips blanks, so no error points at the bare Models collection.
		Assert.DoesNotContain(results, r => r.MemberNames.Contains(nameof(BackendOptions.Models)));
		Assert.Contains(
			results,
			r => r.MemberNames.Contains($"{nameof(BackendOptions.Models)}[0].{nameof(ModelRegistrationOptions.Name)}"));
		Assert.Contains(
			results,
			r => r.MemberNames.Contains($"{nameof(BackendOptions.Models)}[1].{nameof(ModelRegistrationOptions.Name)}"));
	}

	// --- FindDuplicateModelNames() helper ---

	/// <summary>Cases pairing a registry's model names with the duplicates the helper must report.</summary>
	public static TheoryData<string, string[], string[]> DuplicateModelNameCases => new()
	{
		// No names at all — nothing to collide.
		{ "empty registry", [], [] },

		// A single entry cannot duplicate anything.
		{ "single name", ["gpt-5"], [] },

		// Distinct names — the supported same-upstream-variant shape reduces to distinct client names here.
		{ "all distinct", ["gpt-5-low", "gpt-5-high", "claude"], [] },

		// Two identical names collide once.
		{ "exact duplicate", ["gpt-5", "gpt-5"], ["gpt-5"] },

		// Three identical names are still one group, reported once.
		{ "triplicate reported once", ["x", "x", "x"], ["x"] },

		// Case differences collapse (OrdinalIgnoreCase); the first-seen spelling is the reported key.
		{ "case-insensitive duplicate", ["GPT-5", "gpt-5"], ["GPT-5"] },

		// Surrounding whitespace is trimmed before comparison; the trimmed first-seen key is reported.
		{ "whitespace-trimmed duplicate", ["gpt-5", " gpt-5 "], ["gpt-5"] },

		// Blank names are skipped — a missing name is a per-entry failure, never a collection-level duplicate.
		{ "blank names skipped", ["", "   "], [] },

		// Blanks are ignored even when a real duplicate sits alongside them.
		{ "blanks ignored beside a real duplicate", ["", "gpt-5", "gpt-5", "  "], ["gpt-5"] },

		// Independent duplicate groups are each reported once, in first-seen order.
		{ "two duplicate groups, first-seen order", ["b", "b", "a", "a"], ["b", "a"] }
	};

	/// <summary>
	/// Verifies that <see cref="BackendOptions.FindDuplicateModelNames"/> reports exactly the client-facing names
	/// registered more than once — comparing trimmed and case-insensitively like the runtime catalog, skipping
	/// blanks, and yielding each duplicated name once in first-seen order.
	/// </summary>
	/// <param name="scenario">A human-readable label for the case.</param>
	/// <param name="names">The model names the registry is populated with.</param>
	/// <param name="expectedDuplicates">The duplicate names the helper must return, in order.</param>
	[Theory]
	[MemberData(nameof(DuplicateModelNameCases))]
	public void FindDuplicateModelNames_WhenNamesProvided_ReturnsCollidingNames(
		string   scenario,
		string[] names,
		string[] expectedDuplicates)
	{
		_ = scenario;

		// Arrange: one registry entry per name; the helper only reads Name, so other fields stay default.
		List<ModelRegistrationOptions> models =
			names.Select(name => new ModelRegistrationOptions { Name = name }).ToList();

		// Act
		List<string> duplicates = BackendOptions.FindDuplicateModelNames(models).ToList();

		// Assert: exactly the expected duplicates, in the expected order.
		Assert.Equal(expectedDuplicates, duplicates);
	}

	/// <summary>
	/// Verifies that <see cref="BackendOptions.FindDuplicateModelNames"/> guards its argument eagerly, throwing
	/// before any deferred enumeration so a <see langword="null"/> registry fails fast at the call site rather
	/// than when the result is first iterated.
	/// </summary>
	[Fact]
	public void FindDuplicateModelNames_WhenModelsNull_ThrowsArgumentNullException()
	{
		// Act + Assert: the guard runs eagerly, so no enumeration of the result is needed to trigger it.
		var ex =
			Assert.Throws<ArgumentNullException>(() => BackendOptions.FindDuplicateModelNames(null!));
		Assert.Equal("models", ex.ParamName);
	}

	// --- ListenUrl defaults and validation ---

	/// <summary>
	/// Verifies that <see cref="ProxyOptions.ListenUrl"/> defaults to the conventional Ollama port on
	/// localhost so foreground runs and fresh installs keep working without reconfiguration.
	/// </summary>
	[Fact]
	public void ListenUrl_ByDefault_IsLocalhostOllamaPort()
	{
		// Arrange + Act
		ProxyOptions options = new();

		// Assert
		Assert.Equal("http://localhost:11434", options.ListenUrl);
	}

	/// <summary>
	/// Verifies that a well-formed listener URL is accepted by the data-annotation validation rules.
	/// </summary>
	[Theory]
	[InlineData("http://localhost:11434")]
	[InlineData("http://0.0.0.0:11434")]
	[InlineData("http://127.0.0.1:8080")]
	public void Validate_WhenListenUrlWellFormed_ReturnsNoErrors(string listenUrl)
	{
		// Arrange
		ProxyOptions options = new()
		{
			ListenUrl = listenUrl,
			Backends =
			{
				["default"] = ValidBackend()
			}
		};

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		Assert.DoesNotContain(results, r => r.MemberNames.Contains(nameof(ProxyOptions.ListenUrl)));
	}

	/// <summary>
	/// Verifies that the [Url] attribute rejects a listener value that is not a valid absolute URL.
	/// </summary>
	[Theory]
	[InlineData("")]
	[InlineData("not-a-url")]
	[InlineData("localhost:11434")]
	public void Validate_WhenListenUrlMalformed_ReportsListenUrlError(string listenUrl)
	{
		// Arrange
		ProxyOptions options = new()
		{
			ListenUrl = listenUrl,
			Backends =
			{
				["default"] = ValidBackend()
			}
		};

		// Act
		List<ValidationResult> results = Validate(options);

		// Assert
		Assert.Contains(results, r => r.MemberNames.Contains(nameof(ProxyOptions.ListenUrl)));
	}
}
