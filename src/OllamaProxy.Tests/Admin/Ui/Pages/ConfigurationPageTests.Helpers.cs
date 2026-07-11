// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using AngleSharp.Dom;

using Bunit;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using OllamaProxy.Admin;
using OllamaProxy.Admin.Config;
using OllamaProxy.Admin.Editing;
using OllamaProxy.Configuration;
using OllamaProxy.Core;
using OllamaProxy.Hosting;
// The page type OllamaProxy.Admin.Ui.Pages.Configuration collides with the OllamaProxy.Configuration namespace,
// so it is aliased rather than imported via a plain using of OllamaProxy.Admin.Ui.Pages.
using ConfigurationPage = OllamaProxy.Admin.Ui.Pages.Configuration;

namespace OllamaProxy.Tests.Admin.Ui.Pages;

public sealed partial class ConfigurationPageTests
{
	/// <summary>
	/// Renders <see cref="ConfigurationPage"/> with the fake admin service registered and loose JS interop so the
	/// page's <c>adminDirtyGuard.js</c> and <c>adminCommitShortcut.js</c> module imports resolve to harmless
	/// no-ops. The page loads its draft during the initial render (its <c>OnInitializedAsync()</c> awaits
	/// <see cref="IAdminModelService.GetEditableStateAsync"/>), so the returned component has already completed its
	/// first load when the fake resolves synchronously.
	/// </summary>
	/// <param name="service">The fake admin service; defaults to one returning an empty draft.</param>
	/// <returns>The rendered page together with the fake service driving it.</returns>
	private (IRenderedComponent<ConfigurationPage> Component, FakeAdminModelService Service) RenderConfiguration(
		FakeAdminModelService? service = null)
	{
		service ??= new FakeAdminModelService();

		// Loose mode lets the two JS module imports in OnAfterRenderAsync() resolve to no-op handles; no test here
		// needs to assert the setDirty push, so the shared loose behavior is sufficient.
		JSInterop.Mode = JSRuntimeMode.Loose;

		Services.AddSingleton<IAdminModelService>(service);
		Services.AddSingleton<IOptions<AdminOptions>>(
			Options.Create(new AdminOptions { ApiKeyPersistencePolicy = ApiKeyPersistencePolicy.WriteToFile }));

		IRenderedComponent<ConfigurationPage> component = Render<ConfigurationPage>();
		return (component, service);
	}

	/// <summary>
	/// Builds an editable draft carrying the given listener URL and tracing settings, mirroring the shape
	/// <see cref="IAdminModelService.GetEditableStateAsync"/> returns.
	/// </summary>
	/// <param name="listenUrl">The listener URL; defaults to a valid local address.</param>
	/// <param name="tracingEnabled">Whether request tracing is enabled; defaults to disabled.</param>
	/// <returns>The configured draft.</returns>
	private static DesiredProxyState CreateDraft(
		string listenUrl      = "http://localhost:11434",
		bool   tracingEnabled = false) => new()
	{
		ListenUrl = listenUrl,
		RequestTracing = new RequestTracingOptions { Enabled = tracingEnabled }
	};

	/// <summary>
	/// Reads the current value of the listener-URL input rendered in the configuration form.
	/// </summary>
	/// <param name="component">The rendered <see cref="ConfigurationPage"/> page.</param>
	/// <returns>The listener-URL input's value attribute.</returns>
	private static string ListenUrlValue(IRenderedComponent<ConfigurationPage> component) =>
		component.Find("input.configuration-input").GetAttribute("value") ?? string.Empty;

	/// <summary>
	/// Reads the configuration-field labels in document order, giving tests a stable operator-visible view of which
	/// controls the page currently exposes.
	/// </summary>
	/// <param name="component">The rendered <see cref="ConfigurationPage"/> page.</param>
	/// <returns>The label texts rendered in the form.</returns>
	private static IReadOnlyList<string> ConfigurationLabels(IRenderedComponent<ConfigurationPage> component) =>
		component.FindAll(".configuration-label").Select(static label => label.TextContent).ToList();

	/// <summary>
	/// Gets the rendered apply-result banner element for scenarios that know a result must be visible.
	/// </summary>
	/// <param name="component">The rendered <see cref="ConfigurationPage"/> page.</param>
	/// <returns>The single apply-result banner element.</returns>
	private static IElement ApplyResultBanner(IRenderedComponent<ConfigurationPage> component) =>
		component.Find(".apply-result");

	/// <summary>
	/// Gets the headline text rendered inside the apply-result banner.
	/// </summary>
	/// <param name="component">The rendered <see cref="Configuration"/> page.</param>
	/// <returns>The exact headline text.</returns>
	private static string ApplyResultHeadline(IRenderedComponent<ConfigurationPage> component) =>
		component.Find(".apply-result-headline").TextContent;

	/// <summary>
	/// A configurable <see cref="IAdminModelService"/> test double. The configuration page only calls
	/// <see cref="GetEditableStateAsync"/> and <see cref="ApplyDesiredStateAsync"/>; the fetch and streaming-probe
	/// members are required by the interface but never exercised by this page, so they throw to make an accidental
	/// call obvious. Each used method is driven by a settable delegate and counts its calls so the page's
	/// orchestration (how many loads or applies it triggers) can be asserted.
	/// </summary>
	private sealed class FakeAdminModelService : IAdminModelService
	{
		/// <summary>
		/// The factory the fake invokes on each <see cref="GetEditableStateAsync"/> call. A fresh draft per call
		/// mirrors the real service reading the live configuration anew, so a post-apply reload sees a distinct
		/// instance. Defaults to a valid draft.
		/// </summary>
		public Func<DesiredProxyState> StateFactory { get; set; } = static () => CreateDraft();

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
		/// Gets the draft passed to the most recent <see cref="ApplyDesiredStateAsync"/> call, or
		/// <see langword="null"/> when apply has not run.
		/// </summary>
		public DesiredProxyState? LastAppliedState { get; private set; }

		/// <inheritdoc/>
		public Task<DraftModelSnapshot> FetchDraftSnapshotAsync(
			DesiredBackend       draft,
			DiscoveryProbePolicy probePolicy,
			CancellationToken    cancellationToken) =>
			throw new InvalidOperationException("The configuration page must not fetch model snapshots.");

		/// <inheritdoc/>
		public IAsyncEnumerable<DiscoveryCandidate> ProbeDraftStreamingAsync(
			DesiredBackend       draft,
			DiscoveryProbePolicy probePolicy,
			CancellationToken    cancellationToken) =>
			throw new InvalidOperationException("The configuration page must not stream model probes.");

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
