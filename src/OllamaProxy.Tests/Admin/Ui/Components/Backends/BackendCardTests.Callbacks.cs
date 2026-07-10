// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Bunit;

using OllamaProxy.Admin.Reconciliation;
using OllamaProxy.Admin.Ui.Components.Backends;
using OllamaProxy.Configuration;

namespace OllamaProxy.Tests.Admin.Ui.Components.Backends;

// Callbacks: the card owns two events and forwards the rest verbatim to its children.
//
// BackendCard is a composite: its whole job is to route the page's callbacks to the right child. So its callback
// contract splits in two. The two events it owns outright — the header toggle and the remove action — are driven
// by its own DOM, so those tests click the real element. Every other callback is a pass-through: the card hands
// its EventCallback parameter straight to a child (OnConfigurationChanged goes to two). Those tests raise the
// *child's* forwarded parameter and assert the page handler the card was given observed it — with the payload
// intact where one flows. Raising the child's parameter (rather than reaching into the child's DOM, which the
// child's own suite covers) tests exactly the wire the card owns: a dropped forward leaves the child with a
// default no-op callback and the page handler never fires; a crossed forward fires the wrong page handler.
//
//   1. Owned events: clicking the header raises OnToggleExpand; clicking Remove raises OnRemove
//      (Click_Header / Click_RemoveButton).
//
//   2. Forwarded to BackendSettings: provider-type, mode, and the shared configuration-changed notifier
//      (OnProviderTypeChanged / OnModeChanged / OnConfigurationChanged … WhenSettingsRaises).
//
//   3. Forwarded to BackendAdvanced: the advanced-toggle (renamed from the child's OnToggle), the model-prefix
//      route, and the same shared configuration-changed notifier's second wire
//      (OnAdvancedToggle / OnModelPrefixChanged / OnConfigurationChanged … WhenAdvancedRaises).
//
//   4. Forwarded to BackendModels: the three header/streaming actions, the three model-scoped row actions, and
//      the four pinned-field write-backs (…WhenModelsRaises).
public sealed partial class BackendCardTests
{
	/// <summary>
	/// A single reconciled row used as the payload for the model-scoped forwards. It is a discovered (unpinned)
	/// row; the card forwards it unchanged, so the tests assert the very same instance (or, for the edit records,
	/// the same wrapped value) reaches the page handler.
	/// </summary>
	private static readonly ReconciledModel SampleModel = new(
		Name: "llama3",
		ExposedName: "llama3",
		BackendName: "openai-prod",
		UpstreamModel: "llama3",
		Capabilities: null,
		ContextLength: null,
		State: ReconciledModelState.Discovered);

	// --- 1. Owned events ---

	/// <summary>
	/// Verifies that clicking the card header raises <see cref="BackendCard.OnToggleExpand"/>, so the page can flip
	/// which card is expanded. This is a card-owned event, so the test drives the real header button.
	/// </summary>
	[Fact]
	public void Click_Header_InvokesOnToggleExpand()
	{
		// Arrange
		int invocations = 0;

		IRenderedComponent<BackendCard> cut = RenderCard(
			configure: parameters => parameters.Add(
				component => component.OnToggleExpand,
				() => invocations++));

		// Act
		Header(cut).Click();

		// Assert
		Assert.Equal(1, invocations);
	}

	/// <summary>
	/// Verifies that clicking the "Remove backend" button raises <see cref="BackendCard.OnRemove"/>, so the page
	/// can drop the backend from the draft. This is a card-owned event, so the test drives the real button.
	/// </summary>
	[Fact]
	public void Click_RemoveButton_InvokesOnRemove()
	{
		// Arrange
		int invocations = 0;

		IRenderedComponent<BackendCard> cut = RenderCard(
			configure: parameters => parameters.Add(
				component => component.OnRemove,
				() => invocations++));

		// Act
		cut.Find("button.backend-remove").Click();

		// Assert
		Assert.Equal(1, invocations);
	}

	// --- 2. Forwarded to BackendSettings ---

