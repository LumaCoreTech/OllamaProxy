// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using AngleSharp.Dom;

using Bunit;

using OllamaProxy.Admin.Editing;
using OllamaProxy.Admin.Reconciliation;
using OllamaProxy.Admin.Ui.Components.Backends;
using OllamaProxy.Configuration;
using OllamaProxy.Core;
using OllamaProxy.Providers;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Tests.Admin.Ui.Components.Backends;

public sealed partial class BackendCardTests
{
	/// <summary>
	/// A single discovery candidate used only to satisfy the models table's "a snapshot has loaded" gate
	/// (<see cref="ModelListState.Snapshot"/> is non-<see langword="null"/>). No card test reads its fields; the
	/// card composes <see cref="BackendModels"/>, whose own suite covers the table itself.
	/// </summary>
	private static readonly DiscoveryCandidate SampleCandidate = new(
		ClientName: "sample",
		UpstreamModel: "sample",
		ReportedContextLength: null,
		Capabilities: null);

	/// <summary>
	/// The provider catalog the card consults for its header pills and the settings form's picker. It is the real
	/// <see cref="ProviderCatalog"/> rather than a fake, built from a small fixed descriptor set: an
	/// endpoint-prefilling family (<c>openai</c>), a self-hosted family (<c>vllm</c>), and a plug-and-play family
	/// (<c>venice</c>). Reusing the production catalog means the card's <c>ResolveMode</c> / <c>DisplayNameFor</c>
	/// lookups are exercised exactly as they run in the app.
	/// </summary>
	private static readonly IProviderCatalog SampleCatalog = new ProviderCatalog(
	[
		new ProviderDescriptor("openai", "OpenAI", OperatingMode.Explicit, "https://api.openai.com/v1"),
		new ProviderDescriptor("vllm", "vLLM", OperatingMode.Explicit, string.Empty),
		new ProviderDescriptor("venice", "Venice", OperatingMode.PlugAndPlay, "https://api.venice.ai/api/v1")
	]);

	/// <summary>
	/// Renders <see cref="BackendCard"/> with sensible defaults for every required parameter, so a test only
	/// supplies the inputs relevant to the branch it exercises. The four page-supplied closures the card forwards
	/// to <see cref="BackendModels"/> default to harmless implementations because the models table invokes them
	/// during render; leaving them unset would throw once the card is expanded.
	/// </summary>
	/// <param name="backend">The backend this card renders; defaults to a named, OpenAI, Explicit backend.</param>
	/// <param name="expanded">Whether the card's editor is open; defaults to <see langword="true"/> so the panel renders.</param>
	/// <param name="advancedExpanded">Whether the advanced disclosure is open; forwarded to the advanced section.</param>
	/// <param name="isBusy">Whether the owning page is busy, mirrored to every interactive control in the card.</param>
	/// <param name="modeIgnoresPins">Whether the effective mode ignores pins (plug-and-play); forwarded to the models table.</param>
	/// <param name="state">The per-backend model-list state; defaults to a loaded state with a non-null snapshot.</param>
	/// <param name="reconciliation">The reconciliation result, or <see langword="null"/> for the "no models" branch.</param>
	/// <param name="providerCatalog">The provider catalog; defaults to <see cref="SampleCatalog"/>.</param>
	/// <param name="configure">An optional hook to add event-callback parameters the test wants to observe.</param>
	/// <returns>The rendered <see cref="BackendCard"/> component.</returns>
	private IRenderedComponent<BackendCard> RenderCard(
		DesiredBackend?                                           backend          = null,
		bool                                                      expanded         = true,
		bool                                                      advancedExpanded = false,
		bool                                                      isBusy           = false,
		bool                                                      modeIgnoresPins  = false,
		ModelListState?                                           state            = null,
		ReconciliationResult?                                     reconciliation   = null,
		IProviderCatalog?                                         providerCatalog  = null,
		Action<ComponentParameterCollectionBuilder<BackendCard>>? configure        = null)
	{
		backend ??= CreateBackend();
		state ??= LoadedState();
		providerCatalog ??= SampleCatalog;

		return Render<BackendCard>(parameters =>
		{
			parameters
				.Add(component => component.Backend, backend)
				.Add(component => component.Expanded, expanded)
				.Add(component => component.AdvancedExpanded, advancedExpanded)
				.Add(component => component.IsBusy, isBusy)
				.Add(component => component.ModeIgnoresPins, modeIgnoresPins)
				.Add(component => component.State, state)
				.Add(component => component.Reconciliation, reconciliation)
				.Add(component => component.ProviderCatalog, providerCatalog)
				.Add(component => component.IsDetailsExpanded, static _ => false)
				.Add(component => component.GetPinnedName, static model => model.Name)
				.Add(component => component.GetPinnedReasoning, static _ => (ReasoningEffort?)null)
				.Add(component => component.GetContextOverride, static _ => (int?)null);

			configure?.Invoke(parameters);
		});
	}

