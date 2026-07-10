// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using AngleSharp.Dom;

using Bunit;

using OllamaProxy.Admin.Editing;
using OllamaProxy.Admin.Ui.Components.Backends;
using OllamaProxy.Configuration;

namespace OllamaProxy.Tests.Admin.Ui.Components.Backends;

// Backend card: the collapsible per-backend row that composes the whole editor.
//
// BackendCard is the unit the page's foreach renders once per draft backend. It owns a header (display name plus
// provider and mode pills) and, when expanded, an editor panel that stitches together three already-tested
// children — BackendSettings, BackendAdvanced, BackendModels — plus a remove action. Because those children have
// their own suites, these tests assert the card's *own* contract: the header it renders, the expand gate, and how
// it wires itself to its children (which values it forwards, and the two it renames on the way through). They do
// not re-assert the children's internal DOM.
//
//   1. Header: the display name (delegated to the presenter), the provider and mode pills, and the suppressed
//      provider pill for an unset provider (RendersDisplayName / RendersProviderAndModePills /
//      WhenProviderUnset).
//
//   2. Expand gate: the editor panel and its three children render only when open, and the header's a11y state
//      (aria-expanded, aria-label, indicator, aria-controls) tracks the open/closed state
//      (WhenExpanded* / WhenCollapsed*).
//
//   3. Composition wiring: the card forwards Backend / IsBusy / catalog to the right children and renames
//      AdvancedExpanded → BackendAdvanced.Expanded and ProviderCatalog.Providers → BackendSettings.Providers
//      (Forwards* / Renames*). The busy wiring is two locks: a page-global IsBusy plus a derived editor lock
//      (IsBusy || State.IsFetching) that freezes the settings and advanced fields during a per-backend probe while
//      the advanced header and the models table's Cancel stay operable (WhenFetching*).
//
//   4. Remove action: the remove button is present when expanded and gated by IsBusy (RendersRemoveButton /
//      WhenBusy).
//
// For the callback wiring — the two owned events and the forwarded ones — see Callbacks.

/// <summary>
/// Render tests for <see cref="BackendCard"/>, the collapsible per-backend row that composes the full editor.
/// The card owns a header and, when expanded, stitches together <see cref="BackendSettings"/>,
/// <see cref="BackendAdvanced"/>, and <see cref="BackendModels"/>. These tests assert the card's own contract —
/// the header copy it renders (delegating the display name to <see cref="BackendCardPresenter"/>), the expand
/// gate, and the parameters it forwards to and renames for its children — rather than re-asserting the children's
/// DOM, which their own suites cover.
/// </summary>
/// <remarks>
/// The suite is split across partial files, each a chapter in the same story. Reading order:
/// <list type="number">
///     <item>
///         <description>
///         This anchor: the header (display name, provider and mode pills), the expand gate and its header a11y
///         state, the composition wiring (which parameters reach which child, including the two the card renames),
///         and the busy-gated remove button.
///         </description>
///     </item>
///     <item>
///         <description>
///         <c>Callbacks</c>: the event wiring — the card's own header-toggle and remove events, plus the callbacks
///         it forwards from its children (provider, mode, advanced-toggle, model-prefix, configuration-changed, and
///         the models-table actions).
///         </description>
///     </item>
/// </list>
/// The shared render harness, backend fixture builder, real provider catalog, and child accessors live in the
/// <c>Helpers</c> partial.
/// </remarks>
[Trait("Category", "Unit")]
public sealed partial class BackendCardTests : BunitContext
{
	// --- 1. Header ---

	/// <summary>
	/// Verifies that the header renders the backend's display name, so the operator can identify the card in the
	/// collapsed list. The name is delegated to <see cref="BackendCardPresenter.DisplayName"/>, so this pins the
	/// card actually shows the presenter's output.
	/// </summary>
	[Fact]
	public void Render_Header_RendersDisplayName()
	{
		// Arrange
		DesiredBackend backend = CreateBackend(name: "openai-prod");

		// Act
		IRenderedComponent<BackendCard> cut = RenderCard(backend);

		// Assert
		Assert.Equal("openai-prod", cut.Find("span.backend-card-name").TextContent);
	}

	/// <summary>
	/// Verifies that an unnamed backend falls back to the presenter's placeholder in the header, so a not-yet-named
	/// card still has a stable label rather than a blank row.
	/// </summary>
	[Fact]
	public void Render_Header_WhenBackendUnnamed_RendersPlaceholder()
	{
		// Arrange
		DesiredBackend backend = CreateBackend(name: null);

		// Act
		IRenderedComponent<BackendCard> cut = RenderCard(backend);

		// Assert
		Assert.Equal("(unnamed backend)", cut.Find("span.backend-card-name").TextContent);
	}