	/// <summary>
	/// Verifies that the card forwards <see cref="BackendSettings.OnProviderTypeChanged"/> to its own
	/// <see cref="BackendCard.OnProviderTypeChanged"/>, passing the selected provider type through unchanged.
	/// </summary>
	[Fact]
	public async Task OnProviderTypeChanged_WhenSettingsRaises_ForwardsSelectedType()
	{
		// Arrange
		string? received = null;
		bool invoked = false;

		IRenderedComponent<BackendCard> cut = RenderCard(
			configure: parameters => parameters.Add(
				component => component.OnProviderTypeChanged,
				value =>
				{
					received = value;
					invoked = true;
				}));

		// Act
		await cut.InvokeAsync(() => Settings(cut).Instance.OnProviderTypeChanged.InvokeAsync("vllm"));

		// Assert
		Assert.True(invoked);
		Assert.Equal("vllm", received);
	}

	/// <summary>
	/// Verifies that the card forwards <see cref="BackendSettings.OnModeChanged"/> to its own
	/// <see cref="BackendCard.OnModeChanged"/>, passing the selected operating mode through unchanged.
	/// </summary>
	[Fact]
	public async Task OnModeChanged_WhenSettingsRaises_ForwardsSelectedMode()
	{
		// Arrange
		OperatingMode? received = null;
		bool invoked = false;

		IRenderedComponent<BackendCard> cut = RenderCard(
			configure: parameters => parameters.Add(
				component => component.OnModeChanged,
				value =>
				{
					received = value;
					invoked = true;
				}));

		// Act
		await cut.InvokeAsync(() => Settings(cut).Instance.OnModeChanged.InvokeAsync(OperatingMode.Hybrid));

		// Assert
		Assert.True(invoked);
		Assert.Equal(OperatingMode.Hybrid, received);
	}

	/// <summary>
	/// Verifies that the card forwards <see cref="BackendSettings.OnConfigurationChanged"/> to its own
	/// <see cref="BackendCard.OnConfigurationChanged"/>, so a settings edit re-evaluates the page's dirty state.
	/// This is one of the notifier's two wires; the advanced wire is covered separately.
	/// </summary>
	[Fact]
	public async Task OnConfigurationChanged_WhenSettingsRaises_Forwards()
	{
		// Arrange
		int invocations = 0;

		IRenderedComponent<BackendCard> cut = RenderCard(
			configure: parameters => parameters.Add(
				component => component.OnConfigurationChanged,
				() => invocations++));

		// Act
		await cut.InvokeAsync(() => Settings(cut).Instance.OnConfigurationChanged.InvokeAsync());

		// Assert
		Assert.Equal(1, invocations);
	}

	// --- 3. Forwarded to BackendAdvanced ---

	/// <summary>
	/// Verifies that the card wires the advanced section's <see cref="BackendAdvanced.OnToggle"/> to its own
	/// <see cref="BackendCard.OnAdvancedToggle"/> — the card's one callback rename — so the page can flip the
	/// per-backend advanced-expanded set.
	/// </summary>
	[Fact]
	public async Task OnAdvancedToggle_WhenAdvancedRaises_Forwards()
	{
		// Arrange
		int invocations = 0;

		IRenderedComponent<BackendCard> cut = RenderCard(
			configure: parameters => parameters.Add(
				component => component.OnAdvancedToggle,
				() => invocations++));

		// Act
		await cut.InvokeAsync(() => Advanced(cut).Instance.OnToggle.InvokeAsync());

		// Assert
		Assert.Equal(1, invocations);
	}

	/// <summary>
	/// Verifies that the card forwards <see cref="BackendAdvanced.OnModelPrefixChanged"/> to its own
	/// <see cref="BackendCard.OnModelPrefixChanged"/>, passing the raw prefix value through unchanged.
	/// </summary>
	[Fact]
	public async Task OnModelPrefixChanged_WhenAdvancedRaises_ForwardsRawValue()
	{
		// Arrange
		string? received = null;
		bool invoked = false;

		IRenderedComponent<BackendCard> cut = RenderCard(
			configure: parameters => parameters.Add(
				component => component.OnModelPrefixChanged,
				value =>
				{
					received = value;
					invoked = true;
				}));

		// Act
		await cut.InvokeAsync(() => Advanced(cut).Instance.OnModelPrefixChanged.InvokeAsync("vllm"));

		// Assert
		Assert.True(invoked);
		Assert.Equal("vllm", received);
	}

