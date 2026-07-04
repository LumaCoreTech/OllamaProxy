// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Admin.Editing;
using OllamaProxy.Configuration;

namespace OllamaProxy.Tests.Admin.Editing;

// Bridging the admin editor's draft and the real ProxyOptions in both directions: the forward path performs the
// two transformations the draft cannot express on its own, and the reverse path projects the live options back
// into a safe, editable draft.
//
// These tests follow the materializer's three public members, building from the per-backend unit up through the
// whole-state orchestration that composes it, then back down the reverse projection:
//
//   1. MaterializeBackend — the write-only key resolution that is the materializer's whole reason to exist.
//      A blank field means "keep the saved secret", recovered from the snapshot by OriginalName so a rename
//      cannot drop it; a non-blank field is an explicit replacement; a new or vanished backend keeps the blank
//      field for the dry-run to reject. The matrix lives in one Theory (ResolvesApiKey); a separate fact proves
//      every other backend option is carried forward untouched (PreservesOtherOptions), and the two null guards
//      close the region.
//
//   2. Materialize — the orchestration: each draft backend becomes a BackendOptions keyed by its logical name,
//      the request tracing is carried forward verbatim, and an empty draft yields an empty map. The two
//      structural guards the dry-run cannot catch — a blank name and a duplicate name (including one that
//      differs only in case) — throw here. The two null guards close the region.
//
//   3. Dematerialize — the reverse projection: each live backend becomes a draft keyed by its current name
//      (recorded as both Name and OriginalName so a later rename can still recover the secret), the API key is
//      blanked so a secret never reaches the browser (BlanksApiKey), every other option is carried forward
//      (CopiesEveryOtherOption), and every mutable member is deep-copied so editing the draft cannot mutate the
//      live snapshot (DeepCopiesProbing, DeepCopiesRegistry, DeepCopiesTracing). Order is preserved
//      (PreservesEnumerationOrder), an empty configuration yields an empty draft (WhenNoBackends), and the one
//      null guard closes the region.
//
// For the per-backend key field-copy these tests lean on, see BackendOptions.WithApiKey; for the apply path the
// materialized state flows into, see AdminModelServiceTests (ApplyDesiredStateAsync).
[Trait("Category", "Unit")]
public sealed class DesiredStateMaterializerTests
{
	private const string SavedKey = "saved-secret-key";

	#region MaterializeBackend

	/// <summary>
	/// The write-only key-resolution scenarios. Each row supplies the draft's entered key, the draft's
	/// <see cref="DesiredBackend.OriginalName"/>, and the name of the single backend in the snapshot (whose saved
	/// key is always <see cref="SavedKey"/>; <see langword="null"/> means an empty snapshot), then asserts the
	/// key the materialized backend carries. Columns: scenario, entered key, original name, snapshot name,
	/// expected key.
	/// </summary>
	public static TheoryData<string, string, string?, string?, string> ApiKeyCases => new()
	{
		// A non-blank entered key replaces the saved one outright, even when a saved key exists to keep.
		{ "entered key replaces saved", "entered-key-1234", "cloud", "cloud", "entered-key-1234" },

		// A blank key keeps the saved secret, looked up by the (unchanged) original name.
		{ "blank keeps saved, same name", "", "cloud", "cloud", SavedKey },

		// A blank key keeps the saved secret across a rename: OriginalName is the pre-rename identity, so the
		// snapshot lookup still finds it even though the draft's Name has changed.
		{ "blank keeps saved across rename", "", "old-name", "old-name", SavedKey },

		// A new backend (no OriginalName) has no saved key to keep, so the blank field stays blank for the
		// recycle's dry-run to reject as a missing required secret.
		{ "blank stays blank for new backend", "", null, "cloud", "" },

		// A blank key whose original is gone from the snapshot (deleted out from under the editor) likewise
		// has nothing to recover, so it stays blank.
		{ "blank stays blank when original vanished", "", "ghost", "cloud", "" },

		// Whitespace is treated as blank (IsNullOrWhiteSpace), so it too keeps the saved secret.
		{ "whitespace keeps saved", "   ", "cloud", "cloud", SavedKey }
	};