	/// <summary>
	/// Builds a <see cref="DesiredBackend"/> fixture for the card. Only the fields the header reads —
	/// <paramref name="name"/>, <paramref name="providerType"/>, and <paramref name="mode"/> — are parameterized;
	/// the remaining connection fields carry fixed placeholder values the header never renders.
	/// </summary>
	/// <param name="name">
	/// The backend's logical name shown in the header, or <see langword="null"/> to model an unnamed
	/// backend.
	/// </param>
	/// <param name="providerType">The provider-type discriminator resolved to the header's provider pill.</param>
	/// <param name="mode">The pinned operating mode resolved to the header's mode pill.</param>
	/// <returns>The assembled backend draft.</returns>
	private static DesiredBackend CreateBackend(
		string?        name         = "openai-prod",
		string         providerType = "openai",
		OperatingMode? mode         = OperatingMode.Explicit)
	{
		return new DesiredBackend
		{
			Name = name!,
			OriginalName = name,
			Options = new BackendOptions
			{
				BaseUrl = "https://api.openai.com/v1",
				ProviderType = providerType,
				ApiKey = string.Empty,
				Mode = mode
			}
		};
	}

	/// <summary>
	/// The loaded model-list state: a non-null snapshot with no in-flight fetch and no error, so the composed
	/// <see cref="BackendModels"/> renders its table (or its "reports no models" placeholder for a null
	/// reconciliation) rather than a loading state.
	/// </summary>
	/// <returns>A loaded <see cref="ModelListState"/>.</returns>
	private static ModelListState LoadedState()
	{
		return new ModelListState(
			Snapshot: [SampleCandidate],
			IsFetching: false,
			Error: null);
	}

	/// <summary>
	/// The fetching model-list state: a loaded snapshot with an in-flight fetch/probe
	/// (<see cref="ModelListState.IsFetching"/> set) and no page-global busy flag. Models the per-backend probe the
	/// card folds into its <c>EditorBusy</c> lock — freezing the settings and advanced fields — while leaving the
	/// page-global <see cref="BackendCard.IsBusy"/> clear so the advanced header and the models table's Cancel stay
	/// operable.
	/// </summary>
	/// <returns>A fetching <see cref="ModelListState"/>.</returns>
	private static ModelListState FetchingState()
	{
		return new ModelListState(
			Snapshot: [SampleCandidate],
			IsFetching: true,
			Error: null);
	}

	/// <summary>
	/// Gets the card's header button — the toggle carrying the display name, the provider/mode pills, and the
	/// <c>aria-expanded</c> / <c>aria-controls</c> state. It is always rendered, open or closed.
	/// </summary>
	/// <param name="cut">The rendered component.</param>
	/// <returns>The header button element.</returns>
	private static IElement Header(IRenderedComponent<BackendCard> cut) => cut.Find("button.backend-card-header");

	/// <summary>
	/// Gets the quiet neutral pills rendered in the header, in document order. When the provider label is present
	/// there are two — provider first, then mode; when it is suppressed there is one — the always-present mode pill.
	/// </summary>
	/// <param name="cut">The rendered component.</param>
	/// <returns>The header badge span elements.</returns>
	private static IReadOnlyList<IElement> HeaderBadges(IRenderedComponent<BackendCard> cut) =>
		cut.FindAll("button.backend-card-header span.badge");

	/// <summary>
	/// Gets the composed <see cref="BackendSettings"/> child. Only present when the card is expanded.
	/// </summary>
	/// <param name="cut">The rendered component.</param>
	/// <returns>The rendered settings child.</returns>
	private static IRenderedComponent<BackendSettings> Settings(IRenderedComponent<BackendCard> cut) =>
		cut.FindComponent<BackendSettings>();

	/// <summary>
	/// Gets the composed <see cref="BackendAdvanced"/> child. Only present when the card is expanded.
	/// </summary>
	/// <param name="cut">The rendered component.</param>
	/// <returns>The rendered advanced child.</returns>
	private static IRenderedComponent<BackendAdvanced> Advanced(IRenderedComponent<BackendCard> cut) =>
		cut.FindComponent<BackendAdvanced>();

	/// <summary>
	/// Gets the composed <see cref="BackendModels"/> child. Only present when the card is expanded.
	/// </summary>
	/// <param name="cut">The rendered component.</param>
	/// <returns>The rendered models child.</returns>
	private static IRenderedComponent<BackendModels> Models(IRenderedComponent<BackendCard> cut) =>
		cut.FindComponent<BackendModels>();
}
