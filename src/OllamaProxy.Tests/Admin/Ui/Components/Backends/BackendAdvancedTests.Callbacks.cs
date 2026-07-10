// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Bunit;

using OllamaProxy.Admin.Ui.Components.Backends;
using OllamaProxy.Configuration;

namespace OllamaProxy.Tests.Admin.Ui.Components.Backends;

// Callbacks: the disclosure owns no mutation, only the DOM that triggers one, split across three routes.
//
// BackendAdvanced is presentational. Its change notifications fan out by intent: clicking the header toggles the
// panel, the model-prefix field forwards its raw string on a dedicated route (so the page can normalize and
// refresh the exposed names), and every remaining plain-bound field shares one OnConfigurationChanged notifier
// (via @bind:after) so the page re-evaluates its dirty state. These tests exercise each route and assert the
// matching callback fired — proving a rewiring that drops, crosses, or blends a handler is caught:
//
//   1. Toggle: clicking the header raises OnToggle (Header).
//   2. Model-prefix route: editing the prefix raises OnModelPrefixChanged with the verbatim input and does not
//      touch the shared notifier (RawValue / DoesNotInvokeOnConfigurationChanged).
//   3. Configuration-changed fields: context length, reasoning effort, a probing toggle, and a probing knob each
//      raise OnConfigurationChanged (ContextLength / ReasoningEffort / ProbingToggle / ProbingKnob).
public sealed partial class BackendAdvancedTests
{
	// --- 1. Toggle ---

	/// <summary>
	/// Verifies that clicking the disclosure header raises <see cref="BackendAdvanced.OnToggle"/> so the page can
	/// flip its per-backend expanded set and re-render.
	/// </summary>
	[Fact]
	public void Click_Header_InvokesOnToggle()
	{
		// Arrange
		int invocations = 0;

		IRenderedComponent<BackendAdvanced> cut = RenderAdvanced(
			configure: parameters => parameters.Add(
				component => component.OnToggle,
				() => invocations++));

		// Act
		Header(cut).Click();

		// Assert
		Assert.Equal(1, invocations);
	}

	// --- 2. Model-prefix route ---

	/// <summary>
	/// Verifies that editing the model-prefix field raises <see cref="BackendAdvanced.OnModelPrefixChanged"/> with
	/// the <em>verbatim</em> input, so the page — not the component — owns the normalization (trimming,
	/// blank-to-null). The empty-string case proves the component forwards a cleared field as an empty string
	/// rather than pre-normalizing it to <see langword="null"/>.
	/// </summary>
	/// <param name="rawValue">The value typed into the field, forwarded to the callback unchanged.</param>
	/// <remarks>
	/// This is deliberate, not a leak of a component concern onto the page. The editor's draft holds
	/// <em>verbatim</em> input so the operator can type through intermediate states, and normalizing free-text
	/// fields is a boundary concern the page handlers own (see <c>SetModelPrefix</c> in <c>Backends.razor</c>).
	/// Blank-and-separator rules are then enforced by <see cref="BackendOptions"/>'s own validation at the
	/// materialize/dry-run boundary — a normalizing setter here would swallow exactly the blank state that
	/// validation exists to reject. Typed fields (context length, reasoning effort) bind directly because Blazor
	/// already maps a cleared field to <see langword="null"/>; only free text needs the page's normalization.
	/// </remarks>
	[Theory]
	[InlineData("vllm")]
	[InlineData("")]
	public void Change_ModelPrefix_InvokesOnModelPrefixChangedWithRawValue(string rawValue)
	{
		// Arrange
		string? received = null;
		bool invoked = false;

		IRenderedComponent<BackendAdvanced> cut = RenderAdvanced(
			configure: parameters => parameters.Add(
				component => component.OnModelPrefixChanged,
				value =>
				{
					received = value;
					invoked = true;
				}));

		// Act
		ModelPrefixInput(cut).Change(rawValue);

		// Assert
		Assert.True(invoked);
		Assert.Equal(rawValue, received);
	}

