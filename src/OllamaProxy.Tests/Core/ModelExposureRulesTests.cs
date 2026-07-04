// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Configuration;
using OllamaProxy.Core;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Tests.Core;

/// <summary>
/// Tests for <see cref="ModelExposureRules"/>, the shared rules that turn a backend's raw model information
/// into the attributes a client sees. Because startup catalog assembly and admin-time reconciliation both
/// call these helpers, the tests pin their behavior so the admin preview cannot drift from runtime exposure.
/// The file is organized by member:
/// <list type="number">
///     <item>
///         <description>
///         <see cref="ModelExposureRules.ApplyClientFacingPrefix"/> — a blank prefix keeps the bare name;
///         a set prefix produces <c>prefix/model</c>.
///         </description>
///     </item>
///     <item>
///         <description>
///         <see cref="ModelExposureRules.ResolveEffectiveContextWindow"/> — the strict override, reported,
///         backend-default precedence, and the <see langword="null"/> result when no source supplies one.
///         </description>
///     </item>
///     <item>
///         <description>
///         <see cref="ModelExposureRules.ResolveRegisteredCapabilities"/> — completion defaults on, the
///         additive flags default off, and any pinned flag marks the source configured.
///         </description>
///     </item>
/// </list>
/// </summary>
[Trait("Category", "Unit")]
public sealed class ModelExposureRulesTests
{
	#region ApplyClientFacingPrefix()

	/// <summary>
	/// Cases pairing a configured prefix with the bare model name and the expected client-facing name. A
	/// <see langword="null"/>, empty, or whitespace prefix leaves the bare name untouched; any real prefix
	/// is joined with a slash.
	/// </summary>
	public static TheoryData<string, string?, string, string> PrefixCases => new()
	{
		// A null prefix (single-backend deployments) keeps the short bare name.
		{ "null prefix keeps bare name", null, "llama3", "llama3" },

		// An empty prefix is treated the same as none.
		{ "empty prefix keeps bare name", "", "llama3", "llama3" },

		// A whitespace-only prefix is blank after trimming intent, so it is treated as none.
		{ "whitespace prefix keeps bare name", "   ", "llama3", "llama3" },

		// A real prefix is joined to the bare name with a single slash.
		{ "set prefix produces prefix/model", "pool-a", "llama3", "pool-a/llama3" }
	};

	/// <summary>
	/// Verifies that <see cref="ModelExposureRules.ApplyClientFacingPrefix"/> keeps the bare name when the
	/// prefix is blank and otherwise produces <c>prefix/model</c>.
	/// </summary>
	/// <param name="scenario">A human-readable label for the case.</param>
	/// <param name="modelPrefix">The configured backend prefix, or <see langword="null"/> when none is set.</param>
	/// <param name="bareModelName">The bare model name to expose.</param>
	/// <param name="expected">The expected client-facing name.</param>
	[Theory]
	[MemberData(nameof(PrefixCases))]
	public void ApplyClientFacingPrefix_WhenPrefixVaries_AppliesOrKeepsBareName(
		string  scenario,
		string? modelPrefix,
		string  bareModelName,
		string  expected)
	{
		_ = scenario;

		// Act
		string result = ModelExposureRules.ApplyClientFacingPrefix(modelPrefix, bareModelName);

		// Assert
		Assert.Equal(expected, result);
	}

	#endregion

	#region ResolveEffectiveContextWindow()

	/// <summary>
	/// Cases exercising the strict override, reported, backend-default precedence. The explicit per-model
	/// override wins first, the backend's reported value second, the operator default last, and the result
	/// is <see langword="null"/> when no source supplies a window.
	/// </summary>
	public static TheoryData<string, long?, long?, long?, long?> ContextWindowCases => new()
	{
		// The explicit override wins even when every other source is present.
		{ "override wins over reported and default", 4096, 8192, 2048, 4096 },

		// The override still wins when it is the only source.
		{ "override wins when others absent", 4096, null, null, 4096 },

		// With no override, the backend's reported value is used.
		{ "reported used when no override", null, 8192, 2048, 8192 },

		// The backend default only fills the gap when neither override nor reported is present.
		{ "backend default fills the gap", null, null, 2048, 2048 },

		// No source supplies a window, so the result is null rather than a throw.
		{ "null when no source supplies a window", null, null, null, null }
	};

