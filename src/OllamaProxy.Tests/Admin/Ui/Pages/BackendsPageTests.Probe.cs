// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Bunit;

using OllamaProxy.Admin;
using OllamaProxy.Admin.Fetch;
using OllamaProxy.Admin.Ui.Pages;
using OllamaProxy.Core;

namespace OllamaProxy.Tests.Admin.Ui.Pages;

// Model discovery: the lazy fetch on first expand, the explicit refresh, and the streaming capability probe.
//
// A card fetches its model snapshot the first time it expands (never again on its own), so expanding a card is
// the page's model-list data source. A successful fetch reconciles into rows; a failed fetch surfaces the
// classified error message instead. The Probe button streams candidates in, filling the table, and a mid-stream
// fault surfaces as the classified BackendFetchException message. These tests drive that lifecycle through the
// rendered models section beneath an expanded card.
//
// For the load lifecycle and shared harness see the anchor file and Helpers.
public sealed partial class BackendsPageTests
{
	// --- 6. Model discovery: fetch on expand, refresh, streaming probe ---

	/// <summary>
	/// A discovered candidate used to populate the fetch and probe fixtures. Its client name drives the reconciled
	/// row's identity; the remaining fields are the discovered shape the row renders.
	/// </summary>
	private static readonly DiscoveryCandidate SampleModel = new(
		ClientName: "llama3",
		UpstreamModel: "llama3",
		ReportedContextLength: 8192,
		Capabilities: null);

	/// <summary>
	/// Verifies that expanding a card fetches its snapshot exactly once and renders the discovered model as a row,
	/// so the operator sees the backend's models without an explicit refresh.
	/// </summary>
	[Fact]
	public void ToggleExpandAsync_WhenSnapshotSucceeds_FetchesOnceAndRendersRows()
	{
		// Arrange
		int fetchCount = 0;
		FakeAdminModelService service = new()
		{
			StateFactory = static () => CreateDraft(CreateBackend("primary")),
			FetchHandler = (_, _) =>
			{
				fetchCount++;
				return DraftModelSnapshot.Success([SampleModel]);
			}
		};
		(IRenderedComponent<Backends> component, FakeAdminModelService _) = RenderBackends(service);

		// Act
		component.Find("button.backend-card-header").Click();

		// Assert
		Assert.Equal(1, fetchCount);
		Assert.Equal(1, service.FetchDraftSnapshotCallCount);
		Assert.Equal(DiscoveryProbePolicy.NeverProbe, service.LastFetchProbePolicy);
		Assert.Equal(["llama3"], ModelUpstreamNames(component));
		Assert.Empty(component.FindAll("p.backend-empty"));
		Assert.Empty(component.FindAll("p.backend-error"));
	}

	/// <summary>
	/// Verifies that a failed fetch surfaces the classified error message beneath the card instead of a model
	/// table, so the operator learns why discovery failed rather than seeing a misleadingly empty list.
	/// </summary>
	[Fact]
	public void ToggleExpandAsync_WhenSnapshotFails_ShowsClassifiedError()
	{
		// Arrange
		FakeAdminModelService service = new()
		{
			StateFactory = static () => CreateDraft(CreateBackend("primary")),
			FetchHandler = static (_, _) => DraftModelSnapshot.FromFailedFetch(
				BackendFetchResult.Failure("primary", BackendFetchErrorKind.Authentication, "Invalid API key."))
		};
		(IRenderedComponent<Backends> component, FakeAdminModelService _) = RenderBackends(service);

		// Act
		component.Find("button.backend-card-header").Click();

		// Assert
		Assert.Equal("Invalid API key.", component.Find("p.backend-error").TextContent);
		Assert.Equal(1, service.FetchDraftSnapshotCallCount);
		Assert.Equal(DiscoveryProbePolicy.NeverProbe, service.LastFetchProbePolicy);
		Assert.Empty(component.FindAll("table.backend-table"));
	}

	/// <summary>
	/// Verifies that clicking Refresh re-fetches the snapshot, so an operator who changed the URL or provider can
	/// pull the current models without committing first. The re-fetch is observed as a second fetch beyond the
	/// first-expand fetch.
	/// </summary>
	[Fact]
	public void RefreshModelsAsync_WhenClicked_RefetchesSnapshot()
	{
		// Arrange
		int fetchCount = 0;
		FakeAdminModelService service = new()
		{
			StateFactory = static () => CreateDraft(CreateBackend("primary")),
			FetchHandler = (_, _) =>
			{
				fetchCount++;
				return DraftModelSnapshot.Success([SampleModel]);
			}
		};
		(IRenderedComponent<Backends> component, FakeAdminModelService _) = RenderBackends(service);
		component.Find("button.backend-card-header").Click();

		// Act
		component.Find("button.backend-model-action").Click();

		// Assert
		// One fetch on expand, one on the explicit refresh.
		Assert.Equal(2, fetchCount);
		Assert.Equal(2, service.FetchDraftSnapshotCallCount);
		Assert.Equal(DiscoveryProbePolicy.NeverProbe, service.LastFetchProbePolicy);
		Assert.Equal(["llama3"], ModelUpstreamNames(component));
	}

	/// <summary>
	/// Verifies that the streaming probe fills the table with its yielded candidates, so a capability probe of a
	/// poor backend still renders the resolved models. The synchronous fake stream settles into a loaded snapshot,
	/// so the reconciled rows render rather than the "reports no models" placeholder.
	/// </summary>
	[Fact]
	public void ProbeModelsAsync_WhenStreamYieldsCandidates_FillsTheTable()
	{
		// Arrange
		FakeAdminModelService service = new()
		{
			StateFactory = static () => CreateDraft(CreateBackend("primary")),
			ProbeCandidates = [SampleModel]
		};
		(IRenderedComponent<Backends> component, FakeAdminModelService _) = RenderBackends(service);
		component.Find("button.backend-card-header").Click();

		// Act
		// The second action button in the models header is "Probe capabilities".
		component.FindAll("button.backend-model-action")[1].Click();

		// Assert
		Assert.Equal(1, service.ProbeDraftStreamingCallCount);
		Assert.Equal(DiscoveryProbePolicy.ProbeAll, service.LastStreamingProbePolicy);
		Assert.Equal(["llama3"], ModelUpstreamNames(component));
		Assert.Empty(component.FindAll("p.backend-empty"));
		Assert.Empty(component.FindAll("p.backend-error"));
	}

	/// <summary>
	/// Verifies that a probe faulting mid-stream surfaces the classified <see cref="BackendFetchException"/> message
	/// beneath the card, so the operator sees the honest failure rather than a truncated partial list.
	/// </summary>
	[Fact]
	public void ProbeModelsAsync_WhenStreamFaults_ShowsClassifiedError()
	{
		// Arrange
		FakeAdminModelService service = new()
		{
			StateFactory = static () => CreateDraft(CreateBackend("primary")),
			ProbeException = new BackendFetchException(
				BackendFetchErrorKind.Upstream,
				"The backend returned HTTP 500.",
				innerException: null)
		};
		(IRenderedComponent<Backends> component, FakeAdminModelService _) = RenderBackends(service);
		component.Find("button.backend-card-header").Click();

		// Act
		component.FindAll("button.backend-model-action")[1].Click();

		// Assert
		Assert.Equal("The backend returned HTTP 500.", component.Find("p.backend-error").TextContent);
		Assert.Equal(1, service.ProbeDraftStreamingCallCount);
		Assert.Equal(DiscoveryProbePolicy.ProbeAll, service.LastStreamingProbePolicy);
		Assert.Empty(component.FindAll("table.backend-table"));
	}
}