	/// <summary>
	/// Verifies that the header renders two pills — the provider family then the operating mode — so the operator
	/// can tell one backend's provider and mode apart in the collapsed list. Both are resolved through the catalog:
	/// the provider label from the descriptor and the mode label from the presenter.
	/// </summary>
	[Fact]
	public void Render_Header_RendersProviderAndModePills()
	{
		// Arrange
		DesiredBackend backend = CreateBackend(providerType: "venice", mode: OperatingMode.Hybrid);

		// Act
		IRenderedComponent<BackendCard> cut = RenderCard(backend);

		// Assert: provider pill first, mode pill second, in document order.
		Assert.Equal(
			["Venice", "Hybrid"],
			HeaderBadges(cut).Select(badge => badge.TextContent).ToList());
	}

	/// <summary>
	/// Verifies that a backend whose effective mode is left to the provider default still renders the resolved
	/// mode pill, so the header shows the mode the backend will actually run in rather than a blank. With no pinned
	/// mode, an OpenAI backend resolves to Explicit through the catalog.
	/// </summary>
	[Fact]
	public void Render_Header_WhenModeUnset_RendersResolvedModePill()
	{
		// Arrange: no pinned mode; the OpenAI descriptor defaults to Explicit.
		DesiredBackend backend = CreateBackend(providerType: "openai", mode: null);

		// Act
		IRenderedComponent<BackendCard> cut = RenderCard(backend);

		// Assert: provider pill plus the resolved mode pill.
		Assert.Equal(
			["OpenAI", "Explicit"],
			HeaderBadges(cut).Select(badge => badge.TextContent).ToList());
	}

	/// <summary>
	/// Verifies that when the provider type resolves to an empty label the header suppresses the provider pill
	/// rather than showing a blank one, leaving only the always-present mode pill. The mode still resolves, so the
	/// header never collapses to zero pills.
	/// </summary>
	[Fact]
	public void Render_Header_WhenProviderLabelBlank_SuppressesProviderPill()
	{
		// Arrange: an empty provider type resolves to an empty display label, so only the mode pill remains.
		DesiredBackend backend = CreateBackend(providerType: string.Empty, mode: OperatingMode.Explicit);

		// Act
		IRenderedComponent<BackendCard> cut = RenderCard(backend);

		// Assert: exactly one pill — the mode — and no blank provider pill ahead of it.
		Assert.Equal(
			["Explicit"],
			HeaderBadges(cut).Select(badge => badge.TextContent).ToList());
	}

	// --- 2. Expand gate ---

	/// <summary>
	/// Verifies that an expanded card renders its editor panel with all three composed children, so opening a card
	/// reveals the full editor.
	/// </summary>
	[Fact]
	public void Render_WhenExpanded_RendersEditorWithAllThreeChildren()
	{
		// Act
		IRenderedComponent<BackendCard> cut = RenderCard(expanded: true);

		// Assert: the panel plus one of each child.
		Assert.Single(cut.FindAll("div.backend-editor"));
		Assert.Single(cut.FindComponents<BackendSettings>());
		Assert.Single(cut.FindComponents<BackendAdvanced>());
		Assert.Single(cut.FindComponents<BackendModels>());
	}

	/// <summary>
	/// Verifies that an expanded card sets its header's expanded a11y state — <c>aria-expanded="true"</c>, a
	/// "Collapse {name}" label, and the down-pointing indicator — so assistive technology and sighted users both
	/// read the card as open.
	/// </summary>
	[Fact]
	public void Render_WhenExpanded_SetsHeaderExpandedState()
	{
		// Arrange
		DesiredBackend backend = CreateBackend(name: "openai-prod");

		// Act
		IRenderedComponent<BackendCard> cut = RenderCard(backend, expanded: true);

		// Assert
		IElement header = Header(cut);
		Assert.Equal("true", header.GetAttribute("aria-expanded"));
		Assert.Equal("Collapse openai-prod", header.GetAttribute("aria-label"));
		Assert.Equal("▾", cut.Find("span.backend-expand-indicator").TextContent);
	}

