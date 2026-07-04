// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Configuration;

namespace OllamaProxy.Tests.Configuration;

/// <summary>
/// Tests for <see cref="ModelRegistrationOptions.DeepClone"/>, the member-wise copy the admin editor relies on to
/// edit a registry entry without mutating the live snapshot. Every property is value-typed, so the copy is
/// verified by reflection through <see cref="DeepCloneVerifier"/>: one test proves every simple property is carried
/// into a fresh instance, the other pins the (currently empty) set of reference-typed properties so a future
/// reference member cannot be added without its own deep-copy coverage.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ModelRegistrationOptionsTests
{
	/// <summary>
	/// Builds a registry entry with every property set to a distinctive, non-default value, so a copy that
	/// <see cref="ModelRegistrationOptions.DeepClone"/> forgets becomes observable.
	/// </summary>
	/// <returns>A fully populated <see cref="ModelRegistrationOptions"/>.</returns>
	private static ModelRegistrationOptions FullyPopulated() => new()
	{
		Name = "gemma2-27b",
		UpstreamModel = "google/gemma-2-27b",
		SupportsCompletion = true,
		SupportsTools = true,
		SupportsVision = true,
		SupportsEmbeddings = true,
		ContextLength = 8192,
		ReasoningEffort = ReasoningEffort.High
	};

	/// <summary>
	/// Verifies that <see cref="ModelRegistrationOptions.DeepClone"/> returns a distinct instance carrying every
	/// simple property forward unchanged.
	/// </summary>
	[Fact]
	public void DeepClone_WhenPopulated_CopiesEverySimplePropertyIntoNewInstance()
	{
		// Arrange: every property is non-default, so a forgotten copy would surface as a mismatch below.
		ModelRegistrationOptions populated = FullyPopulated();
		DeepCloneVerifier.AssertFixtureAssignsNonDefaultSimpleValues(populated, new ModelRegistrationOptions());

		// Act
		ModelRegistrationOptions clone = populated.DeepClone();

		// Assert: a separate instance whose every simple property equals the original's.
		Assert.NotSame(populated, clone);
		DeepCloneVerifier.AssertSimplePropertiesCopied(populated, clone);
	}

	/// <summary>
	/// Verifies that <see cref="ModelRegistrationOptions"/> exposes no reference-typed state property, so the
	/// hand-written deep-copy tests the composite option types carry are unnecessary here. Adding a reference-typed
	/// property later fails this test until that new member is given its own deep-copy coverage and listed in the
	/// expected set.
	/// </summary>
	[Fact]
	public void DeepClone_ReferenceProperties_MatchVerifiedSet()
	{
		// Act + Assert: the verified set is empty — this type stores only simple values.
		DeepCloneVerifier.AssertReferencePropertiesAre<ModelRegistrationOptions>();
	}
}
