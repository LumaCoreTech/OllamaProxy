// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Admin.Editing;

namespace OllamaProxy.Tests.Admin.Editing;

/// <summary>
/// Tests for <see cref="DesiredStateStructuralValidator"/>, the structural gate for the admin editor's draft: is
/// the desired state keyable by backend name at all?
/// </summary>
/// <remarks>
/// The validator answers exactly one question the apply path would otherwise throw on — can every backend be
/// keyed by a unique, non-blank name? Everything else (URL shape, key length, provider support) is a domain rule
/// left to the recycle's dry-run. These tests walk from "structurally sound" to "structurally broken":
/// <list type="number">
///     <item>
///         <description>
///         Sound drafts return null: a null draft (not yet loaded), an empty draft (an empty backend set is a
///         valid configuration — the proxy starts with no models until a backend is added — so Apply stays
///         enabled), and a draft with unique non-blank names (ReturnsNull).
///         </description>
///     </item>
///     <item>
///         <description>
///         Broken drafts return the exact message the editor shows and the apply path would throw: a blank name
///         wins over everything (ReturnsBlankNameError), and otherwise duplicate names — compared
///         case-insensitively and after trimming, matching how the routing layer keys them — are reported with
///         the offending key(s) (ReturnsDuplicateNamesError).
///         </description>
///     </item>
/// </list>
/// Messages are golden values matched exactly (Assert.Equal), because they are custom production strings the
/// operator reads — not localized BCL text.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class DesiredStateStructuralValidatorTests
{
	/// <summary>
	/// Verifies that a <see langword="null"/> draft — the editor's state before its first load — is treated as
	/// structurally sound and returns <see langword="null"/> rather than an error.
	/// </summary>
	[Fact]
	public void Validate_WhenDraftIsNull_ReturnsNull()
	{
		// Arrange: the editor holds a null draft until the first load completes.

		// Act
		string? result = DesiredStateStructuralValidator.Validate(null);

		// Assert
		Assert.Null(result);
	}

	/// <summary>
	/// Verifies that a draft with no backends is not a structural error: an empty backend set has no key
	/// collision and is a valid configuration in its own right (the proxy starts with no models until a backend
	/// is added), so the validator returns <see langword="null"/> and Apply stays enabled.
	/// </summary>
	[Fact]
	public void Validate_WhenNoBackends_ReturnsNull()
	{
		// Arrange: a freshly assembled draft with an empty backend list.
		DesiredProxyState state = CreateState();

		// Act
		string? result = DesiredStateStructuralValidator.Validate(state);

		// Assert
		Assert.Null(result);
	}

	/// <summary>
	/// Verifies that a draft whose backends all have unique, non-blank names is structurally sound and returns
	/// <see langword="null"/>. Uses more than one backend so a false-positive duplicate detection would surface.
	/// </summary>
	[Fact]
	public void Validate_WhenNamesAreUniqueAndNonBlank_ReturnsNull()
	{
		// Arrange: two distinct, non-blank names — the ordinary "ready to apply" shape.
		DesiredProxyState state = CreateState("alpha", "beta");

		// Act
		string? result = DesiredStateStructuralValidator.Validate(state);

		// Assert
		Assert.Null(result);
	}

	/// <summary>
	/// The blank-name scenarios: a single backend whose name is <see langword="null"/>, empty, or whitespace.
	/// All three are blank per <see cref="string.IsNullOrWhiteSpace"/> and must be rejected identically. Columns:
	/// scenario and the blank name value.
	/// </summary>
	public static TheoryData<string, string?> BlankNameCases => new()
	{
		{ "null name", null },
		{ "empty name", "" },
		{ "whitespace-only name", "   " }
	};

	/// <summary>
	/// Verifies that any blank backend name (null, empty, or whitespace) yields the blank-name error, which the
	/// validator checks before duplicate detection.
	/// </summary>
	/// <param name="scenario">A human-readable description of the blank-name variant under test.</param>
	/// <param name="name">The blank backend name value.</param>
	[Theory]
	[MemberData(nameof(BlankNameCases))]
	public void Validate_WhenAnyNameIsBlank_ReturnsBlankNameError(string scenario, string? name)
	{
		_ = scenario;

		// Arrange: a single backend carrying the blank name, so the blank-name guard is the only one that can fire.
		DesiredProxyState state = CreateState(name);

		// Act
		string? result = DesiredStateStructuralValidator.Validate(state);

		// Assert
		Assert.Equal("Every backend needs a non-blank name.", result);
	}

	/// <summary>
	/// The duplicate-name scenarios. The reported key is the first-encountered trimmed name in each colliding
	/// group (case-insensitive), and multiple colliding groups are joined with ", ". Columns: scenario, the
	/// backend names, and the exact expected error message.
	/// </summary>
	public static TheoryData<string, string[], string> DuplicateNameCases => new()
	{
		{
			"exact duplicate",
			["alpha", "alpha"],
			"Backend names must be unique. Duplicated: alpha."
		},
		{
			// Case-insensitive collision: the group key keeps the first element's casing.
			"case-insensitive duplicate keeps first casing",
			["Alpha", "alpha"],
			"Backend names must be unique. Duplicated: Alpha."
		},
		{
			// Trimming collision: "beta" and " beta " key to the same trimmed name.
			"whitespace-trimmed duplicate",
			["beta", " beta "],
			"Backend names must be unique. Duplicated: beta."
		},
		{
			// Two independent colliding groups are both reported, joined with ", ".
			"multiple duplicated groups joined",
			["a", "a", "b", "b"],
			"Backend names must be unique. Duplicated: a, b."
		}
	};

	/// <summary>
	/// Verifies that duplicate backend names — compared case-insensitively and after trimming — are reported
	/// with the offending key(s) in the exact message the editor shows and the apply path would throw.
	/// </summary>
	/// <param name="scenario">A human-readable description of the duplicate variant under test.</param>
	/// <param name="names">The backend names to place in the draft.</param>
	/// <param name="expectedMessage">The exact error message the validator must return.</param>
	[Theory]
	[MemberData(nameof(DuplicateNameCases))]
	public void Validate_WhenNamesAreDuplicated_ReturnsDuplicateNamesError(
		string   scenario,
		string[] names,
		string   expectedMessage)
	{
		_ = scenario;

		// Arrange: every name is non-blank, so the blank-name guard passes and duplicate detection is what fires.
		DesiredProxyState state = CreateState(names);

		// Act
		string? result = DesiredStateStructuralValidator.Validate(state);

		// Assert
		Assert.Equal(expectedMessage, result);
	}

	/// <summary>
	/// Builds a <see cref="DesiredProxyState"/> holding one <see cref="DesiredBackend"/> per supplied name, in
	/// order. A <see langword="null"/> entry is assigned as the backend's name to exercise the blank-name path.
	/// </summary>
	/// <param name="backendNames">The backend names to stage, in editor order.</param>
	/// <returns>A draft whose backends carry exactly the supplied names.</returns>
	private static DesiredProxyState CreateState(params string?[] backendNames)
	{
		var state = new DesiredProxyState();

		foreach (string? name in backendNames)
		{
			state.Backends.Add(new DesiredBackend { Name = name! });
		}

		return state;
	}
}
