// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Runtime.CompilerServices;

using Microsoft.Extensions.Options;

using OllamaProxy.Admin;
using OllamaProxy.Admin.Config;
using OllamaProxy.Admin.Editing;
using OllamaProxy.Admin.Fetch;
using OllamaProxy.Configuration;
using OllamaProxy.Core;
using OllamaProxy.Providers.Abstractions;
using OllamaProxy.Tests.Admin.Editing;
using OllamaProxy.Tests.Admin.Fetch;
using OllamaProxy.Tests.Admin.Reconciliation;

namespace OllamaProxy.Tests.Admin;

/// <summary>
/// Tests for <see cref="AdminModelService"/>: preview an uncommitted draft backend, load the live configuration
/// as an editable draft, and apply a complete edited configuration.
/// </summary>
/// <remarks>
/// These tests follow the three members of <see cref="IAdminModelService"/>:
/// <list type="number">
///     <item>
///         <description>
///         <c>FetchDraftSnapshotAsync</c> is the editor's model-list data source for an uncommitted backend: it
///         fetches the raw, unreconciled snapshot against the draft's own (unsaved) settings so the editor can
///         reconcile it locally and re-reconcile on every pin/unpin/mode change without another round-trip. The
///         caller picks the probe policy — NeverProbe for a refresh, ProbeAll for an on-demand capability probe.
///         It materializes the draft and returns its raw candidates unreconciled (WhenDraftSucceeds), passing the
///         caller's probe policy through to the fetcher (WhenProbing), recovering the draft's blank API key from
///         the snapshot by OriginalName so the fetch authenticates with the saved secret (WhenDraftKeyBlank); a
///         fetch failure is captured as a failure snapshot rather than thrown (WhenDraftFetchFails); an unnamed
///         draft still fetches under a fallback label (WhenDraftUnnamed); a null draft is rejected (WhenDraftNull).
///         </description>
///     </item>
///     <item>
///         <description>
///         <c>GetEditableStateAsync</c> is the load counterpart to <c>ApplyDesiredStateAsync</c>: it projects the
///         live snapshot into an editable draft for the editor to bind to, never handing back a secret. It reads
///         the snapshot and dematerializes it into a draft that mirrors the configuration with each API key
///         blanked (WhenLoading); a cancelled token is honoured before any work (WhenCancelledThroughToken).
///         </description>
///     </item>
///     <item>
///         <description>
///         <c>ApplyDesiredStateAsync</c> materializes the whole editor draft into an authoritative desired state
///         and hands it to the applier, never mutating the snapshot. It keys the backends by name with their
///         write-only keys resolved and carries the draft's request tracing forward (WhenApplying), returns the
///         applier's outcome and the chosen policy verbatim (WhenApplierRejects), propagates the materializer's
///         duplicate-name guard (WhenDuplicateNames), and rejects a null state before touching the applier
///         (WhenStateNull).
///         </description>
///     </item>
/// </list>
/// Constructor guards (the three null collaborators) close the file. For the per-backend fetch and its failure
/// classification, see <see cref="BackendModelFetcherTests"/>; for the pure pin-vs-snapshot merge, see
/// <see cref="ModelReconcilerTests"/>; for the draft-to-ProxyOptions materialization in isolation, see
/// <see cref="DesiredStateMaterializerTests"/>.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class AdminModelServiceTests
{
	#region FetchDraftSnapshotAsync

	// --- 1. Fetch an uncommitted draft's raw model snapshot for local reconciliation ---

	/// <summary>
	/// Verifies that <see cref="AdminModelService.FetchDraftSnapshotAsync"/> fetches against the draft's own
	/// settings and returns the backend's <em>raw, unreconciled</em> candidates verbatim and in order, so the
	/// editor can reconcile them locally; the snapshot deliberately carries no reconciliation, mode, or pins.
	/// </summary>
	[Fact]
	public async Task FetchDraftSnapshotAsync_WhenDraftSucceeds_ReturnsRawSnapshot()
	{
		// Arrange: a draft named "cloud" whose fetch returns two candidates. The draft carries a pin, but the
		// snapshot is returned unreconciled, so the pin must not collapse a1 into an Available row here — both
		// candidates pass through as-is.
		DesiredBackend draft = Draft(
			"cloud",
			originalName: null,
			Backend(OperatingMode.Hybrid, Pin("alpha", upstream: "a1")));
		FakeFetcher fetcher = FetcherReturning(Ok("cloud", Candidate("a1"), Candidate("b1")));
		AdminModelService sut = Service(new CountingOptionsMonitor(OptionsWith(BackendsNamed())), fetcher);

		// Act
		DraftModelSnapshot snapshot =
			await sut.FetchDraftSnapshotAsync(draft, DiscoveryProbePolicy.NeverProbe, CancellationToken.None);

		// Assert: the success snapshot carries the raw candidates verbatim, in the backend's reported order, with
		// no error fields set.
		Assert.True(snapshot.Succeeded);
		Assert.Null(snapshot.ErrorKind);
		Assert.Null(snapshot.ErrorMessage);
		Assert.NotNull(snapshot.Snapshot);
		Assert.Collection(
			snapshot.Snapshot,
			candidate => Assert.Equal("a1", candidate.UpstreamModel),
			candidate => Assert.Equal("b1", candidate.UpstreamModel));

		// Negative check: a refresh never probes.
		Assert.Equal(DiscoveryProbePolicy.NeverProbe, fetcher.LastProbePolicyByBackend["cloud"]);
	}

	/// <summary>
	/// Verifies that <see cref="AdminModelService.FetchDraftSnapshotAsync"/> passes the caller's
	/// <see cref="DiscoveryProbePolicy.ProbeAll"/> through to the fetcher, so an on-demand capability probe
	/// against the draft actually probes — the path a capability-poor backend needs to resolve its capabilities.
	/// </summary>
	[Fact]
	public async Task FetchDraftSnapshotAsync_WhenProbing_FetchesWithProbeAll()
	{
		// Arrange
		DesiredBackend draft = Draft("cloud", originalName: null, Backend(OperatingMode.Hybrid));
		FakeFetcher fetcher = FetcherReturning(Ok("cloud", Candidate("c1")));
		AdminModelService sut = Service(new CountingOptionsMonitor(OptionsWith(BackendsNamed())), fetcher);

		// Act
		_ = await sut.FetchDraftSnapshotAsync(draft, DiscoveryProbePolicy.ProbeAll, CancellationToken.None);

		// Assert: the draft was fetched under ProbeAll, not the fast no-probe default.
		DiscoveryProbePolicy policy = Assert.Single(fetcher.LastProbePolicyByBackend).Value;
		Assert.Equal(DiscoveryProbePolicy.ProbeAll, policy);
	}

	/// <summary>
	/// Verifies that <see cref="AdminModelService.FetchDraftSnapshotAsync"/> recovers a draft's blank
	/// (write-only) API key from the live snapshot by its <see cref="DesiredBackend.OriginalName"/>, so the
	/// fetch uses the saved secret the editor never received.
	/// </summary>
	[Fact]
	public async Task FetchDraftSnapshotAsync_WhenDraftKeyBlank_FetchesWithRecoveredKey()
	{
		// Arrange: the snapshot holds the saved backend "cloud" with a real key; the draft renames it to "renamed"
		// but leaves the key blank, so the saved secret must be recovered by OriginalName ("cloud"), not Name.
		BackendOptions saved = Backend(OperatingMode.Hybrid);
		saved.ApiKey = "saved-secret-key";
		ProxyOptions options = OptionsWith(OneBackend("cloud", saved));

		BackendOptions edited = Backend(OperatingMode.Hybrid);
		edited.ApiKey = ""; // blank means "keep the saved secret"
		DesiredBackend draft = Draft("renamed", originalName: "cloud", edited);
		FakeFetcher fetcher = FetcherReturning(Ok("renamed"));
		AdminModelService sut = Service(new CountingOptionsMonitor(options), fetcher);

		// Act
		_ = await sut.FetchDraftSnapshotAsync(draft, DiscoveryProbePolicy.NeverProbe, CancellationToken.None);

		// Assert: the backend handed to the fetcher carried the recovered saved key, not the blank draft key.
		BackendOptions fetched = Assert.Single(fetcher.FetchedBackends);
		Assert.Equal("saved-secret-key", fetched.ApiKey);
	}

	/// <summary>
	/// Verifies that a draft whose fetch fails is captured as a failure snapshot — carrying the classified error
	/// and message with a null snapshot — rather than throwing, so the editor can render the failure inline.
	/// </summary>
	[Fact]
	public async Task FetchDraftSnapshotAsync_WhenDraftFetchFails_ReturnsFailureSnapshot()
	{
		// Arrange: the fetch for the draft returns an authentication failure (e.g. the entered key is wrong).
		DesiredBackend draft = Draft("cloud", originalName: null, Backend(OperatingMode.Hybrid));
		FakeFetcher fetcher = FetcherReturning(Fail("cloud", BackendFetchErrorKind.Authentication, "bad key"));
		AdminModelService sut = Service(new CountingOptionsMonitor(OptionsWith(BackendsNamed())), fetcher);

		// Act
		DraftModelSnapshot snapshot =
			await sut.FetchDraftSnapshotAsync(draft, DiscoveryProbePolicy.NeverProbe, CancellationToken.None);

		// Assert: the failure is captured verbatim with no snapshot payload.
		Assert.False(snapshot.Succeeded);
		Assert.Null(snapshot.Snapshot);
		Assert.Equal(BackendFetchErrorKind.Authentication, snapshot.ErrorKind);
		Assert.Equal("bad key", snapshot.ErrorMessage);
	}

	/// <summary>
	/// Verifies that an unnamed draft (no <see cref="DesiredBackend.Name"/> and no
	/// <see cref="DesiredBackend.OriginalName"/>) still fetches, under the fallback placeholder label, so the
	/// editor can list a backend's models before it is named.
	/// </summary>
	[Fact]
	public async Task FetchDraftSnapshotAsync_WhenDraftUnnamed_FetchesUnderFallbackName()
	{
		// Arrange: a draft with a blank name and no original identity; the fetcher must still be invoked under a
		// non-blank fallback label so its name guard is satisfied. The label never reaches the rendered rows — the
		// editor reconciles the returned snapshot against the draft — so this only proves the fetch happened.
		DesiredBackend draft = Draft("", originalName: null, Backend(OperatingMode.Hybrid));
		FakeFetcher fetcher = FetcherReturning(Ok("(unnamed backend)"));
		AdminModelService sut = Service(new CountingOptionsMonitor(OptionsWith(BackendsNamed())), fetcher);

		// Act
		DraftModelSnapshot snapshot =
			await sut.FetchDraftSnapshotAsync(draft, DiscoveryProbePolicy.NeverProbe, CancellationToken.None);

		// Assert: the fetch happened under the placeholder label and succeeded.
		Assert.True(snapshot.Succeeded);
		string fetchedName = Assert.Single(fetcher.FetchedBackendNames);
		Assert.Equal("(unnamed backend)", fetchedName);
	}

	/// <summary>
	/// Verifies that <see cref="AdminModelService.FetchDraftSnapshotAsync"/> rejects a <see langword="null"/>
	/// draft before touching the configuration or fetcher.
	/// </summary>
	[Fact]
	public async Task FetchDraftSnapshotAsync_WhenDraftNull_ThrowsArgumentNullException()
	{
		// Arrange
		FakeFetcher fetcher = FetcherReturning();
		AdminModelService sut = Service(new CountingOptionsMonitor(OptionsWith(BackendsNamed())), fetcher);

		// Act + Assert
		var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
			                sut.FetchDraftSnapshotAsync(
				                null!,
				                DiscoveryProbePolicy.NeverProbe,
				                CancellationToken.None));
		Assert.Equal("draft", exception.ParamName);

		// Negative check: the guard fired before any fetch was attempted.
		Assert.Empty(fetcher.FetchedBackendNames);
	}

	#endregion

	#region GetEditableStateAsync

	// --- 2. Load the whole configuration as an editable draft ---

	/// <summary>
	/// Verifies that <see cref="AdminModelService.GetEditableStateAsync"/> projects the live snapshot into an
	/// editable draft that mirrors the configuration — one draft per backend keyed by its current name — with
	/// every API key blanked so the saved secret never reaches the browser.
	/// </summary>
	[Fact]
	public async Task GetEditableStateAsync_WhenLoading_ProjectsSnapshotIntoDraftWithBlankedKeys()
	{
		// Arrange: two backends carrying real saved secrets and a distinctive tracing block.
		BackendOptions cloud = Backend(OperatingMode.Hybrid);
		cloud.ApiKey = "cloud-saved-secret";
		BackendOptions local = Backend(OperatingMode.Explicit);
		local.ApiKey = "local-saved-secret";
		var backends = new Dictionary<string, BackendOptions>(StringComparer.OrdinalIgnoreCase)
		{
			["cloud"] = cloud,
			["local"] = local
		};
		var options = new ProxyOptions
		{
			Backends = backends,
			RequestTracing = new RequestTracingOptions { Enabled = true, Directory = "diagnostics", MaxFiles = 42 }
		};
		AdminModelService sut = Service(new CountingOptionsMonitor(options), FetcherReturning());

		// Act
		DesiredProxyState draft = await sut.GetEditableStateAsync(CancellationToken.None);

		// Assert: one draft per backend, each keyed by its current name as both editable name and rename anchor,
		// with the saved secret blanked; the tracing is carried into the draft, deep-copied (a fresh instance).
		Assert.Equal(2, draft.Backends.Count);
		Assert.Equal(["cloud", "local"], draft.Backends.Select(backend => backend.Name));
		Assert.Equal(["cloud", "local"], draft.Backends.Select(backend => backend.OriginalName));
		Assert.All(draft.Backends, backend => Assert.Equal(string.Empty, backend.Options.ApiKey));
		Assert.NotSame(options.RequestTracing, draft.RequestTracing);
		Assert.True(draft.RequestTracing.Enabled);
		Assert.Equal("diagnostics", draft.RequestTracing.Directory);
		Assert.Equal(42, draft.RequestTracing.MaxFiles);
	}

	/// <summary>
	/// Verifies that <see cref="AdminModelService.GetEditableStateAsync"/> honours a cancelled token before doing
	/// any work, surfacing an <see cref="OperationCanceledException"/> rather than returning a draft.
	/// </summary>
	[Fact]
	public async Task GetEditableStateAsync_WhenCancelledThroughToken_PropagatesCancellation()
	{
		// Arrange: an already-cancelled token; the snapshot content is irrelevant because the guard fires first.
		using var cts = new CancellationTokenSource();
		cts.Cancel();
		ProxyOptions options = OptionsWith(OneBackend("cloud", Backend(OperatingMode.Hybrid)));
		AdminModelService sut = Service(new CountingOptionsMonitor(options), FetcherReturning());

		// Act + Assert
		await Assert.ThrowsAsync<OperationCanceledException>(() => sut.GetEditableStateAsync(cts.Token));
	}

	#endregion

	#region ApplyDesiredStateAsync

	// --- 3. Apply the whole edited configuration ---

	/// <summary>
	/// Verifies that applying a desired state hands the applier a fresh backend map keyed by each draft's name,
	/// with write-only keys resolved against the snapshot and the draft's request tracing carried forward, so a
	/// whole-section write reflects the complete edited configuration.
	/// </summary>
	[Fact]
	public async Task ApplyDesiredStateAsync_WhenApplying_MaterializesAndCarriesTracingForward()
	{
		// Arrange: the snapshot holds "cloud" with a saved key; the draft keeps that key blank (must be recovered)
		// and adds a second backend "local" with an entered key. A distinctive tracing block must survive.
		BackendOptions saved = Backend(OperatingMode.Hybrid);
		saved.ApiKey = "saved-secret-key";
		ProxyOptions options = OptionsWith(OneBackend("cloud", saved));

		BackendOptions cloudEdit = Backend(OperatingMode.Hybrid);
		cloudEdit.ApiKey = ""; // keep the saved secret
		BackendOptions localNew = Backend(OperatingMode.Explicit);
		localNew.ApiKey = "local-entered-key";

		var tracing = new RequestTracingOptions { Enabled = true, Directory = "diagnostics", MaxFiles = 42 };
		DesiredProxyState state = State(
			tracing,
			Draft("cloud", originalName: "cloud", cloudEdit),
			Draft("local", originalName: null, localNew));
		var applier = new FakeApplier(ApplyResult.Applied);
		AdminModelService sut = Service(new CountingOptionsMonitor(options), FetcherReturning(), applier);

		// Act
		ApplyResult result = await sut.ApplyDesiredStateAsync(
			                     state,
			                     CancellationToken.None);

		// Assert: both backends keyed by name with resolved keys, and the tracing carried forward by reference.
		Assert.True(result.Success);
		ProxyOptions desired = applier.LastDesiredState!;
		Assert.Equal(2, desired.Backends.Count);
		Assert.Equal("saved-secret-key", desired.Backends["cloud"].ApiKey);
		Assert.Equal("local-entered-key", desired.Backends["local"].ApiKey);
		Assert.Same(tracing, desired.RequestTracing);
	}

	/// <summary>
	/// Verifies that the applier's outcome is returned verbatim so a rejected recycle surfaces to the caller
	/// unchanged rather than being reinterpreted by the service.
	/// </summary>
	[Fact]
	public async Task ApplyDesiredStateAsync_WhenApplierRejects_ReturnsRejectionVerbatim()
	{
		// Arrange: the applier rejects with a validation error; the service must surface the rejection unchanged.
		ProxyOptions options = OptionsWith(OneBackend("cloud", Backend(OperatingMode.Hybrid)));
		var applier = new FakeApplier(ApplyResult.ValidationRejected(["boom"]));
		AdminModelService sut = Service(new CountingOptionsMonitor(options), FetcherReturning(), applier);
		BackendOptions edited = Backend(OperatingMode.Hybrid);
		edited.ApiKey = "entered-key-1234";
		DesiredProxyState state = State(new RequestTracingOptions(), Draft("cloud", originalName: "cloud", edited));

		// Act
		ApplyResult result = await sut.ApplyDesiredStateAsync(
			                     state,
			                     CancellationToken.None);

		// Assert: the rejection and its errors are returned verbatim.
		Assert.False(result.Success);
		Assert.Equal(ApplyOutcome.ValidationRejected, result.Outcome);
		Assert.Equal(["boom"], result.Errors);
	}

	/// <summary>
	/// Verifies that <see cref="AdminModelService.ApplyDesiredStateAsync"/> propagates the materializer's
	/// duplicate-name guard — two drafts sharing a name throw before the applier is ever called, since the
	/// recycle's dry-run could not catch a name that silently collides as a map key.
	/// </summary>
	[Fact]
	public async Task ApplyDesiredStateAsync_WhenDuplicateNames_ThrowsArgumentException()
	{
		// Arrange: two drafts both named "cloud".
		ProxyOptions options = OptionsWith(OneBackend("cloud", Backend(OperatingMode.Hybrid)));
		var applier = new FakeApplier(ApplyResult.Applied);
		AdminModelService sut = Service(new CountingOptionsMonitor(options), FetcherReturning(), applier);
		BackendOptions first = Backend(OperatingMode.Hybrid);
		first.ApiKey = "first-key-12345";
		BackendOptions second = Backend(OperatingMode.Hybrid);
		second.ApiKey = "second-key-1234";
		DesiredProxyState state = State(
			new RequestTracingOptions(),
			Draft("cloud", originalName: "cloud", first),
			Draft("cloud", originalName: null, second));

		// Act + Assert
		var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
			                sut.ApplyDesiredStateAsync(
				                state,
				                CancellationToken.None));
		Assert.Equal("state", exception.ParamName);
		AssertCustomArgumentMessage("The desired state contains more than one backend named 'cloud'.", exception);

		// Negative check: the guard fired before any apply was attempted.
		Assert.Equal(0, applier.ApplyCallCount);
	}

	/// <summary>
	/// Verifies that <see cref="AdminModelService.ApplyDesiredStateAsync"/> rejects a <see langword="null"/>
	/// desired state before touching the configuration.
	/// </summary>
	[Fact]
	public async Task ApplyDesiredStateAsync_WhenStateNull_ThrowsArgumentNullException()
	{
		// Arrange
		ProxyOptions options = OptionsWith(OneBackend("cloud", Backend(OperatingMode.Hybrid)));
		var applier = new FakeApplier(ApplyResult.Applied);
		AdminModelService sut = Service(new CountingOptionsMonitor(options), FetcherReturning(), applier);

		// Act + Assert
		var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
			                sut.ApplyDesiredStateAsync(
				                null!,
				                CancellationToken.None));
		Assert.Equal("desiredState", exception.ParamName);

		// Negative check: the guard fired before any apply was attempted.
		Assert.Equal(0, applier.ApplyCallCount);
	}

	#endregion

	#region Constructor

	/// <summary>
	/// Verifies that the constructor rejects a <see langword="null"/> options monitor.
	/// </summary>
	[Fact]
	public void Constructor_WhenOptionsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() =>
			new AdminModelService(null!, FetcherReturning(), new FakeApplier(ApplyResult.Applied)));
		Assert.Equal("options", exception.ParamName);
	}

	/// <summary>
	/// Verifies that the constructor rejects a <see langword="null"/> fetcher.
	/// </summary>
	[Fact]
	public void Constructor_WhenFetcherNull_ThrowsArgumentNullException()
	{
		// Arrange
		var monitor = new CountingOptionsMonitor(OptionsWith(BackendsNamed("cloud")));

		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() =>
			new AdminModelService(monitor, null!, new FakeApplier(ApplyResult.Applied)));
		Assert.Equal("fetcher", exception.ParamName);
	}

	/// <summary>
	/// Verifies that the constructor rejects a <see langword="null"/> applier.
	/// </summary>
	[Fact]
	public void Constructor_WhenApplierNull_ThrowsArgumentNullException()
	{
		// Arrange
		var monitor = new CountingOptionsMonitor(OptionsWith(BackendsNamed("cloud")));

		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() =>
			new AdminModelService(monitor, FetcherReturning(), null!));
		Assert.Equal("applier", exception.ParamName);
	}

	#endregion

	#region Test infrastructure

	/// <summary>
	/// Builds an <see cref="AdminModelService"/> with a no-op applier, for the fetch and load tests that never
	/// apply changes; routes through the three-argument overload so the applier dependency lives in one place.
	/// </summary>
	/// <param name="options">The options monitor supplying the snapshot.</param>
	/// <param name="fetcher">The fetcher double.</param>
	/// <returns>The configured service.</returns>
	private static AdminModelService Service(IOptionsMonitor<ProxyOptions> options, FakeFetcher fetcher) =>
		Service(options, fetcher, new FakeApplier(ApplyResult.Applied));

	/// <summary>
	/// Builds an <see cref="AdminModelService"/> with an explicit applier, for the apply tests that assert on the
	/// desired state handed to it.
	/// </summary>
	/// <param name="options">The options monitor supplying the snapshot.</param>
	/// <param name="fetcher">The fetcher double.</param>
	/// <param name="applier">The applier double.</param>
	/// <returns>The configured service.</returns>
	private static AdminModelService Service(
		IOptionsMonitor<ProxyOptions> options,
		FakeFetcher                   fetcher,
		FakeApplier                   applier) => new(options, fetcher, applier);

	/// <summary>
	/// Builds proxy options around the given backends; no validation is run, mirroring the tolerantly bound
	/// snapshot the chassis admin surface loads. Each backend owns its mode and registry, so there is no
	/// section-level mode or registry to supply.
	/// </summary>
	/// <param name="backends">The configured backends keyed by logical name.</param>
	/// <returns>The assembled proxy options.</returns>
	private static ProxyOptions OptionsWith(Dictionary<string, BackendOptions> backends) =>
		new() { Backends = backends };

	/// <summary>
	/// Builds a single-entry backend dictionary mapping <paramref name="name"/> to <paramref name="backend"/>,
	/// for the common case of a test that configures exactly one backend with specific options.
	/// </summary>
	/// <param name="name">The logical backend name.</param>
	/// <param name="backend">The backend options to map.</param>
	/// <returns>The single-entry backend dictionary.</returns>
	private static Dictionary<string, BackendOptions> OneBackend(string name, BackendOptions backend) =>
		new(StringComparer.OrdinalIgnoreCase) { [name] = backend };

	/// <summary>
	/// Builds a backend dictionary from the given names, all sharing placeholder options; used where the backend's
	/// own fields are irrelevant because the fetcher returns canned results keyed by name.
	/// </summary>
	/// <param name="names">The logical backend names, in the order they should enumerate.</param>
	/// <returns>The backend dictionary, ordered by insertion.</returns>
	private static Dictionary<string, BackendOptions> BackendsNamed(params string[] names)
	{
		var backends = new Dictionary<string, BackendOptions>(StringComparer.OrdinalIgnoreCase);
		foreach (string name in names)
		{
			backends[name] = Backend();
		}

		return backends;
	}

	/// <summary>
	/// Builds placeholder backend options with an optional mode and registry pins; URL and key are placeholders
	/// because the fetcher is faked and never opens a connection. The mode defaults to
	/// <see langword="null"/> so the backend resolves the provider-aware default, matching a bound snapshot that
	/// did not declare one.
	/// </summary>
	/// <param name="mode">The operating mode the backend declares, or <see langword="null"/> for the default.</param>
	/// <param name="pins">The registry pins to add, in order.</param>
	/// <returns>The configured backend options.</returns>
	private static BackendOptions Backend(OperatingMode? mode = null, params ModelRegistrationOptions[] pins)
	{
		var backend = new BackendOptions
		{
			BaseUrl = "https://x/v1",
			ProviderType = "openai",
			ApiKey = "placeholder-key",
			Mode = mode
		};

		foreach (ModelRegistrationOptions pin in pins)
		{
			backend.Models.Add(pin);
		}

		return backend;
	}

	/// <summary>
	/// Builds a registry pin for the given client-facing name, with an optional upstream alias. The backend is
	/// implied by the entry's position in its owning <see cref="BackendOptions.Models"/> list.
	/// </summary>
	/// <param name="name">The client-facing model name.</param>
	/// <param name="upstream">The optional upstream alias; when omitted the name is used upstream.</param>
	/// <returns>The configured registry entry.</returns>
	private static ModelRegistrationOptions Pin(string name, string? upstream = null) =>
		new() { Name = name, UpstreamModel = upstream };

	/// <summary>
	/// Builds an editor draft backend with the given logical name, original name, and editable options, for the
	/// preview and apply tests.
	/// </summary>
	/// <param name="name">The logical (possibly renamed) backend name.</param>
	/// <param name="originalName">The pre-rename identity, or <see langword="null"/> for a new backend.</param>
	/// <param name="options">The editable backend surface, including the write-only key field.</param>
	/// <returns>The configured draft backend.</returns>
	private static DesiredBackend Draft(string name, string? originalName, BackendOptions options) =>
		new() { Name = name, OriginalName = originalName, Options = options };

	/// <summary>
	/// Builds an editor draft state around the given request tracing and backends, in the order supplied.
	/// </summary>
	/// <param name="tracing">The root request-tracing block to carry forward.</param>
	/// <param name="backends">The draft backends, in editor order.</param>
	/// <returns>The assembled draft state.</returns>
	private static DesiredProxyState State(RequestTracingOptions tracing, params DesiredBackend[] backends) =>
		new() { Backends = [.. backends], RequestTracing = tracing };

	/// <summary>
	/// Builds a resolved discovery candidate with the given upstream id and optional client-facing name; context
	/// and capabilities are concrete placeholders the reconciliation passes through verbatim.
	/// </summary>
	/// <param name="upstream">The upstream model identifier.</param>
	/// <param name="clientName">The optional client-facing name; when omitted, <paramref name="upstream"/> is used.</param>
	/// <returns>The discovery candidate.</returns>
	private static DiscoveryCandidate Candidate(string upstream, string? clientName = null) => new(
		clientName ?? upstream,
		upstream,
		ReportedContextLength: null,
		ModelCapabilities.CompletionOnly);

	/// <summary>
	/// Builds a successful fetch result for the given backend carrying the supplied models.
	/// </summary>
	/// <param name="backendName">The backend the result describes.</param>
	/// <param name="models">The resolved models the fetch returned.</param>
	/// <returns>The successful fetch result.</returns>
	private static BackendFetchResult Ok(string backendName, params DiscoveryCandidate[] models) =>
		BackendFetchResult.Success(backendName, models);

	/// <summary>
	/// Builds a failed fetch result for the given backend with the classified error and message.
	/// </summary>
	/// <param name="backendName">The backend the result describes.</param>
	/// <param name="errorKind">How far the failure could be attributed.</param>
	/// <param name="errorMessage">The human-readable failure description.</param>
	/// <returns>The failed fetch result.</returns>
	private static BackendFetchResult Fail(string backendName, BackendFetchErrorKind errorKind, string errorMessage) =>
		BackendFetchResult.Failure(backendName, errorKind, errorMessage);

	/// <summary>
	/// Builds a fetcher that returns the supplied canned results, keyed by their backend name.
	/// </summary>
	/// <param name="results">The canned results, one per backend the test configures.</param>
	/// <returns>The fetcher double.</returns>
	private static FakeFetcher FetcherReturning(params BackendFetchResult[] results) => new(
		results.ToDictionary(result => result.BackendName, StringComparer.OrdinalIgnoreCase));

	/// <summary>
	/// Asserts the exact custom part of an <see cref="ArgumentException.Message"/> without asserting the localized
	/// framework-added parameter suffix.
	/// </summary>
	/// <param name="expectedMessage">The custom production message expected at the start of the exception text.</param>
	/// <param name="exception">The exception whose message should carry the custom production text.</param>
	private static void AssertCustomArgumentMessage(string expectedMessage, ArgumentException exception)
	{
		string actualMessage = exception.Message.Length >= expectedMessage.Length
			                       ? exception.Message[..expectedMessage.Length]
			                       : exception.Message;

		Assert.Equal(expectedMessage, actualMessage);
	}

	/// <summary>
	/// A fetcher test double that returns canned results keyed by backend name and records the backends it was
	/// asked to fetch, so a draft-preview test can prove what reached the fetch.
	/// </summary>
	/// <param name="results">The canned results keyed by backend name.</param>
	private sealed class FakeFetcher(IReadOnlyDictionary<string, BackendFetchResult> results) : IBackendModelFetcher
	{
		/// <summary>
		/// Gets the backend names the fetcher was asked to fetch, in call order. The canned results complete
		/// synchronously, so the service's fan-out runs sequentially and a plain list is safe.
		/// </summary>
		public List<string> FetchedBackendNames { get; } = [];

		/// <summary>
		/// Gets the backend options the fetcher was handed, in call order, so a draft-preview test can prove the
		/// materialized backend (most importantly its resolved write-only key) reached the fetch.
		/// </summary>
		public List<BackendOptions> FetchedBackends { get; } = [];

		/// <summary>
		/// Gets the probe policy the fetcher was last asked to use, keyed by backend name, so a test can prove the
		/// draft refresh requests <see cref="DiscoveryProbePolicy.NeverProbe"/> and a probe requests
		/// <see cref="DiscoveryProbePolicy.ProbeAll"/>.
		/// </summary>
		public Dictionary<string, DiscoveryProbePolicy> LastProbePolicyByBackend { get; } =
			new(StringComparer.OrdinalIgnoreCase);

		/// <inheritdoc/>
		public Task<BackendFetchResult> FetchAsync(
			string               backendName,
			BackendOptions       backend,
			DiscoveryProbePolicy probePolicy,
			CancellationToken    cancellationToken)
		{
			FetchedBackendNames.Add(backendName);
			FetchedBackends.Add(backend);
			LastProbePolicyByBackend[backendName] = probePolicy;

			if (!results.TryGetValue(backendName, out BackendFetchResult? result))
			{
				throw new InvalidOperationException($"No canned fetch result configured for backend '{backendName}'.");
			}

			return Task.FromResult(result);
		}

		/// <inheritdoc/>
		public async IAsyncEnumerable<DiscoveryCandidate> FetchStreamingAsync(
			string                                     backendName,
			BackendOptions                             backend,
			DiscoveryProbePolicy                       probePolicy,
			[EnumeratorCancellation] CancellationToken cancellationToken)
		{
			FetchedBackendNames.Add(backendName);
			FetchedBackends.Add(backend);
			LastProbePolicyByBackend[backendName] = probePolicy;

			if (!results.TryGetValue(backendName, out BackendFetchResult? result))
			{
				throw new InvalidOperationException($"No canned fetch result configured for backend '{backendName}'.");
			}

			// A failed canned result models the streaming failure contract: it throws a classified
			// BackendFetchException rather than yielding, exactly as the real fetcher does mid-stream.
			if (!result.Succeeded)
			{
				throw new BackendFetchException(
					result.ErrorKind!.Value,
					result.ErrorMessage!,
					innerException: null);
			}

			await Task.CompletedTask.ConfigureAwait(false);

			foreach (DiscoveryCandidate candidate in result.Models!)
			{
				cancellationToken.ThrowIfCancellationRequested();
				yield return candidate;
			}
		}
	}

	/// <summary>
	/// An applier test double that returns a canned <see cref="ApplyResult"/> and records the desired state and
	/// how often it was called, so an apply test can assert on the whole-section state the service built without
	/// running a real write-and-recycle.
	/// </summary>
	/// <param name="result">The canned result every <see cref="ApplyAsync"/> call returns.</param>
	private sealed class FakeApplier(ApplyResult result) : IProxyConfigApplier
	{
		/// <summary>Gets the desired state handed to the most recent apply, or <see langword="null"/> before the first.</summary>
		public ProxyOptions? LastDesiredState { get; private set; }

		/// <summary>Gets the number of times <see cref="ApplyAsync"/> was called.</summary>
		public int ApplyCallCount { get; private set; }

		/// <inheritdoc/>
		public Task<ApplyResult> ApplyAsync(
			ProxyOptions      desiredState,
			CancellationToken cancellationToken)
		{
			ApplyCallCount++;
			LastDesiredState = desiredState;

			return Task.FromResult(result);
		}
	}

	/// <summary>
	/// A minimal <see cref="IOptionsMonitor{TOptions}"/> over a fixed snapshot that counts how often
	/// <see cref="CurrentValue"/> is read, so a test can prove the service snapshots the options exactly once.
	/// </summary>
	/// <param name="value">The fixed options snapshot.</param>
	private sealed class CountingOptionsMonitor(ProxyOptions value) : IOptionsMonitor<ProxyOptions>
	{
		/// <summary>Gets the number of times <see cref="CurrentValue"/> has been read.</summary>
		public int CurrentValueReadCount { get; private set; }

		/// <summary>Gets the fixed options snapshot, counting the read.</summary>
		public ProxyOptions CurrentValue
		{
			get
			{
				CurrentValueReadCount++;
				return value;
			}
		}

		/// <summary>Returns the fixed snapshot regardless of the requested name.</summary>
		/// <param name="name">The options name (ignored).</param>
		/// <returns>The fixed options snapshot.</returns>
		public ProxyOptions Get(string? name) => value;

		/// <summary>Returns <see langword="null"/> because the snapshot never changes.</summary>
		/// <param name="listener">The change listener (ignored).</param>
		/// <returns><see langword="null"/>; no change notifications are raised.</returns>
		public IDisposable? OnChange(Action<ProxyOptions, string?> listener) => null;
	}

	#endregion
}