	/// <summary>
	/// Verifies that the card forwards <see cref="BackendAdvanced.OnConfigurationChanged"/> to its own
	/// <see cref="BackendCard.OnConfigurationChanged"/> — the notifier's second wire — so an advanced edit
	/// re-evaluates the page's dirty state just as a settings edit does.
	/// </summary>
	[Fact]
	public async Task OnConfigurationChanged_WhenAdvancedRaises_Forwards()
	{
		// Arrange
		int invocations = 0;

		IRenderedComponent<BackendCard> cut = RenderCard(
			configure: parameters => parameters.Add(
				component => component.OnConfigurationChanged,
				() => invocations++));

		// Act
		await cut.InvokeAsync(() => Advanced(cut).Instance.OnConfigurationChanged.InvokeAsync());

		// Assert
		Assert.Equal(1, invocations);
	}

	// --- 4. Forwarded to BackendModels ---

	/// <summary>
	/// Verifies that the card forwards <see cref="BackendModels.OnRefreshModels"/> to its own
	/// <see cref="BackendCard.OnRefreshModels"/>, so the page can trigger a no-probe re-fetch.
	/// </summary>
	[Fact]
	public async Task OnRefreshModels_WhenModelsRaises_Forwards()
	{
		// Arrange
		int invocations = 0;

		IRenderedComponent<BackendCard> cut = RenderCard(
			configure: parameters => parameters.Add(
				component => component.OnRefreshModels,
				() => invocations++));

		// Act
		await cut.InvokeAsync(() => Models(cut).Instance.OnRefreshModels.InvokeAsync());

		// Assert
		Assert.Equal(1, invocations);
	}

	/// <summary>
	/// Verifies that the card forwards <see cref="BackendModels.OnProbeModels"/> to its own
	/// <see cref="BackendCard.OnProbeModels"/>, so the page can trigger a streaming capability probe.
	/// </summary>
	[Fact]
	public async Task OnProbeModels_WhenModelsRaises_Forwards()
	{
		// Arrange
		int invocations = 0;

		IRenderedComponent<BackendCard> cut = RenderCard(
			configure: parameters => parameters.Add(
				component => component.OnProbeModels,
				() => invocations++));

		// Act
		await cut.InvokeAsync(() => Models(cut).Instance.OnProbeModels.InvokeAsync());

		// Assert
		Assert.Equal(1, invocations);
	}

	/// <summary>
	/// Verifies that the card forwards <see cref="BackendModels.OnCancelProbe"/> to its own
	/// <see cref="BackendCard.OnCancelProbe"/>, so the page can signal a running probe's cancellation source.
	/// </summary>
	[Fact]
	public async Task OnCancelProbe_WhenModelsRaises_Forwards()
	{
		// Arrange
		int invocations = 0;

		IRenderedComponent<BackendCard> cut = RenderCard(
			configure: parameters => parameters.Add(
				component => component.OnCancelProbe,
				() => invocations++));

		// Act
		await cut.InvokeAsync(() => Models(cut).Instance.OnCancelProbe.InvokeAsync());

		// Assert
		Assert.Equal(1, invocations);
	}

	/// <summary>
	/// Verifies that the card forwards <see cref="BackendModels.OnTogglePin"/> to its own
	/// <see cref="BackendCard.OnTogglePin"/>, passing the toggled row through unchanged.
	/// </summary>
	[Fact]
	public async Task OnTogglePin_WhenModelsRaises_ForwardsModel()
	{
		// Arrange
		ReconciledModel? captured = null;

		IRenderedComponent<BackendCard> cut = RenderCard(
			configure: parameters => parameters.Add(
				component => component.OnTogglePin,
				model => captured = model));

		// Act
		await cut.InvokeAsync(() => Models(cut).Instance.OnTogglePin.InvokeAsync(SampleModel));

		// Assert: the very same row instance, forwarded verbatim.
		Assert.Same(SampleModel, captured);
	}

	/// <summary>
	/// Verifies that the card forwards <see cref="BackendModels.OnAddVariant"/> to its own
	/// <see cref="BackendCard.OnAddVariant"/>, passing the source row through unchanged.
	/// </summary>
	[Fact]
	public async Task OnAddVariant_WhenModelsRaises_ForwardsModel()
	{
		// Arrange
		ReconciledModel? captured = null;

		IRenderedComponent<BackendCard> cut = RenderCard(
			configure: parameters => parameters.Add(
				component => component.OnAddVariant,
				model => captured = model));

		// Act
		await cut.InvokeAsync(() => Models(cut).Instance.OnAddVariant.InvokeAsync(SampleModel));

		// Assert
		Assert.Same(SampleModel, captured);
	}

