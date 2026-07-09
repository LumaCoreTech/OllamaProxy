// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Bunit;

using OllamaProxy.Admin.Reconciliation;
using OllamaProxy.Admin.Ui.Components.Backends;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Tests.Admin.Ui.Components.Backends;

// Callbacks: every operator action raises the EventCallback the page supplies.
//
// BackendModels is presentational — it owns no mutation, only the DOM that triggers one. These tests click or
// toggle each control and assert the matching callback fired (and, for the model-scoped callbacks, that the right
// row was passed), so a rewiring that drops or crosses a handler is caught:
//
//   1. Header actions: Refresh and Probe (Refresh / Probe).
//   2. Streaming action: Cancel, available only while the progress banner is shown (Cancel).
//   3. Row actions: toggle-pin, add-variant, and toggle-details, each carrying its own model (TogglePin /
//      AddVariant / ToggleDetails).
public sealed partial class BackendModelsTests
{
	// --- 1. Header actions ---

	/// <summary>
	/// Verifies that clicking Refresh raises <see cref="BackendModels.OnRefreshModels"/> so the page can trigger a
	/// no-probe re-fetch.
	/// </summary>
	[Fact]
	public void Click_Refresh_InvokesOnRefreshModels()
	{
		// Arrange
		int invocations = 0;

		IRenderedComponent<BackendModels> cut = RenderModels(
			configure: parameters => parameters.Add(component => component.OnRefreshModels, () => invocations++));

		// Act
		HeaderButtons(cut)[0].Click();

		// Assert
		Assert.Equal(1, invocations);
	}

	/// <summary>
	/// Verifies that clicking Probe raises <see cref="BackendModels.OnProbeModels"/> so the page can trigger a
	/// streaming capability probe.
	/// </summary>
	[Fact]
	public void Click_Probe_InvokesOnProbeModels()
	{
		// Arrange
		int invocations = 0;

		IRenderedComponent<BackendModels> cut = RenderModels(
			configure: parameters => parameters.Add(component => component.OnProbeModels, () => invocations++));

		// Act
		HeaderButtons(cut)[1].Click();

		// Assert
		Assert.Equal(1, invocations);
	}

	// --- 2. Streaming action ---

	/// <summary>
	/// Verifies that clicking Cancel during a streaming probe raises <see cref="BackendModels.OnCancelProbe"/> so the
	/// page can signal the probe's cancellation source.
	/// </summary>
	[Fact]
	public void Click_Cancel_WhenStreaming_InvokesOnCancelProbe()
	{
		// Arrange: the Cancel button exists only while the streaming progress banner is shown.
		int invocations = 0;

		IRenderedComponent<BackendModels> cut = RenderModels(
			state: StreamingState(),
			configure: parameters => parameters.Add(component => component.OnCancelProbe, () => invocations++));

		// Act
		cut.Find("button.backend-probe-cancel").Click();

		// Assert
		Assert.Equal(1, invocations);
	}

	// --- 3. Row actions ---

	/// <summary>
	/// Verifies that toggling a discovered row's pin checkbox raises <see cref="BackendModels.OnTogglePin"/> with
	/// that row's model, so the page can promote it into the registry.
	/// </summary>
	[Fact]
	public void Change_PinCheckbox_InvokesOnTogglePinWithModel()
	{
		// Arrange
		ReconciledModel? captured = null;

		IRenderedComponent<BackendModels> cut = RenderModels(
			reconciliation: Reconciliation(DiscoveredModel(name: "llama3")),
			configure: parameters => parameters.Add(component => component.OnTogglePin, model => captured = model));

		// Act
		cut.Find("tbody input[type=checkbox]").Change(true);

		// Assert
		Assert.NotNull(captured);
		Assert.Equal("llama3", captured.Name);
	}

	/// <summary>
	/// Verifies that clicking "+ variant" on a pinned row raises <see cref="BackendModels.OnAddVariant"/> with that
	/// row's model, so the page can clone the pin under a new name.
	/// </summary>
	[Fact]
	public void Click_AddVariant_InvokesOnAddVariantWithModel()
	{
		// Arrange
		ReconciledModel? captured = null;

		IRenderedComponent<BackendModels> cut = RenderModels(
			reconciliation: Reconciliation(AvailablePin(name: "gpt-4")),
			configure: parameters => parameters.Add(component => component.OnAddVariant, model => captured = model));

		// Act
		cut.Find("button.model-add-variant").Click();

		// Assert
		Assert.NotNull(captured);
		Assert.Equal("gpt-4", captured.Name);
	}

	/// <summary>
	/// Verifies that clicking a model's detail toggle raises <see cref="BackendModels.OnToggleModelDetails"/> with
	/// that row's model, so the page can flip its expanded-detail set.
	/// </summary>
	[Fact]
	public void Click_DetailToggle_InvokesOnToggleModelDetailsWithModel()
	{
		// Arrange: metadata is required for the toggle to render.
		ReconciledModel? captured = null;
		var metadata = new ProviderModelMetadata(DisplayName: "Llama 3 Instruct");

		IRenderedComponent<BackendModels> cut = RenderModels(
			reconciliation: Reconciliation(DiscoveredModel(name: "llama3", metadata: metadata)),
			configure: parameters => parameters.Add(
				component => component.OnToggleModelDetails,
				model => captured = model));

		// Act
		cut.Find("button.model-details-toggle").Click();

		// Assert
		Assert.NotNull(captured);
		Assert.Equal("llama3", captured.Name);
	}
}
