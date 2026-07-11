// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Runtime.CompilerServices;

using AngleSharp.Dom;

using Bunit;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using OllamaProxy.Admin;
using OllamaProxy.Admin.Config;
using OllamaProxy.Admin.Editing;
using OllamaProxy.Admin.Ui.Pages;
using OllamaProxy.Configuration;
using OllamaProxy.Core;
using OllamaProxy.Hosting;
using OllamaProxy.Providers;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Tests.Admin.Ui.Pages;

public sealed partial class BackendsPageTests
{
	/// <summary>
	/// The provider catalog the page consults for default base URLs and effective-mode resolution. It is the real
	/// <see cref="ProviderCatalog"/> built from a small fixed descriptor set — an endpoint-prefilling family
	/// (<c>openai</c>), a self-hosted family (<c>vllm</c>), and a plug-and-play family (<c>venice</c>) — so the
	/// page's <c>DefaultBaseUrlFor()</c> and <c>ResolveMode()</c> lookups run exactly as they do in the app.
	/// </summary>
	private static readonly IProviderCatalog SampleCatalog = new ProviderCatalog(
	[
		new ProviderDescriptor("openai", "OpenAI", OperatingMode.Explicit, "https://api.openai.com/v1"),
		new ProviderDescriptor("vllm", "vLLM", OperatingMode.Explicit, string.Empty),
		new ProviderDescriptor("venice", "Venice", OperatingMode.PlugAndPlay, "https://api.venice.ai/api/v1")
	]);

	/// <summary>
	/// Renders <see cref="Backends"/> with the fake admin service and the sample catalog registered, and with
	/// loose JS interop so the page's <c>adminDirtyGuard.js</c> and <c>adminCommitShortcut.js</c> module imports
	/// are harmless no-ops. The page loads its draft during the initial render (its <c>OnInitializedAsync()</c>
	/// awaits <see cref="IAdminModelService.GetEditableStateAsync"/>), so the returned component has already
	/// completed its first load when the fake resolves synchronously.
	/// </summary>
	/// <param name="service">The fake admin service; defaults to one returning an empty draft.</param>
	/// <returns>The rendered page together with the fake service driving it.</returns>
	private (IRenderedComponent<Backends> Component, FakeAdminModelService Service) RenderBackends(
		FakeAdminModelService? service = null)
	{
		service ??= new FakeAdminModelService();

		// Loose mode lets the two JS module imports in OnAfterRenderAsync() resolve to no-op handles; a test that
		// needs to assert the setDirty push sets up the module explicitly before rendering.
		JSInterop.Mode = JSRuntimeMode.Loose;

		Services.AddSingleton<IAdminModelService>(service);
		Services.AddSingleton(SampleCatalog);
		Services.AddSingleton<IOptions<AdminOptions>>(
			Options.Create(new AdminOptions { ApiKeyPersistencePolicy = ApiKeyPersistencePolicy.WriteToFile }));

		IRenderedComponent<Backends> component = Render<Backends>();
		return (component, service);
	}

	/// <summary>
	/// Builds an editable draft carrying the given backends, mirroring the shape
	/// <see cref="IAdminModelService.GetEditableStateAsync"/> returns.
	/// </summary>
	/// <param name="backends">The backends the draft holds; none for an empty configuration.</param>
	/// <returns>The configured draft.</returns>
	private static DesiredProxyState CreateDraft(params DesiredBackend[] backends) =>
		new() { Backends = [.. backends] };

	/// <summary>
	/// Builds a <see cref="DesiredBackend"/> for the render fixtures, defaulting to a named, OpenAI, Explicit
	/// backend with a valid base URL so the draft is structurally sound (non-blank, unique name).
	/// </summary>
	/// <param name="name">The logical backend name.</param>
	/// <param name="mode">The operating mode; defaults to <see cref="OperatingMode.Explicit"/>.</param>
	/// <returns>The configured backend, carrying its own name as <see cref="DesiredBackend.OriginalName"/>.</returns>
	private static DesiredBackend CreateBackend(string name, OperatingMode? mode = OperatingMode.Explicit) => new()
	{
		Name = name,
		OriginalName = name,
		Options = new BackendOptions
		{
			BaseUrl = "https://api.openai.com/v1",
			ProviderType = "openai",
			Mode = mode
		}
	};

	/// <summary>
	/// Reads the backend-card display names in document order, giving page tests a stable, operator-visible view of
	/// which cards are rendered without inspecting private page state.
	/// </summary>
	/// <param name="component">The rendered <see cref="Backends"/> page.</param>
	/// <returns>The card names rendered in the page.</returns>
	private static IReadOnlyList<string> BackendCardNames(IRenderedComponent<Backends> component) => component
		.FindAll("span.backend-card-name")
		.Select(static element => element.TextContent)
		.ToList();

	/// <summary>
	/// Reads the model upstream names rendered in the expanded model table, in document order.
	/// </summary>
	/// <param name="component">The rendered <see cref="Backends"/> page.</param>
	/// <returns>The upstream model names visible in the table.</returns>
	private static IReadOnlyList<string> ModelUpstreamNames(IRenderedComponent<Backends> component) =>
		component.FindAll("span.model-upstream").Select(static element => element.TextContent).ToList();

	/// <summary>
	/// Gets the rendered apply-result banner element for scenarios that know a result must be visible.
	/// </summary>
	/// <param name="component">The rendered <see cref="Backends"/> page.</param>
	/// <returns>The single apply-result banner element.</returns>
	private static IElement ApplyResultBanner(IRenderedComponent<Backends> component) =>
		component.Find(".apply-result");