	/// <summary>
	/// Verifies that the card forwards <see cref="BackendModels.OnToggleModelDetails"/> to its own
	/// <see cref="BackendCard.OnToggleModelDetails"/>, passing the row whose detail panel toggled through unchanged.
	/// </summary>
	[Fact]
	public async Task OnToggleModelDetails_WhenModelsRaises_ForwardsModel()
	{
		// Arrange
		ReconciledModel? captured = null;

		IRenderedComponent<BackendCard> cut = RenderCard(
			configure: parameters => parameters.Add(
				component => component.OnToggleModelDetails,
				model => captured = model));

		// Act
		await cut.InvokeAsync(() => Models(cut).Instance.OnToggleModelDetails.InvokeAsync(SampleModel));

		// Assert
		Assert.Same(SampleModel, captured);
	}

	/// <summary>
	/// Verifies that the card forwards <see cref="BackendModels.OnSetPinnedName"/> to its own
	/// <see cref="BackendCard.OnSetPinnedName"/>, passing the name edit through unchanged.
	/// </summary>
	[Fact]
	public async Task OnSetPinnedName_WhenModelsRaises_ForwardsEdit()
	{
		// Arrange
		PinnedNameEdit? captured = null;
		var edit = new PinnedNameEdit(SampleModel, "custom-name");

		IRenderedComponent<BackendCard> cut = RenderCard(
			configure: parameters => parameters.Add(
				component => component.OnSetPinnedName,
				value => captured = value));

		// Act
		await cut.InvokeAsync(() => Models(cut).Instance.OnSetPinnedName.InvokeAsync(edit));

		// Assert
		Assert.Equal(edit, captured);
	}

	/// <summary>
	/// Verifies that the card forwards <see cref="BackendModels.OnSetPinnedReasoning"/> to its own
	/// <see cref="BackendCard.OnSetPinnedReasoning"/>, passing the reasoning-effort edit through unchanged.
	/// </summary>
	[Fact]
	public async Task OnSetPinnedReasoning_WhenModelsRaises_ForwardsEdit()
	{
		// Arrange
		PinnedReasoningEdit? captured = null;
		var edit = new PinnedReasoningEdit(SampleModel, ReasoningEffort.High);

		IRenderedComponent<BackendCard> cut = RenderCard(
			configure: parameters => parameters.Add(
				component => component.OnSetPinnedReasoning,
				value => captured = value));

		// Act
		await cut.InvokeAsync(() => Models(cut).Instance.OnSetPinnedReasoning.InvokeAsync(edit));

		// Assert
		Assert.Equal(edit, captured);
	}

	/// <summary>
	/// Verifies that the card forwards <see cref="BackendModels.OnSetContextOverride"/> to its own
	/// <see cref="BackendCard.OnSetContextOverride"/>, passing the context-length edit through unchanged.
	/// </summary>
	[Fact]
	public async Task OnSetContextOverride_WhenModelsRaises_ForwardsEdit()
	{
		// Arrange
		PinnedContextEdit? captured = null;
		var edit = new PinnedContextEdit(SampleModel, 4096);

		IRenderedComponent<BackendCard> cut = RenderCard(
			configure: parameters => parameters.Add(
				component => component.OnSetContextOverride,
				value => captured = value));

		// Act
		await cut.InvokeAsync(() => Models(cut).Instance.OnSetContextOverride.InvokeAsync(edit));

		// Assert
		Assert.Equal(edit, captured);
	}

	/// <summary>
	/// Verifies that the card forwards <see cref="BackendModels.OnSetPinnedCapability"/> to its own
	/// <see cref="BackendCard.OnSetPinnedCapability"/>, passing the capability edit through unchanged.
	/// </summary>
	[Fact]
	public async Task OnSetPinnedCapability_WhenModelsRaises_ForwardsEdit()
	{
		// Arrange
		PinnedCapabilityEdit? captured = null;
		var edit = new PinnedCapabilityEdit(SampleModel, PinnedCapability.Tools, Value: true);

		IRenderedComponent<BackendCard> cut = RenderCard(
			configure: parameters => parameters.Add(
				component => component.OnSetPinnedCapability,
				value => captured = value));

		// Act
		await cut.InvokeAsync(() => Models(cut).Instance.OnSetPinnedCapability.InvokeAsync(edit));

		// Assert
		Assert.Equal(edit, captured);
	}
}
