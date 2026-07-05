// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Configuration;

namespace OllamaProxy.Tests.Configuration;

/// <summary>
/// DeepClone() companion to the validation tests in the anchor file (see <see cref="RequestTracingOptions.Validate"/>).
/// </summary>
/// <remarks>
/// RequestTracing is value-typed throughout, so the copy is verified by reflection through DeepCloneVerifier: one
/// test proves every simple property is carried into a fresh instance, the other pins the (currently empty) set
/// of reference-typed properties so a future reference member cannot be added without its own deep-copy coverage.
/// </remarks>
public sealed partial class RequestTracingOptionsTests
{
	/// <summary>
	/// Builds a tracing block with every property set to a distinctive, non-default value, so a copy that
	/// <see cref="RequestTracingOptions.DeepClone"/> forgets becomes observable.
	/// </summary>
	/// <returns>A fully populated <see cref="RequestTracingOptions"/>.</returns>
	private static RequestTracingOptions FullyPopulated() => new()
	{
		Enabled = true,
		Directory = "diagnostics",
		MaxFiles = 42,
		MaxBodyBytes = 4096,
		RedactAttachments = false
	};

	/// <summary>
	/// Verifies that <see cref="RequestTracingOptions.DeepClone"/> returns a distinct instance carrying every
	/// simple property forward unchanged.
	/// </summary>
	[Fact]
	public void DeepClone_WhenPopulated_CopiesEverySimplePropertyIntoNewInstance()
	{
		// Arrange: every property is non-default, so a forgotten copy would surface as a mismatch below.
		RequestTracingOptions populated = FullyPopulated();
		DeepCloneVerifier.AssertFixtureAssignsNonDefaultSimpleValues(populated, new RequestTracingOptions());

		// Act
		RequestTracingOptions clone = populated.DeepClone();

		// Assert: a separate instance whose every simple property equals the original's.
		Assert.NotSame(populated, clone);
		DeepCloneVerifier.AssertSimplePropertiesCopied(populated, clone);
	}

	/// <summary>
	/// Verifies that <see cref="RequestTracingOptions"/> exposes no reference-typed state property. Adding one
	/// later fails this test until that new member is given its own deep-copy coverage and listed in the expected
	/// set.
	/// </summary>
	[Fact]
	public void DeepClone_ReferenceProperties_MatchVerifiedSet()
	{
		// Act + Assert: the verified set is empty — this type stores only simple values.
		DeepCloneVerifier.AssertReferencePropertiesAre<RequestTracingOptions>();
	}
}