	/// <summary>
	/// Verifies that <see cref="ModelExposureRules.ResolveEffectiveContextWindow"/> applies the
	/// override → reported → backend-default precedence and returns <see langword="null"/> when no source
	/// supplies a window.
	/// </summary>
	/// <param name="scenario">A human-readable label for the case.</param>
	/// <param name="explicitOverride">The explicit per-model override, when set.</param>
	/// <param name="reported">The context length the backend reported, when any.</param>
	/// <param name="backendDefault">The operator-configured backend default, when any.</param>
	/// <param name="expected">The expected effective context window, or <see langword="null"/>.</param>
	[Theory]
	[MemberData(nameof(ContextWindowCases))]
	public void ResolveEffectiveContextWindow_WhenSourcesVary_AppliesPrecedence(
		string scenario,
		long?  explicitOverride,
		long?  reported,
		long?  backendDefault,
		long?  expected)
	{
		_ = scenario;

		// Act
		long? result = ModelExposureRules.ResolveEffectiveContextWindow(explicitOverride, reported, backendDefault);

		// Assert
		Assert.Equal(expected, result);
	}

	#endregion

	#region ResolveRegisteredCapabilities()

	/// <summary>
	/// Cases mapping a registry entry's four nullable capability flags to the resolved capabilities. Completion
	/// defaults to <see langword="true"/>, the additive flags default to <see langword="false"/>, and the source
	/// is <see cref="CapabilitySource.Configured"/> whenever any flag is pinned and
	/// <see cref="CapabilitySource.Default"/> only when every flag is unset.
	/// </summary>
	public static TheoryData<string, bool?, bool?, bool?, bool?, bool, bool, bool, bool, CapabilitySource>
		CapabilityCases => new()
	{
		// Nothing pinned: completion defaults on, the rest off, and the source is the conservative default.
		{
			"all unset defaults to completion-only", null, null, null, null, true, false, false, false,
			CapabilitySource.Default
		},

		// A single pinned flag (even one matching the default) marks the whole set configured.
		{
			"explicit completion flips source to configured", true, null, null, null, true, false, false, false,
			CapabilitySource.Configured
		},

		// An embedding-only pin: completion is explicitly off, embeddings on.
		{
			"completion off with embeddings on", false, null, null, true, false, false, false, true,
			CapabilitySource.Configured
		},

		// Tools pinned on: completion keeps its default, tools carried through.
		{
			"tools pinned on over default completion", null, true, null, null, true, true, false, false,
			CapabilitySource.Configured
		},

		// Vision pinned on: completion keeps its default, vision carried through.
		{
			"vision pinned on over default completion", null, null, true, null, true, false, true, false,
			CapabilitySource.Configured
		},

		// Embeddings pinned on additively, alongside the default completion baseline.
		{
			"embeddings pinned on over default completion", null, null, null, true, true, false, false, true,
			CapabilitySource.Configured
		},

		// Every flag pinned on: all carried through, source configured.
		{
			"all flags pinned on", true, true, true, true, true, true, true, true,
			CapabilitySource.Configured
		}
	};

	/// <summary>
	/// Verifies that <see cref="ModelExposureRules.ResolveRegisteredCapabilities"/> resolves each nullable flag
	/// over its baseline (completion on, additive flags off) and reports the source as configured whenever any
	/// flag is pinned.
	/// </summary>
	/// <param name="scenario">A human-readable label for the case.</param>
	/// <param name="completion">The pinned completion flag, or <see langword="null"/> when unset.</param>
	/// <param name="tools">The pinned tools flag, or <see langword="null"/> when unset.</param>
	/// <param name="vision">The pinned vision flag, or <see langword="null"/> when unset.</param>
	/// <param name="embeddings">The pinned embeddings flag, or <see langword="null"/> when unset.</param>
	/// <param name="expectedCompletion">The expected resolved completion flag.</param>
	/// <param name="expectedTools">The expected resolved tools flag.</param>
	/// <param name="expectedVision">The expected resolved vision flag.</param>
	/// <param name="expectedEmbeddings">The expected resolved embeddings flag.</param>
	/// <param name="expectedSource">The expected capability provenance.</param>
	[Theory]
	[MemberData(nameof(CapabilityCases))]
	public void ResolveRegisteredCapabilities_WhenFlagsVary_ResolvesCapabilitiesAndSource(
		string           scenario,
		bool?            completion,
		bool?            tools,
		bool?            vision,
		bool?            embeddings,
		bool             expectedCompletion,
		bool             expectedTools,
		bool             expectedVision,
		bool             expectedEmbeddings,
		CapabilitySource expectedSource)
	{
		_ = scenario;

		// Arrange
		ModelRegistrationOptions registration = new()
		{
			Name = "model",
			SupportsCompletion = completion,
			SupportsTools = tools,
			SupportsVision = vision,
			SupportsEmbeddings = embeddings
		};

		// Act
		ModelCapabilities result = ModelExposureRules.ResolveRegisteredCapabilities(registration);

		// Assert: full record equality pins every functional flag, the provenance, and the (unset) inconclusive
		// overlay in a single comparison.
		ModelCapabilities expected = new(
			expectedCompletion,
			expectedTools,
			expectedVision,
			expectedEmbeddings,
			expectedSource);
		Assert.Equal(expected, result);
	}

	#endregion
}