	/// <summary>
	/// Verifies that the header's <c>aria-controls</c> references the editor panel's <c>id</c>, so assistive
	/// technology can associate the toggle with the region it reveals. The id is instance-generated, so the test
	/// asserts the linkage (and its stable prefix) rather than a fixed value.
	/// </summary>
	[Fact]
	public void Render_WhenExpanded_LinksHeaderToEditorViaAriaControls()
	{
		// Act
		IRenderedComponent<BackendCard> cut = RenderCard(expanded: true);

		// Assert: the header points at exactly the editor panel's generated id.
		string panelId = cut.Find("div.backend-editor").GetAttribute("id")!;
		Assert.StartsWith("backend-editor-", panelId);
		Assert.Equal(panelId, Header(cut).GetAttribute("aria-controls"));
	}

	/// <summary>
	/// Verifies that a collapsed card omits its editor panel and every composed child, keeping the whole editor out
	/// of the DOM until the operator opens the card.
	/// </summary>
	[Fact]
	public void Render_WhenCollapsed_OmitsEditorAndChildren()
	{
		// Act
		IRenderedComponent<BackendCard> cut = RenderCard(expanded: false);

		// Assert: no panel and none of the three children.
		Assert.Empty(cut.FindAll("div.backend-editor"));
		Assert.Empty(cut.FindComponents<BackendSettings>());
		Assert.Empty(cut.FindComponents<BackendAdvanced>());
		Assert.Empty(cut.FindComponents<BackendModels>());
	}

	/// <summary>
	/// Verifies that a collapsed card sets its header's collapsed a11y state — <c>aria-expanded="false"</c>, an
	/// "Expand {name}" label, and the right-pointing indicator — the observable counterpart to
	/// <see cref="Render_WhenExpanded_SetsHeaderExpandedState"/>.
	/// </summary>
	[Fact]
	public void Render_WhenCollapsed_SetsHeaderCollapsedState()
	{
		// Arrange
		DesiredBackend backend = CreateBackend(name: "openai-prod");

		// Act
		IRenderedComponent<BackendCard> cut = RenderCard(backend, expanded: false);

		// Assert
		IElement header = Header(cut);
		Assert.Equal("false", header.GetAttribute("aria-expanded"));
		Assert.Equal("Expand openai-prod", header.GetAttribute("aria-label"));
		Assert.Equal("▸", cut.Find("span.backend-expand-indicator").TextContent);
	}

	// --- 3. Composition wiring ---

	/// <summary>
	/// Verifies that the card hands the same <see cref="DesiredBackend"/> instance to all three children, so every
	/// section of the editor edits one shared draft rather than diverging copies.
	/// </summary>
	[Fact]
	public void Render_WhenExpanded_ForwardsSameBackendToEveryChild()
	{
		// Arrange
		DesiredBackend backend = CreateBackend();

		// Act
		IRenderedComponent<BackendCard> cut = RenderCard(backend);

		// Assert: reference equality — the identical draft, not an equal copy.
		Assert.Same(backend, Settings(cut).Instance.Backend);
		Assert.Same(backend, Advanced(cut).Instance.Backend);
		Assert.Same(backend, Models(cut).Instance.Backend);
	}

	/// <summary>
	/// Verifies that the card mirrors its <see cref="BackendCard.IsBusy"/> flag to every child, so a busy page
	/// disables the whole editor as a unit rather than leaving one section live. With no probe in flight the
	/// derived editor lock equals the page-global flag, so the settings and advanced-field locks match it too.
	/// </summary>
	[Fact]
	public void Render_WhenBusy_ForwardsIsBusyToEveryChild()
	{
		// Act
		IRenderedComponent<BackendCard> cut = RenderCard(isBusy: true);

		// Assert
		Assert.True(Settings(cut).Instance.IsBusy);
		Assert.True(Advanced(cut).Instance.IsBusy);
		Assert.True(Advanced(cut).Instance.FieldsBusy);
		Assert.True(Models(cut).Instance.IsBusy);
	}

	/// <summary>
	/// Verifies that a per-backend probe (<see cref="ModelListState.IsFetching"/> set while the page is not globally
	/// busy) freezes the editable settings and advanced fields: the settings form is busy and the advanced section's
	/// <see cref="BackendAdvanced.FieldsBusy"/> lock is raised. This is the probe half of the card's two-lock design
	/// — an edit cannot race a stream that is already using the old connection and probing values.
	/// </summary>
	[Fact]
	public void Render_WhenFetching_LocksSettingsAndAdvancedFields()
	{
		// Act: the page is not busy, but this backend is fetching/probing.
		IRenderedComponent<BackendCard> cut = RenderCard(isBusy: false, state: FetchingState());

		// Assert: the fields freeze via the derived editor lock.
		Assert.True(Settings(cut).Instance.IsBusy);
		Assert.True(Advanced(cut).Instance.FieldsBusy);
	}