	/// <summary>
	/// Verifies that editing the model-prefix field does <em>not</em> raise
	/// <see cref="BackendAdvanced.OnConfigurationChanged"/>, since the prefix rides its own
	/// <see cref="BackendAdvanced.OnModelPrefixChanged"/> route (a <c>@bind:set</c> handler that already bubbles to
	/// the page) rather than the shared <c>@bind:after</c> notifier the other fields use. This pins the two routes
	/// apart so a rewire that folds the prefix into the shared notifier is caught.
	/// </summary>
	[Fact]
	public void Change_ModelPrefix_DoesNotInvokeOnConfigurationChanged()
	{
		// Arrange
		int configurationChanges = 0;

		IRenderedComponent<BackendAdvanced> cut = RenderAdvanced(
			configure: parameters => parameters.Add(
				component => component.OnConfigurationChanged,
				() => configurationChanges++));

		// Act
		ModelPrefixInput(cut).Change("vllm");

		// Assert: the shared notifier stays untouched — the prefix uses its own route.
		Assert.Equal(0, configurationChanges);
	}

	// --- 3. Configuration-changed fields ---

	/// <summary>
	/// Verifies that editing the context-length field raises <see cref="BackendAdvanced.OnConfigurationChanged"/>
	/// so the page can re-evaluate its dirty state.
	/// </summary>
	[Fact]
	public void Change_ContextLength_InvokesOnConfigurationChanged()
	{
		// Arrange
		int invocations = 0;

		IRenderedComponent<BackendAdvanced> cut = RenderAdvanced(
			configure: parameters => parameters.Add(
				component => component.OnConfigurationChanged,
				() => invocations++));

		// Act
		ContextLengthInput(cut).Change("4096");

		// Assert
		Assert.Equal(1, invocations);
	}

	/// <summary>
	/// Verifies that picking a default reasoning effort raises <see cref="BackendAdvanced.OnConfigurationChanged"/>
	/// so the page can re-evaluate its dirty state.
	/// </summary>
	[Fact]
	public void Change_ReasoningEffort_InvokesOnConfigurationChanged()
	{
		// Arrange
		int invocations = 0;

		IRenderedComponent<BackendAdvanced> cut = RenderAdvanced(
			configure: parameters => parameters.Add(
				component => component.OnConfigurationChanged,
				() => invocations++));

		// Act
		ReasoningEffortSelect(cut).Change(nameof(ReasoningEffort.High));

		// Assert
		Assert.Equal(1, invocations);
	}

	/// <summary>
	/// Verifies that toggling a capability-probing checkbox raises
	/// <see cref="BackendAdvanced.OnConfigurationChanged"/> so the page can re-evaluate its dirty state.
	/// </summary>
	[Fact]
	public void Change_ProbingToggle_InvokesOnConfigurationChanged()
	{
		// Arrange
		int invocations = 0;

		IRenderedComponent<BackendAdvanced> cut = RenderAdvanced(
			configure: parameters => parameters.Add(
				component => component.OnConfigurationChanged,
				() => invocations++));

		// Act: the Completion probe defaults on, so unchecking it is a real change.
		ProbingToggles(cut)[0].Change(false);

		// Assert
		Assert.Equal(1, invocations);
	}

	/// <summary>
	/// Verifies that editing a capability-probing knob raises <see cref="BackendAdvanced.OnConfigurationChanged"/>
	/// so the page can re-evaluate its dirty state.
	/// </summary>
	[Fact]
	public void Change_ProbingKnob_InvokesOnConfigurationChanged()
	{
		// Arrange
		int invocations = 0;

		IRenderedComponent<BackendAdvanced> cut = RenderAdvanced(
			configure: parameters => parameters.Add(
				component => component.OnConfigurationChanged,
				() => invocations++));

		// Act: the first knob is the startup timeout (default 10s); 20s is within its domain bounds.
		ProbingKnobInputs(cut)[0].Change("20");

		// Assert
		Assert.Equal(1, invocations);
	}
}
