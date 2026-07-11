// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Admin.Reconciliation;
using OllamaProxy.Admin.Ui.Components.Backends;
using OllamaProxy.Configuration;

namespace OllamaProxy.Tests.Admin.Ui.Components.Backends;

/// <summary>
/// Tests for the four write-back payload records of the <see cref="BackendModels"/> editor:
/// <see cref="PinnedNameEdit"/>, <see cref="PinnedReasoningEdit"/>, <see cref="PinnedContextEdit"/>, and
/// <see cref="PinnedCapabilityEdit"/>.
/// </summary>
/// <remarks>
/// These are <see langword="readonly"/> <c>record struct</c>s carried across
/// <see cref="Microsoft.AspNetCore.Components.EventCallback{TValue}"/> boundaries. Their contract is purely
/// structural: each faithfully carries the edited <see cref="ReconciledModel"/> together with the raw field
/// value, and two payloads are interchangeable exactly when every component matches. The tests therefore pin
/// down two things per record:
/// <list type="number">
///     <item>
///         <description>Construction and deconstruction round-trip every component verbatim.</description>
///     </item>
///     <item>
///         <description>
///         Value equality (and a matching <see cref="object.GetHashCode"/>) holds when all components are equal
///         and breaks as soon as any single component differs — including the <c>Model</c> reference, whose
///         own record equality participates in the payload's equality.
///         </description>
///     </item>
/// </list>
/// Because the file covers four distinct types, each is isolated in its own <c>#region</c>.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class PinnedModelEditsTests
{
	#region PinnedNameEdit

	/// <summary>
	/// Verifies that <see cref="PinnedNameEdit"/> construction stores the model and raw value and deconstructs
	/// them back verbatim.
	/// </summary>
	[Fact]
	public void PinnedNameEdit_Constructor_StoresAndDeconstructsComponents()
	{
		// Arrange
		ReconciledModel model = Model("gpt-4o");

		// Act
		var sut = new PinnedNameEdit(model, "  My Alias  ");
		(ReconciledModel deconstructedModel, string? deconstructedValue) = sut;

		// Assert
		Assert.Same(model, sut.Model);
		Assert.Equal("  My Alias  ", sut.Value);
		Assert.Same(model, deconstructedModel);
		Assert.Equal("  My Alias  ", deconstructedValue);
	}

	/// <summary>
	/// Verifies that two <see cref="PinnedNameEdit"/> payloads with equal components are value-equal and share a
	/// hash code across all equality surfaces.
	/// </summary>
	[Fact]
	public void PinnedNameEdit_Equals_WhenComponentsEqual_IsEqual()
	{
		// Arrange: distinct but value-equal model instances prove equality is structural, not reference-based.
		var left = new PinnedNameEdit(Model("gpt-4o"), "alias");
		var right = new PinnedNameEdit(Model("gpt-4o"), "alias");

		// Act + Assert
		AssertValueEqual(left, right);
	}

	/// <summary>
	/// Verifies that <see cref="PinnedNameEdit"/> equality breaks when the model or the value differs, including
	/// the <see langword="null"/>-versus-blank distinction the editor preserves.
	/// </summary>
	/// <param name="scenario">A human-readable description of the differing component.</param>
	/// <param name="rightModelName">The bare model name for the right payload's model.</param>
	/// <param name="rightValue">The raw value for the right payload.</param>
	[Theory]
	[InlineData("model differs", "gpt-4o-mini", "alias")]
	[InlineData("value differs", "gpt-4o", "other")]
	[InlineData("value null vs non-null", "gpt-4o", null)]
	public void PinnedNameEdit_Equals_WhenComponentDiffers_IsNotEqual(
		string  scenario,
		string  rightModelName,
		string? rightValue)
	{
		_ = scenario;

		// Arrange
		var left = new PinnedNameEdit(Model("gpt-4o"), "alias");
		var right = new PinnedNameEdit(Model(rightModelName), rightValue);

		// Act + Assert
		AssertValueNotEqual(left, right);
	}

	#endregion

	#region PinnedReasoningEdit

	/// <summary>
	/// Verifies that <see cref="PinnedReasoningEdit"/> construction stores the model and selected effort and
	/// deconstructs them back verbatim.
	/// </summary>
	[Fact]
	public void PinnedReasoningEdit_Constructor_StoresAndDeconstructsComponents()
	{
		// Arrange
		ReconciledModel model = Model("gpt-4o");

		// Act
		var sut = new PinnedReasoningEdit(model, ReasoningEffort.High);
		(ReconciledModel deconstructedModel, ReasoningEffort? deconstructedValue) = sut;

		// Assert
		Assert.Same(model, sut.Model);
		Assert.Equal(ReasoningEffort.High, sut.Value);
		Assert.Same(model, deconstructedModel);
		Assert.Equal(ReasoningEffort.High, deconstructedValue);
	}

	/// <summary>
	/// Verifies that two <see cref="PinnedReasoningEdit"/> payloads with equal components are value-equal and
	/// share a hash code across all equality surfaces.
	/// </summary>
	[Fact]
	public void PinnedReasoningEdit_Equals_WhenComponentsEqual_IsEqual()
	{
		// Arrange
		var left = new PinnedReasoningEdit(Model("gpt-4o"), ReasoningEffort.Medium);
		var right = new PinnedReasoningEdit(Model("gpt-4o"), ReasoningEffort.Medium);

		// Act + Assert
		AssertValueEqual(left, right);
	}

	/// <summary>
	/// Verifies that <see cref="PinnedReasoningEdit"/> equality breaks when the model or the selected effort
	/// differs, including the <see langword="null"/> "inherit" selection.
	/// </summary>
	/// <param name="scenario">A human-readable description of the differing component.</param>
	/// <param name="rightModelName">The bare model name for the right payload's model.</param>
	/// <param name="rightValue">The selected effort for the right payload, or <see langword="null"/> to inherit.</param>
	[Theory]
	[InlineData("model differs", "gpt-4o-mini", ReasoningEffort.Medium)]
	[InlineData("value differs", "gpt-4o", ReasoningEffort.Low)]
	[InlineData("value null vs non-null", "gpt-4o", null)]
	public void PinnedReasoningEdit_Equals_WhenComponentDiffers_IsNotEqual(
		string           scenario,
		string           rightModelName,
		ReasoningEffort? rightValue)
	{
		_ = scenario;

		// Arrange
		var left = new PinnedReasoningEdit(Model("gpt-4o"), ReasoningEffort.Medium);
		var right = new PinnedReasoningEdit(Model(rightModelName), rightValue);

		// Act + Assert
		AssertValueNotEqual(left, right);
	}

	#endregion

	#region PinnedContextEdit

	/// <summary>
	/// Verifies that <see cref="PinnedContextEdit"/> construction stores the model and token count and
	/// deconstructs them back verbatim.
	/// </summary>
	[Fact]
	public void PinnedContextEdit_Constructor_StoresAndDeconstructsComponents()
	{
		// Arrange
		ReconciledModel model = Model("gpt-4o");

		// Act
		var sut = new PinnedContextEdit(model, 8192);
		(ReconciledModel deconstructedModel, int? deconstructedValue) = sut;

		// Assert
		Assert.Same(model, sut.Model);
		Assert.Equal(8192, sut.Value);
		Assert.Same(model, deconstructedModel);
		Assert.Equal(8192, deconstructedValue);
	}

	/// <summary>
	/// Verifies that two <see cref="PinnedContextEdit"/> payloads with equal components are value-equal and
	/// share a hash code across all equality surfaces.
	/// </summary>
	[Fact]
	public void PinnedContextEdit_Equals_WhenComponentsEqual_IsEqual()
	{
		// Arrange
		var left = new PinnedContextEdit(Model("gpt-4o"), 4096);
		var right = new PinnedContextEdit(Model("gpt-4o"), 4096);

		// Act + Assert
		AssertValueEqual(left, right);
	}

	/// <summary>
	/// Verifies that <see cref="PinnedContextEdit"/> equality breaks when the model or the token count differs,
	/// including the <see langword="null"/> "cleared" value.
	/// </summary>
	/// <param name="scenario">A human-readable description of the differing component.</param>
	/// <param name="rightModelName">The bare model name for the right payload's model.</param>
	/// <param name="rightValue">The raw token count for the right payload, or <see langword="null"/> when cleared.</param>
	[Theory]
	[InlineData("model differs", "gpt-4o-mini", 4096)]
	[InlineData("value differs", "gpt-4o", 8192)]
	[InlineData("value null vs non-null", "gpt-4o", null)]
	public void PinnedContextEdit_Equals_WhenComponentDiffers_IsNotEqual(
		string scenario,
		string rightModelName,
		int?   rightValue)
	{
		_ = scenario;

		// Arrange
		var left = new PinnedContextEdit(Model("gpt-4o"), 4096);
		var right = new PinnedContextEdit(Model(rightModelName), rightValue);

		// Act + Assert
		AssertValueNotEqual(left, right);
	}

	#endregion

	#region PinnedCapabilityEdit

	/// <summary>
	/// Verifies that <see cref="PinnedCapabilityEdit"/> construction stores the model, the changed capability
	/// flag, and its new value, and deconstructs them back verbatim.
	/// </summary>
	[Fact]
	public void PinnedCapabilityEdit_Constructor_StoresAndDeconstructsComponents()
	{
		// Arrange
		ReconciledModel model = Model("gpt-4o");

		// Act
		var sut = new PinnedCapabilityEdit(model, PinnedCapability.Tools, true);
		(ReconciledModel deconstructedModel, PinnedCapability deconstructedCapability, bool deconstructedValue) = sut;

		// Assert
		Assert.Same(model, sut.Model);
		Assert.Equal(PinnedCapability.Tools, sut.Capability);
		Assert.True(sut.Value);
		Assert.Same(model, deconstructedModel);
		Assert.Equal(PinnedCapability.Tools, deconstructedCapability);
		Assert.True(deconstructedValue);
	}

	/// <summary>
	/// Verifies that two <see cref="PinnedCapabilityEdit"/> payloads with equal components are value-equal and
	/// share a hash code across all equality surfaces.
	/// </summary>
	[Fact]
	public void PinnedCapabilityEdit_Equals_WhenComponentsEqual_IsEqual()
	{
		// Arrange
		var left = new PinnedCapabilityEdit(Model("gpt-4o"), PinnedCapability.Vision, true);
		var right = new PinnedCapabilityEdit(Model("gpt-4o"), PinnedCapability.Vision, true);

		// Act + Assert
		AssertValueEqual(left, right);
	}

	/// <summary>
	/// Verifies that <see cref="PinnedCapabilityEdit"/> equality breaks when the model, the capability flag, or
	/// its value differs.
	/// </summary>
	/// <param name="scenario">A human-readable description of the differing component.</param>
	/// <param name="rightModelName">The bare model name for the right payload's model.</param>
	/// <param name="rightCapability">The capability flag for the right payload.</param>
	/// <param name="rightValue">The new flag value for the right payload.</param>
	[Theory]
	[InlineData("model differs", "gpt-4o-mini", PinnedCapability.Vision, true)]
	[InlineData("capability differs", "gpt-4o", PinnedCapability.Embeddings, true)]
	[InlineData("value differs", "gpt-4o", PinnedCapability.Vision, false)]
	public void PinnedCapabilityEdit_Equals_WhenComponentDiffers_IsNotEqual(
		string           scenario,
		string           rightModelName,
		PinnedCapability rightCapability,
		bool             rightValue)
	{
		_ = scenario;

		// Arrange
		var left = new PinnedCapabilityEdit(Model("gpt-4o"), PinnedCapability.Vision, true);
		var right = new PinnedCapabilityEdit(Model(rightModelName), rightCapability, rightValue);

		// Act + Assert
		AssertValueNotEqual(left, right);
	}

	#endregion

	#region Test infrastructure

	/// <summary>
	/// Builds a minimal discovered <see cref="ReconciledModel"/> row identified by the given bare name. Two calls
	/// with the same name yield distinct-but-value-equal instances, so equality assertions exercise structural
	/// (not reference) equality of the carried model.
	/// </summary>
	/// <param name="name">The bare model name, used for both the identity and the exposed name.</param>
	/// <returns>The configured row.</returns>
	private static ReconciledModel Model(string name)
	{
		return new ReconciledModel(
			name,
			name,
			"cloud",
			name,
			Capabilities: null,
			ContextLength: null,
			ReconciledModelState.Discovered);
	}

	/// <summary>
	/// Asserts that two payloads are value-equal across <see cref="object.Equals(object)"/>, the typed
	/// <c>==</c>/<c>!=</c> operators, and <see cref="object.GetHashCode"/> (asserted stable on repeat calls).
	/// </summary>
	/// <typeparam name="T">The payload record type under test.</typeparam>
	/// <param name="left">The first payload.</param>
	/// <param name="right">The second, expected-equal payload.</param>
	private static void AssertValueEqual<T>(T left, T right)
		where T : struct, IEquatable<T>
	{
		Assert.Equal(left, right);
		Assert.True(left.Equals(right));
		Assert.True(right.Equals(left));
		Assert.Equal(left.GetHashCode(), right.GetHashCode());
		Assert.Equal(left.GetHashCode(), left.GetHashCode());
	}

	/// <summary>
	/// Asserts that two payloads are not value-equal across <see cref="object.Equals(object)"/> and the typed
	/// <c>Equals</c> surface, ruling out an accidental equality collapse.
	/// </summary>
	/// <typeparam name="T">The payload record type under test.</typeparam>
	/// <param name="left">The first payload.</param>
	/// <param name="right">The second, expected-different payload.</param>
	private static void AssertValueNotEqual<T>(T left, T right)
		where T : struct, IEquatable<T>
	{
		Assert.NotEqual(left, right);
		Assert.False(left.Equals(right));
		Assert.False(right.Equals(left));
	}

	#endregion
}
