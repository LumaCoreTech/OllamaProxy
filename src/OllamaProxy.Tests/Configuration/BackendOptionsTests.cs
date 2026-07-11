// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Configuration;

namespace OllamaProxy.Tests.Configuration;

/// <summary>
/// Tests for <see cref="BackendOptions.DeepClone"/>, the copy the admin editor loads so a browser edit cannot
/// reach back into the running proxy's configuration. The story escalates from the easy part to the structural
/// guard: first every simple property is carried into a fresh instance (verified by reflection through
/// <see cref="DeepCloneVerifier"/>), then each reference-typed member is shown to be a fresh, isolated copy
/// (<see cref="BackendOptions.Probing"/>, then the <see cref="BackendOptions.Models"/> registry), and finally the
/// reference-property set is pinned so a new reference member cannot be added without its own deep-copy coverage.
/// </summary>
/// <remarks>
/// For <see cref="BackendOptions.Validate"/> coverage, see the validation companion partial
/// (BackendOptionsTests.Validate.cs).
/// </remarks>
[Trait("Category", "Unit")]
public sealed partial class BackendOptionsTests
{
	/// <summary>
	/// Builds a backend with every property set to a distinctive, non-default value — including a mutated
	/// <see cref="BackendOptions.Probing"/> block and a richly populated <see cref="BackendOptions.Models"/> entry
	/// — so a copy that <see cref="BackendOptions.DeepClone"/> forgets (or shares instead of clones) becomes
	/// observable.
	/// </summary>
	/// <returns>A fully populated <see cref="BackendOptions"/>.</returns>
	private static BackendOptions FullyPopulated() => new()
	{
		BaseUrl = "https://api.example.com/v1",
		ProviderType = "vllm",
		ApiKey = "sk-abcdefgh",
		Mode = OperatingMode.Hybrid,
		ContextLength = 8192,
		ModelPrefix = "vllm",
		ReasoningEffort = ReasoningEffort.High,

		// Nested initializers mutate the default Probing instance and append to the default Models list, so the
		// init-only members never need reassigning.
		Probing =
		{
			ProbeCompletion = false,
			ProbeTools = false,
			ProbeVision = false,
			ProbeEmbeddings = false,
			TimeoutSeconds = 25,
			InteractiveTimeoutSeconds = 90,
			MaxProbeRetries = 7,
			RetryBaseDelaySeconds = 9,
			MaxConcurrentProbes = 5
		},
		Models =
		{
			new ModelRegistrationOptions
			{
				Name = "gemma2-27b",
				UpstreamModel = "google/gemma-2-27b",
				SupportsCompletion = true,
				SupportsTools = true,
				SupportsVision = true,
				SupportsEmbeddings = true,
				ContextLength = 8192,
				ReasoningEffort = ReasoningEffort.High
			}
		}
	};

	/// <summary>
	/// Verifies that <see cref="BackendOptions.DeepClone"/> returns a distinct instance carrying every simple
	/// property forward unchanged.
	/// </summary>
	[Fact]
	public void DeepClone_WhenPopulated_CopiesEverySimplePropertyIntoNewInstance()
	{
		// Arrange: every simple property is non-default, so a forgotten copy would surface as a mismatch below.
		BackendOptions populated = FullyPopulated();
		DeepCloneVerifier.AssertFixtureAssignsNonDefaultSimpleValues(populated, new BackendOptions());

		// Act
		BackendOptions clone = populated.DeepClone();

		// Assert: a separate instance whose every simple property equals the original's.
		Assert.NotSame(populated, clone);
		DeepCloneVerifier.AssertSimplePropertiesCopied(populated, clone);
	}

	/// <summary>
	/// Verifies that <see cref="BackendOptions.DeepClone"/> deep-copies <see cref="BackendOptions.Probing"/> into a
	/// fresh instance, so editing the clone's probing toggles cannot mutate the original.
	/// </summary>
	[Fact]
	public void DeepClone_WhenProbingPopulated_ProducesIndependentCopy()
	{
		// Arrange
		BackendOptions populated = FullyPopulated();

		// Act
		BackendOptions clone = populated.DeepClone();

		// Assert: a fresh Probing instance carrying every field forward.
		Assert.NotSame(populated.Probing, clone.Probing);
		DeepCloneVerifier.AssertSimplePropertiesCopied(populated.Probing, clone.Probing);

		// Editing the clone's probing must not reach back into the original.
		clone.Probing.MaxConcurrentProbes = 1;
		Assert.Equal(5, populated.Probing.MaxConcurrentProbes);
	}

	/// <summary>
	/// Verifies that <see cref="BackendOptions.DeepClone"/> deep-copies the <see cref="BackendOptions.Models"/>
	/// registry into a fresh list of fresh entries, so adding, removing, or editing a clone row cannot mutate the
	/// original registry.
	/// </summary>
	[Fact]
	public void DeepClone_WhenModelsPopulated_ProducesIndependentRegistry()
	{
		// Arrange
		BackendOptions populated = FullyPopulated();

		// Act
		BackendOptions clone = populated.DeepClone();

		// Assert: a fresh list holding a fresh entry whose every field equals the original entry's.
		Assert.NotSame(populated.Models, clone.Models);
		ModelRegistrationOptions original = Assert.Single(populated.Models);
		ModelRegistrationOptions copy = Assert.Single(clone.Models);
		Assert.NotSame(original, copy);
		DeepCloneVerifier.AssertSimplePropertiesCopied(original, copy);

		// Editing a copied entry must not mutate the original entry.
		copy.Name = "edited";
		Assert.Equal("gemma2-27b", original.Name);

		// Adding to the copied list must not grow the original list.
		clone.Models.Add(new ModelRegistrationOptions { Name = "extra" });
		Assert.Single(populated.Models);
	}

	/// <summary>
	/// Verifies that the reference-typed state properties of <see cref="BackendOptions"/> are exactly
	/// <see cref="BackendOptions.Models"/> and <see cref="BackendOptions.Probing"/> — the two members the
	/// independence tests above clone by hand. A reference-typed property added later fails this test until it too
	/// is given dedicated deep-copy coverage and listed here.
	/// </summary>
	[Fact]
	public void DeepClone_ReferenceProperties_MatchVerifiedSet()
	{
		// Act + Assert: only Models and Probing hold reference state; both are verified above.
		DeepCloneVerifier.AssertReferencePropertiesAre<BackendOptions>(
			nameof(BackendOptions.Models),
			nameof(BackendOptions.Probing));
	}
}