	/// <summary>
	/// Verifies that <see cref="DesiredStateMaterializer.MaterializeBackend"/> resolves the write-only API key:
	/// a non-blank entered key replaces, a blank key is recovered from the snapshot by
	/// <see cref="DesiredBackend.OriginalName"/>, and a new or vanished backend keeps the blank key.
	/// </summary>
	/// <param name="scenario">A human-readable description of the case under test.</param>
	/// <param name="enteredKey">The key the draft's <see cref="BackendOptions.ApiKey"/> carries.</param>
	/// <param name="originalName">
	/// The draft's <see cref="DesiredBackend.OriginalName"/>, or <see langword="null"/> for a new
	/// backend.
	/// </param>
	/// <param name="snapshotName">
	/// The name of the single backend in the snapshot, or <see langword="null"/> for an empty
	/// snapshot.
	/// </param>
	/// <param name="expectedKey">The API key the materialized backend is expected to carry.</param>
	[Theory]
	[MemberData(nameof(ApiKeyCases))]
	public void MaterializeBackend_AcrossKeyAndOriginalName_ResolvesApiKey(
		string  scenario,
		string  enteredKey,
		string? originalName,
		string? snapshotName,
		string  expectedKey)
	{
		_ = scenario;

		// Arrange: a draft carrying the entered key and original name, against a snapshot that holds one saved
		// backend (or none). The draft's Name is intentionally distinct from OriginalName so the rename row
		// proves the lookup uses OriginalName, not Name.
		DesiredBackend desired = Draft("current-name", originalName, OptionsWithKey(enteredKey));
		IDictionary<string, BackendOptions> snapshot = snapshotName is null
			                                               ? EmptySnapshot()
			                                               : SnapshotWith(snapshotName, SavedKey);

		// Act
		BackendOptions result = DesiredStateMaterializer.MaterializeBackend(desired, snapshot);

		// Assert
		Assert.Equal(expectedKey, result.ApiKey);
	}

	/// <summary>
	/// Verifies that <see cref="DesiredStateMaterializer.MaterializeBackend"/> carries every backend option other
	/// than the API key forward unchanged, sharing the draft's reference-typed members (the same shallow-copy
	/// contract as <see cref="BackendOptions.WithApiKey"/>).
	/// </summary>
	[Fact]
	public void MaterializeBackend_WhenOptionsPopulated_PreservesEveryOtherOption()
	{
		// Arrange: a fully populated draft with a non-blank key, so only the carry-forward of the other options
		// is under test.
		var options = new BackendOptions
		{
			BaseUrl = "https://api.example.com/v1",
			ProviderType = "vllm",
			ApiKey = "entered-key-1234",
			Mode = OperatingMode.Hybrid,
			ContextLength = 8192,
			ModelPrefix = "vllm",
			ReasoningEffort = ReasoningEffort.High,
			Probing = new CapabilityProbingOptions { ProbeVision = false, MaxConcurrentProbes = 8 },
			Models = { new ModelRegistrationOptions { Name = "gemma2-27b" } }
		};
		DesiredBackend desired = Draft("local", originalName: "local", options);

		// Act
		BackendOptions result = DesiredStateMaterializer.MaterializeBackend(desired, EmptySnapshot());

		// Assert: every scalar copied, and the reference-typed members shared (not cloned), per WithApiKey.
		Assert.Equal("https://api.example.com/v1", result.BaseUrl);
		Assert.Equal("vllm", result.ProviderType);
		Assert.Equal("entered-key-1234", result.ApiKey);
		Assert.Equal(OperatingMode.Hybrid, result.Mode);
		Assert.Equal(8192, result.ContextLength);
		Assert.Equal("vllm", result.ModelPrefix);
		Assert.Equal(ReasoningEffort.High, result.ReasoningEffort);
		Assert.Same(options.Probing, result.Probing);
		Assert.Same(options.Models, result.Models);
	}