	/// <summary>
	/// Verifies that a per-backend probe leaves the two page-scoped controls operable: the advanced section's
	/// header (gated on <see cref="BackendAdvanced.IsBusy"/>) and the models table (gated on
	/// <see cref="BackendModels.IsBusy"/>). The card deliberately withholds its editor lock from these — the header
	/// so the operator can open the section to watch the probe fill in, and the models table so its Cancel button
	/// stays live while <see cref="ModelListState.IsFetching"/> holds.
	/// </summary>
	[Fact]
	public void Render_WhenFetching_LeavesAdvancedHeaderAndModelsOperable()
	{
		// Act
		IRenderedComponent<BackendCard> cut = RenderCard(isBusy: false, state: FetchingState());

		// Assert: the editor lock does not reach the header toggle or the models table.
		Assert.False(Advanced(cut).Instance.IsBusy);
		Assert.False(Models(cut).Instance.IsBusy);
	}

	/// <summary>
	/// Verifies that the card renames its <see cref="BackendCard.AdvancedExpanded"/> parameter to the advanced
	/// section's <see cref="BackendAdvanced.Expanded"/>, since the two carry the same open/closed meaning under
	/// different names. Rendering with the flag set and reading the child proves the rename is wired, not dropped.
	/// </summary>
	[Fact]
	public void Render_WhenAdvancedExpanded_ForwardsToBackendAdvancedExpanded()
	{
		// Act
		IRenderedComponent<BackendCard> cut = RenderCard(advancedExpanded: true);

		// Assert: the card's AdvancedExpanded reaches the child as its Expanded.
		Assert.True(Advanced(cut).Instance.Expanded);
	}

	/// <summary>
	/// Verifies that the card feeds the settings form the catalog's provider list, so the provider picker offers
	/// exactly the providers the page's catalog publishes rather than a hard-coded set. This is the card's second
	/// rename: <see cref="BackendCard.ProviderCatalog"/>'s <c>Providers</c> becomes
	/// <see cref="BackendSettings.Providers"/>.
	/// </summary>
	[Fact]
	public void Render_WhenExpanded_ForwardsCatalogProvidersToSettings()
	{
		// Act
		IRenderedComponent<BackendCard> cut = RenderCard();

		// Assert: the settings form received exactly the catalog's descriptor list.
		Assert.Same(SampleCatalog.Providers, Settings(cut).Instance.Providers);
	}

	/// <summary>
	/// Verifies that the card forwards its <see cref="BackendCard.ModeIgnoresPins"/> flag to the models table, so
	/// a plug-and-play backend hides the pin column exactly as the page intends.
	/// </summary>
	[Fact]
	public void Render_WhenModeIgnoresPins_ForwardsToModels()
	{
		// Act
		IRenderedComponent<BackendCard> cut = RenderCard(modeIgnoresPins: true);

		// Assert
		Assert.True(Models(cut).Instance.ModeIgnoresPins);
	}

	// --- 4. Remove action ---

	/// <summary>
	/// Verifies that an expanded card renders the "Remove backend" action, so the operator can delete the backend
	/// from within its open editor.
	/// </summary>
	[Fact]
	public void Render_WhenExpanded_RendersRemoveButton()
	{
		// Act
		IRenderedComponent<BackendCard> cut = RenderCard(expanded: true);

		// Assert
		Assert.Equal("Remove backend", cut.Find("button.backend-remove").TextContent.Trim());
	}

	/// <summary>
	/// Verifies that a busy page disables the remove button, so the backend cannot be removed mid-load or
	/// mid-apply.
	/// </summary>
	[Fact]
	public void Render_WhenBusy_DisablesRemoveButton()
	{
		// Act
		IRenderedComponent<BackendCard> cut = RenderCard(isBusy: true);

		// Assert
		Assert.True(cut.Find("button.backend-remove").HasAttribute("disabled"));
	}

	/// <summary>
	/// Verifies that when the page is not busy the remove button is enabled, proving the disabled state is genuinely
	/// gated on <see cref="BackendCard.IsBusy"/> rather than always applied.
	/// </summary>
	[Fact]
	public void Render_WhenNotBusy_EnablesRemoveButton()
	{
		// Act
		IRenderedComponent<BackendCard> cut = RenderCard(isBusy: false);

		// Assert
		Assert.False(cut.Find("button.backend-remove").HasAttribute("disabled"));
	}
}