	/// <summary>
	/// Gets the headline text rendered inside the apply-result banner.
	/// </summary>
	/// <param name="component">The rendered <see cref="Backends"/> page.</param>
	/// <returns>The exact headline text.</returns>
	private static string ApplyResultHeadline(IRenderedComponent<Backends> component) =>
		component.Find(".apply-result-headline").TextContent;

	/// <summary>
	/// A configurable <see cref="IAdminModelService"/> test double. Every method is driven by a settable delegate
	/// or backing value so a test supplies only the behavior its branch exercises, and each call is counted so the
	/// page's orchestration (how many loads, applies, or fetches it triggers) can be asserted.
	/// </summary>
	private sealed class FakeAdminModelService : IAdminModelService
	{
		/// <summary>
		/// The factory the fake invokes on each <see cref="GetEditableStateAsync"/> call. A fresh draft per call
		/// mirrors the real service reading the live configuration anew, so a post-apply reload sees a distinct
		/// instance. Defaults to an empty draft.
		/// </summary>
		public Func<DesiredProxyState> StateFactory { get; set; } = static () => new DesiredProxyState();

		/// <summary>
		/// The handler backing <see cref="FetchDraftSnapshotAsync"/>. Defaults to an empty successful snapshot.
		/// </summary>
		public Func<DesiredBackend, DiscoveryProbePolicy, DraftModelSnapshot> FetchHandler { get; set; } =
			static (_, _) => DraftModelSnapshot.Success([]);

		/// <summary>
		/// The candidates <see cref="ProbeDraftStreamingAsync"/> yields, in order. Defaults to none.
		/// </summary>
		public IReadOnlyList<DiscoveryCandidate> ProbeCandidates { get; set; } = [];

		/// <summary>
		/// An optional exception the streaming probe throws after yielding <see cref="ProbeCandidates"/>, used to
		/// exercise the page's fault-classification branch. <see langword="null"/> for a clean stream.
		/// </summary>
		public Exception? ProbeException { get; set; }

		/// <summary>
		/// The handler backing <see cref="ApplyDesiredStateAsync"/>. Defaults to a successful apply.
		/// </summary>
		public Func<DesiredProxyState, ApplyResult> ApplyHandler { get; set; } = static _ => ApplyResult.Applied;

		/// <summary>
		/// Gets the number of times <see cref="GetEditableStateAsync"/> has been called (initial load plus every
		/// discard or post-apply reload).
		/// </summary>
		public int GetEditableStateCallCount { get; private set; }

		/// <summary>
		/// Gets the number of times <see cref="ApplyDesiredStateAsync"/> has been called.
		/// </summary>
		public int ApplyCallCount { get; private set; }

		/// <summary>
		/// Gets the number of times <see cref="FetchDraftSnapshotAsync"/> has been called.
		/// </summary>
		public int FetchDraftSnapshotCallCount { get; private set; }

		/// <summary>
		/// Gets the probe policy passed to the most recent <see cref="FetchDraftSnapshotAsync"/> call, or
		/// <see langword="null"/> when no buffered fetch has run.
		/// </summary>
		public DiscoveryProbePolicy? LastFetchProbePolicy { get; private set; }

		/// <summary>
		/// Gets the number of times <see cref="ProbeDraftStreamingAsync"/> has been called.
		/// </summary>
		public int ProbeDraftStreamingCallCount { get; private set; }

		/// <summary>
		/// Gets the probe policy passed to the most recent <see cref="ProbeDraftStreamingAsync"/> call, or
		/// <see langword="null"/> when no streaming probe has run.
		/// </summary>
		public DiscoveryProbePolicy? LastStreamingProbePolicy { get; private set; }

		/// <summary>
		/// Gets the draft passed to the most recent <see cref="ApplyDesiredStateAsync"/> call, or
		/// <see langword="null"/> when apply has not run.
		/// </summary>
		public DesiredProxyState? LastAppliedState { get; private set; }

		/// <inheritdoc/>
		public Task<DraftModelSnapshot> FetchDraftSnapshotAsync(
			DesiredBackend       draft,
			DiscoveryProbePolicy probePolicy,
			CancellationToken    cancellationToken)
		{
			FetchDraftSnapshotCallCount++;
			LastFetchProbePolicy = probePolicy;
			return Task.FromResult(FetchHandler(draft, probePolicy));
		}

		/// <inheritdoc/>
		public async IAsyncEnumerable<DiscoveryCandidate> ProbeDraftStreamingAsync(
			DesiredBackend                             draft,
			DiscoveryProbePolicy                       probePolicy,
			[EnumeratorCancellation] CancellationToken cancellationToken)
		{
			ProbeDraftStreamingCallCount++;
			LastStreamingProbePolicy = probePolicy;

			foreach (DiscoveryCandidate candidate in ProbeCandidates)
			{
				cancellationToken.ThrowIfCancellationRequested();
				yield return candidate;
			}

			if (ProbeException is not null)
			{
				throw ProbeException;
			}

			await Task.CompletedTask.ConfigureAwait(false);
		}

		/// <inheritdoc/>
		public Task<DesiredProxyState> GetEditableStateAsync(CancellationToken cancellationToken)
		{
			GetEditableStateCallCount++;
			return Task.FromResult(StateFactory());
		}

		/// <inheritdoc/>
		public Task<ApplyResult> ApplyDesiredStateAsync(
			DesiredProxyState desiredState,
			CancellationToken cancellationToken)
		{
			ApplyCallCount++;
			LastAppliedState = desiredState;
			return Task.FromResult(ApplyHandler(desiredState));
		}
	}
}
