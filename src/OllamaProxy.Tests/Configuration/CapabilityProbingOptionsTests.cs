// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Configuration;

namespace OllamaProxy.Tests.Configuration;

/// <summary>
/// Tests for <see cref="CapabilityProbingOptions.DeepClone"/>, the member-wise copy the admin editor relies on to
/// edit a backend's probing toggles without mutating the live snapshot. Every property is value-typed, so the copy
/// is verified by reflection through <see cref="DeepCloneVerifier"/>: one test proves every simple property is
/// carried into a fresh instance, the other pins the (currently empty) set of reference-typed properties so a
/// future reference member cannot be added without its own deep-copy coverage.
/// </summary>
[Trait("Category", "Unit")]
public sealed class CapabilityProbingOptionsTests
{
	/// <summary>
	/// Builds a probing block with every property set to a distinctive, non-default value (within its valid
	/// range), so a copy that <see cref="CapabilityProbingOptions.DeepClone"/> forgets becomes observable.
	/// </summary>
	/// <returns>A fully populated <see cref="CapabilityProbingOptions"/>.</returns>
	private static CapabilityProbingOptions FullyPopulated() => new()
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
	};

	/// <summary>
	/// Verifies that <see cref="CapabilityProbingOptions.DeepClone"/> returns a distinct instance carrying every
	/// simple property forward unchanged.
	/// </summary>
	[Fact]
	public void DeepClone_WhenPopulated_CopiesEverySimplePropertyIntoNewInstance()
	{
		// Arrange: every property is non-default, so a forgotten copy would surface as a mismatch below.
		CapabilityProbingOptions populated = FullyPopulated();
		DeepCloneVerifier.AssertFixtureAssignsNonDefaultSimpleValues(populated, new CapabilityProbingOptions());

		// Act
		CapabilityProbingOptions clone = populated.DeepClone();

		// Assert: a separate instance whose every simple property equals the original's.
		Assert.NotSame(populated, clone);
		DeepCloneVerifier.AssertSimplePropertiesCopied(populated, clone);
	}

	/// <summary>
	/// Verifies that <see cref="CapabilityProbingOptions"/> exposes no reference-typed state property. Adding one
	/// later fails this test until that new member is given its own deep-copy coverage and listed in the expected
	/// set.
	/// </summary>
	[Fact]
	public void DeepClone_ReferenceProperties_MatchVerifiedSet()
	{
		// Act + Assert: the verified set is empty — this type stores only simple values.
		DeepCloneVerifier.AssertReferencePropertiesAre<CapabilityProbingOptions>();
	}
}