	/// <summary>
	/// Verifies that <see cref="DesiredStateMaterializer.MaterializeBackend"/> rejects a <see langword="null"/>
	/// draft backend.
	/// </summary>
	[Fact]
	public void MaterializeBackend_WhenDesiredNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() =>
			DesiredStateMaterializer.MaterializeBackend(null!, EmptySnapshot()));
		Assert.Equal("desired", exception.ParamName);
	}

	/// <summary>
	/// Verifies that <see cref="DesiredStateMaterializer.MaterializeBackend"/> rejects a <see langword="null"/>
	/// snapshot.
	/// </summary>
	[Fact]
	public void MaterializeBackend_WhenCurrentBackendsNull_ThrowsArgumentNullException()
	{
		// Arrange
		DesiredBackend desired = Draft("cloud", originalName: "cloud", OptionsWithKey("entered-key-1234"));

		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() =>
			DesiredStateMaterializer.MaterializeBackend(desired, null!));
		Assert.Equal("currentBackends", exception.ParamName);
	}

	#endregion

	#region Materialize

	/// <summary>
	/// Verifies that <see cref="DesiredStateMaterializer.Materialize"/> keys each backend by its logical name and
	/// resolves each one's write-only key end to end: a blank key keeps its saved secret while an entered key
	/// replaces it, in the same pass.
	/// </summary>
	[Fact]
	public void Materialize_WhenBackendsKeyedAndKeysResolved_ProducesNamedMapWithResolvedKeys()
	{
		// Arrange: "alpha" keeps its saved key (blank field); "bravo" replaces its key (entered field).
		DesiredProxyState state = State(
			new RequestTracingOptions(),
			Draft("alpha", originalName: "alpha", OptionsWithKey("")),
			Draft("bravo", originalName: "bravo", OptionsWithKey("bravo-new-key-1")));
		IDictionary<string, BackendOptions> snapshot = SnapshotWith(("alpha", SavedKey), ("bravo", "bravo-old-key"));

		// Act
		ProxyOptions result = DesiredStateMaterializer.Materialize(state, snapshot);

		// Assert: both backends present under their names, each with the correctly resolved key.
		Assert.Equal(2, result.Backends.Count);
		Assert.Equal(SavedKey, result.Backends["alpha"].ApiKey);
		Assert.Equal("bravo-new-key-1", result.Backends["bravo"].ApiKey);
	}

	/// <summary>
	/// Verifies that <see cref="DesiredStateMaterializer.Materialize"/> carries the draft's
	/// <see cref="DesiredProxyState.RequestTracing"/> forward into the materialized
	/// <see cref="ProxyOptions.RequestTracing"/> verbatim (same instance).
	/// </summary>
	[Fact]
	public void Materialize_WhenRequestTracingProvided_CarriesItForwardVerbatim()
	{
		// Arrange: a distinctive tracing block so the carry-forward is observable by reference.
		var tracing = new RequestTracingOptions { Enabled = true, Directory = "diagnostics", MaxFiles = 42 };
		DesiredProxyState state = State(
			tracing,
			Draft("cloud", originalName: "cloud", OptionsWithKey("entered-key-1234")));

		// Act
		ProxyOptions result = DesiredStateMaterializer.Materialize(state, EmptySnapshot());

		// Assert: the exact same tracing instance flows through untouched.
		Assert.Same(tracing, result.RequestTracing);
	}

	/// <summary>
	/// Verifies that <see cref="DesiredStateMaterializer.Materialize"/> carries the draft's
	/// <see cref="DesiredProxyState.ListenUrl"/> forward into the materialized
	/// <see cref="ProxyOptions.ListenUrl"/>.
	/// </summary>
	[Fact]
	public void Materialize_WhenListenUrlProvided_CarriesItForward()
	{
		// Arrange
		DesiredProxyState state = State(
			new RequestTracingOptions(),
			listenUrl: "http://0.0.0.0:11434");

		// Act
		ProxyOptions result = DesiredStateMaterializer.Materialize(state, EmptySnapshot());

		// Assert
		Assert.Equal("http://0.0.0.0:11434", result.ListenUrl);
	}

	/// <summary>
	/// Verifies that <see cref="DesiredStateMaterializer.Materialize"/> produces an empty backend map for a draft
	/// with no backends — the degenerate case of an operator assembling a configuration from scratch. The
	/// recycle's dry-run, not the materializer, rejects a commit with no backends.
	/// </summary>
	[Fact]
	public void Materialize_WhenNoBackends_ProducesEmptyMap()
	{
		// Arrange
		DesiredProxyState state = State(new RequestTracingOptions());

		// Act
		ProxyOptions result = DesiredStateMaterializer.Materialize(state, EmptySnapshot());

		// Assert
		Assert.Empty(result.Backends);
	}

	/// <summary>
	/// Verifies that <see cref="DesiredStateMaterializer.Materialize"/> rejects a draft backend whose name is
	/// blank, since the name becomes a dictionary key that cannot be blank.
	/// </summary>
	[Fact]
	public void Materialize_WhenBackendNameBlank_ThrowsArgumentException()
	{
		// Arrange
		DesiredProxyState state = State(
			new RequestTracingOptions(),
			Draft("   ", originalName: null, OptionsWithKey("entered-key-1234")));

		// Act + Assert: the custom message is asserted by prefix so the localized "(Parameter 'state')" suffix
		// does not make the assertion fail on a non-English machine.
		var exception = Assert.Throws<ArgumentException>(() =>
			DesiredStateMaterializer.Materialize(state, EmptySnapshot()));
		Assert.Equal("state", exception.ParamName);
		Assert.StartsWith("A backend in the desired state has a blank name.", exception.Message);
	}

	/// <summary>
	/// Verifies that <see cref="DesiredStateMaterializer.Materialize"/> rejects two backends sharing a name,
	/// since the second would silently overwrite the first in the keyed map before the dry-run ever runs.
	/// </summary>
	[Fact]
	public void Materialize_WhenDuplicateNames_ThrowsArgumentException()
	{
		// Arrange
		DesiredProxyState state = State(
			new RequestTracingOptions(),
			Draft("cloud", originalName: "cloud", OptionsWithKey("first-key-12345")),
			Draft("cloud", originalName: null, OptionsWithKey("second-key-1234")));

		// Act + Assert
		var exception = Assert.Throws<ArgumentException>(() =>
			DesiredStateMaterializer.Materialize(state, SnapshotWith("cloud", SavedKey)));
		Assert.Equal("state", exception.ParamName);
		Assert.StartsWith("The desired state contains more than one backend named 'cloud'.", exception.Message);
	}

	/// <summary>
	/// Verifies that <see cref="DesiredStateMaterializer.Materialize"/> treats two names differing only in case
	/// as a duplicate, mirroring the case-insensitive keying the routing layer uses.
	/// </summary>
	[Fact]
	public void Materialize_WhenDuplicateNamesDifferOnlyInCase_ThrowsArgumentException()
	{
		// Arrange: "Cloud" and "cloud" collide under the case-insensitive comparer.
		DesiredProxyState state = State(
			new RequestTracingOptions(),
			Draft("Cloud", originalName: "Cloud", OptionsWithKey("first-key-12345")),
			Draft("cloud", originalName: null, OptionsWithKey("second-key-1234")));

		// Act + Assert: the message reports whichever casing failed the add (the second entry, "cloud").
		var exception = Assert.Throws<ArgumentException>(() =>
			DesiredStateMaterializer.Materialize(state, SnapshotWith("Cloud", SavedKey)));
		Assert.Equal("state", exception.ParamName);
		Assert.StartsWith("The desired state contains more than one backend named 'cloud'.", exception.Message);
	}

	/// <summary>
	/// Verifies that <see cref="DesiredStateMaterializer.Materialize"/> rejects a <see langword="null"/> draft
	/// state.
	/// </summary>
	[Fact]
	public void Materialize_WhenStateNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() =>
			DesiredStateMaterializer.Materialize(null!, EmptySnapshot()));
		Assert.Equal("state", exception.ParamName);
	}

	/// <summary>
	/// Verifies that <see cref="DesiredStateMaterializer.Materialize"/> rejects a <see langword="null"/>
	/// snapshot.
	/// </summary>
	[Fact]
	public void Materialize_WhenCurrentBackendsNull_ThrowsArgumentNullException()
	{
		// Arrange
		DesiredProxyState state = State(new RequestTracingOptions());

		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() =>
			DesiredStateMaterializer.Materialize(state, null!));
		Assert.Equal("currentBackends", exception.ParamName);
	}

	#endregion

	#region Dematerialize

	/// <summary>
	/// Verifies that <see cref="DesiredStateMaterializer.Dematerialize"/> keys each draft backend by its current
	/// map name, recording that name as both the editable <see cref="DesiredBackend.Name"/> and the
	/// <see cref="DesiredBackend.OriginalName"/> the commit path uses to recover the saved secret across a rename.
	/// </summary>
	[Fact]
	public void Dematerialize_WhenBackendLoaded_KeysDraftByCurrentNameAndPinsOriginalName()
	{
		// Arrange: one saved backend under a distinctive key.
		var options = new ProxyOptions
		{
			Backends = { ["cloud"] = OptionsWithKey(SavedKey) }
		};

		// Act
		DesiredProxyState draft = DesiredStateMaterializer.Dematerialize(options);

		// Assert: the single draft carries the map key as both its editable name and its pre-rename identity.
		DesiredBackend backend = Assert.Single(draft.Backends);
		Assert.Equal("cloud", backend.Name);
		Assert.Equal("cloud", backend.OriginalName);
	}

	/// <summary>
	/// Verifies that <see cref="DesiredStateMaterializer.Dematerialize"/> blanks every backend's API key so the
	/// saved secret never reaches the browser; the blank field is the editor's "keep the saved secret" sentinel.
	/// </summary>
	[Fact]
	public void Dematerialize_WhenBackendHasSavedKey_BlanksApiKey()
	{
		// Arrange: a backend carrying a real saved secret.
		var options = new ProxyOptions
		{
			Backends = { ["cloud"] = OptionsWithKey(SavedKey) }
		};

		// Act
		DesiredProxyState draft = DesiredStateMaterializer.Dematerialize(options);

		// Assert: the draft's key field is empty, not the saved secret.
		DesiredBackend backend = Assert.Single(draft.Backends);
		Assert.Equal(string.Empty, backend.Options.ApiKey);
	}

	/// <summary>
	/// Verifies that <see cref="DesiredStateMaterializer.Dematerialize"/> carries the live
	/// <see cref="ProxyOptions.ListenUrl"/> into the draft.
	/// </summary>
	[Fact]
	public void Dematerialize_WhenListenUrlSet_CopiesIt()
	{
		// Arrange
		var options = new ProxyOptions { ListenUrl = "http://127.0.0.1:49999" };

		// Act
		DesiredProxyState draft = DesiredStateMaterializer.Dematerialize(options);

		// Assert
		Assert.Equal("http://127.0.0.1:49999", draft.ListenUrl);
	}

	/// <summary>
	/// Verifies that <see cref="DesiredStateMaterializer.Dematerialize"/> carries every backend option other than
	/// the API key forward into the draft unchanged.
	/// </summary>
	[Fact]
	public void Dematerialize_WhenOptionsPopulated_CopiesEveryOtherOption()
	{
		// Arrange: a fully populated backend so every scalar carry-forward is observable.
		var saved = new BackendOptions
		{
			BaseUrl = "https://api.example.com/v1",
			ProviderType = "vllm",
			ApiKey = SavedKey,
			Mode = OperatingMode.Hybrid,
			ContextLength = 8192,
			ModelPrefix = "vllm",
			ReasoningEffort = ReasoningEffort.High
		};
		var options = new ProxyOptions { Backends = { ["local"] = saved } };

		// Act
		DesiredProxyState draft = DesiredStateMaterializer.Dematerialize(options);

		// Assert: every scalar copied verbatim (the API key is asserted blank by its own test).
		BackendOptions result = Assert.Single(draft.Backends).Options;
		Assert.Equal("https://api.example.com/v1", result.BaseUrl);
		Assert.Equal("vllm", result.ProviderType);
		Assert.Equal(OperatingMode.Hybrid, result.Mode);
		Assert.Equal(8192, result.ContextLength);
		Assert.Equal("vllm", result.ModelPrefix);
		Assert.Equal(ReasoningEffort.High, result.ReasoningEffort);
	}

	/// <summary>
	/// Verifies that <see cref="DesiredStateMaterializer.Dematerialize"/> deep-copies each backend's
	/// <see cref="BackendOptions.Probing"/> so editing a draft's probing toggles cannot mutate the live snapshot.
	/// </summary>
	[Fact]
	public void Dematerialize_WhenProbingCopied_IsIndependentOfSnapshot()
	{
		// Arrange: a backend with distinctive, non-default probing values so the copy is observable.
		BackendOptions saved = OptionsWithKey(SavedKey);
		saved.Probing.ProbeVision = false;
		saved.Probing.MaxConcurrentProbes = 9;
		var options = new ProxyOptions { Backends = { ["cloud"] = saved } };

		// Act
		DesiredProxyState draft = DesiredStateMaterializer.Dematerialize(options);
		CapabilityProbingOptions copy = Assert.Single(draft.Backends).Options.Probing;

		// Assert: the values were copied, but mutating the copy must not reach back into the live snapshot —
		// proving a fresh instance, not the shared reference the options monitor hands out.
		Assert.NotSame(saved.Probing, copy);
		Assert.False(copy.ProbeVision);
		Assert.Equal(9, copy.MaxConcurrentProbes);
		copy.MaxConcurrentProbes = 1;
		Assert.Equal(9, saved.Probing.MaxConcurrentProbes);
	}

	/// <summary>
	/// Verifies that <see cref="DesiredStateMaterializer.Dematerialize"/> deep-copies each backend's model
	/// registry — a fresh list of fresh entries — so adding, removing, or editing a draft row cannot mutate the
	/// live snapshot's registry.
	/// </summary>
	[Fact]
	public void Dematerialize_WhenRegistryCopied_IsIndependentOfSnapshot()
	{
		// Arrange: a backend with one richly populated registry entry so every field copy is observable.
		BackendOptions saved = OptionsWithKey(SavedKey);
		saved.Models.Add(
			new ModelRegistrationOptions
			{
				Name = "gemma2-27b",
				UpstreamModel = "google/gemma-2-27b",
				SupportsCompletion = true,
				SupportsTools = true,
				SupportsVision = false,
				SupportsEmbeddings = false,
				ContextLength = 8192
			});
		var options = new ProxyOptions { Backends = { ["cloud"] = saved } };

		// Act
		DesiredProxyState draft = DesiredStateMaterializer.Dematerialize(options);
		IList<ModelRegistrationOptions> copies = Assert.Single(draft.Backends).Options.Models;

		// Assert: a fresh list holding a fresh entry with every field carried forward.
		Assert.NotSame(saved.Models, copies);
		ModelRegistrationOptions copy = Assert.Single(copies);
		ModelRegistrationOptions original = saved.Models[0];
		Assert.NotSame(original, copy);
		Assert.Equal("gemma2-27b", copy.Name);
		Assert.Equal("google/gemma-2-27b", copy.UpstreamModel);
		Assert.True(copy.SupportsCompletion);
		Assert.True(copy.SupportsTools);
		Assert.False(copy.SupportsVision);
		Assert.False(copy.SupportsEmbeddings);
		Assert.Equal(8192, copy.ContextLength);

		// Editing the copied row must not reach back into the live snapshot's entry.
		copy.Name = "edited";
		Assert.Equal("gemma2-27b", original.Name);
	}

	/// <summary>
	/// Verifies that <see cref="DesiredStateMaterializer.Dematerialize"/> deep-copies the root
	/// <see cref="ProxyOptions.RequestTracing"/> so editing the draft's tracing fields cannot mutate the live
	/// snapshot.
	/// </summary>
	[Fact]
	public void Dematerialize_WhenTracingCopied_IsIndependentOfSnapshot()
	{
		// Arrange: a distinctive tracing block so every field copy is observable.
		var tracing = new RequestTracingOptions
		{
			Enabled = true,
			Directory = "diagnostics",
			MaxFiles = 42,
			MaxBodyBytes = 4096,
			RedactAttachments = false
		};
		var options = new ProxyOptions { RequestTracing = tracing };

		// Act
		DesiredProxyState draft = DesiredStateMaterializer.Dematerialize(options);

		// Assert: every field copied into a fresh instance, isolated from the live snapshot.
		Assert.NotSame(tracing, draft.RequestTracing);
		Assert.True(draft.RequestTracing.Enabled);
		Assert.Equal("diagnostics", draft.RequestTracing.Directory);
		Assert.Equal(42, draft.RequestTracing.MaxFiles);
		Assert.Equal(4096, draft.RequestTracing.MaxBodyBytes);
		Assert.False(draft.RequestTracing.RedactAttachments);
		draft.RequestTracing.MaxFiles = 1;
		Assert.Equal(42, tracing.MaxFiles);
	}

	/// <summary>
	/// Verifies that <see cref="DesiredStateMaterializer.Dematerialize"/> lists the draft backends in the
	/// snapshot's enumeration order, the order the editor renders them.
	/// </summary>
	[Fact]
	public void Dematerialize_WhenMultipleBackends_PreservesEnumerationOrder()
	{
		// Arrange: three backends inserted in a deliberate, non-alphabetical order.
		var options = new ProxyOptions
		{
			Backends =
			{
				["zeta"] = OptionsWithKey(SavedKey),
				["alpha"] = OptionsWithKey(SavedKey),
				["mid"] = OptionsWithKey(SavedKey)
			}
		};

		// Act
		DesiredProxyState draft = DesiredStateMaterializer.Dematerialize(options);

		// Assert: the draft preserves the dictionary's enumeration order rather than re-sorting.
		Assert.Equal(["zeta", "alpha", "mid"], draft.Backends.Select(backend => backend.Name));
	}

	/// <summary>
	/// Verifies that <see cref="DesiredStateMaterializer.Dematerialize"/> produces an empty draft for a
	/// configuration with no backends — the degenerate case round-tripping back to the editor.
	/// </summary>
	[Fact]
	public void Dematerialize_WhenNoBackends_ProducesEmptyDraft()
	{
		// Arrange
		var options = new ProxyOptions();

		// Act
		DesiredProxyState draft = DesiredStateMaterializer.Dematerialize(options);

		// Assert
		Assert.Empty(draft.Backends);
	}

	/// <summary>
	/// Verifies that <see cref="DesiredStateMaterializer.Dematerialize"/> rejects a <see langword="null"/> options
	/// snapshot.
	/// </summary>
	[Fact]
	public void Dematerialize_WhenOptionsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() =>
			DesiredStateMaterializer.Dematerialize(null!));
		Assert.Equal("options", exception.ParamName);
	}

	#endregion

	#region Test infrastructure

	/// <summary>
	/// Builds a draft backend with the given logical name, original name, and editable options.
	/// </summary>
	/// <param name="name">The logical (possibly renamed) backend name.</param>
	/// <param name="originalName">The pre-rename identity, or <see langword="null"/> for a new backend.</param>
	/// <param name="options">The editable backend surface, including the write-only key field.</param>
	/// <returns>The configured draft backend.</returns>
	private static DesiredBackend Draft(string name, string? originalName, BackendOptions options) =>
		new() { Name = name, OriginalName = originalName, Options = options };

	/// <summary>
	/// Builds editable backend options carrying the given write-only key field; the remaining fields are valid
	/// placeholders because the materializer only resolves the key and copies the rest verbatim.
	/// </summary>
	/// <param name="apiKey">The write-only key field value (blank means "keep the saved secret").</param>
	/// <returns>The configured options.</returns>
	private static BackendOptions OptionsWithKey(string apiKey) => new()
		{ BaseUrl = "https://x/v1", ProviderType = "openai", ApiKey = apiKey };

	/// <summary>
	/// Builds a draft state around the given request tracing and backends, in the order supplied. The listener URL
	/// defaults to a neutral test value.
	/// </summary>
	/// <param name="tracing">The root request-tracing block to carry forward.</param>
	/// <param name="backends">The draft backends, in editor order.</param>
	/// <returns>The assembled draft state.</returns>
	private static DesiredProxyState State(RequestTracingOptions tracing, params DesiredBackend[] backends) =>
		State(tracing, "http://127.0.0.1:49999", backends);

	/// <summary>
	/// Builds a draft state around the given request tracing, listener URL, and backends, in the order supplied.
	/// </summary>
	/// <param name="tracing">The root request-tracing block to carry forward.</param>
	/// <param name="listenUrl">The listener URL to carry forward.</param>
	/// <param name="backends">The draft backends, in editor order.</param>
	/// <returns>The assembled draft state.</returns>
	private static DesiredProxyState State(
		RequestTracingOptions   tracing,
		string                  listenUrl,
		params DesiredBackend[] backends) => new()
	{
		ListenUrl = listenUrl,
		Backends = [.. backends],
		RequestTracing = tracing
	};

	/// <summary>
	/// Builds an empty configuration snapshot (no saved backends), for the new-backend and no-recovery cases.
	/// </summary>
	/// <returns>An empty, case-insensitive backend map.</returns>
	private static Dictionary<string, BackendOptions> EmptySnapshot() => new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Builds a single-entry configuration snapshot mapping <paramref name="name"/> to a backend carrying
	/// <paramref name="savedKey"/> as its saved secret.
	/// </summary>
	/// <param name="name">The saved backend's logical name.</param>
	/// <param name="savedKey">The saved API key the snapshot backend carries.</param>
	/// <returns>The single-entry, case-insensitive backend map.</returns>
	private static Dictionary<string, BackendOptions> SnapshotWith(string name, string savedKey) =>
		new(StringComparer.OrdinalIgnoreCase) { [name] = OptionsWithKey(savedKey) };

	/// <summary>
	/// Builds a configuration snapshot from the given (name, saved key) pairs.
	/// </summary>
	/// <param name="entries">The saved backends as (name, saved key) pairs.</param>
	/// <returns>The case-insensitive backend map.</returns>
	private static Dictionary<string, BackendOptions> SnapshotWith(params (string Name, string SavedKey)[] entries)
	{
		var snapshot = new Dictionary<string, BackendOptions>(StringComparer.OrdinalIgnoreCase);
		foreach ((string name, string savedKey) in entries)
		{
			snapshot[name] = OptionsWithKey(savedKey);
		}

		return snapshot;
	}

	#endregion
}
