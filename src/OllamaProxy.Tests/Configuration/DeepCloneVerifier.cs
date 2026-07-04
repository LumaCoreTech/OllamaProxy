// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Reflection;

namespace OllamaProxy.Tests.Configuration;

/// <summary>
/// Reflection helpers that turn the "<c>DeepClone()</c> must carry every property forward" convention into a
/// contract the test suite enforces. The configuration option types copy themselves member by member, so a
/// property added later is silently dropped by an out-of-date <c>DeepClone()</c> unless a test notices. These
/// helpers make that omission fail.
/// </summary>
/// <remarks>
///     <para>
///     A property is classified by its type. A <em>simple</em> property holds a primitive, an enum, a
///     <see cref="string"/>, a <see cref="decimal"/>, or a <see cref="Nullable{T}"/> of one of those: a value a
///     faithful clone reproduces exactly. A <em>reference</em> property holds anything else (a nested options
///     object, a list): state a faithful clone copies into a fresh instance rather than shares.
///     </para>
///     <para>
///     The concerns are checked separately. <see cref="AssertFixtureAssignsNonDefaultSimpleValues"/> guards the
///     test fixture: every simple property must differ from a fresh instance, so a forgotten copy is observable
///     (a default copied to a default would read as success). <see cref="AssertSimplePropertiesCopied"/> guards
///     the clone: every simple property must match. <see cref="AssertReferencePropertiesAre"/> pins the set of
///     reference properties, so a newly added one fails until it is given its own deep-copy assertions.
///     </para>
/// </remarks>
static class DeepCloneVerifier
{
	/// <summary>
	/// Asserts that <paramref name="populated"/> assigns a non-default value to every simple property, comparing
	/// against a freshly constructed <paramref name="freshDefault"/>. A property left at its default would hide a
	/// forgotten copy in <c>DeepClone()</c> (a default copied to a default reads as success), so this guards the
	/// fixture the clone assertion relies on.
	/// </summary>
	/// <typeparam name="T">The options type under test.</typeparam>
	/// <param name="populated">The fully populated fixture the clone test will clone.</param>
	/// <param name="freshDefault">A newly constructed instance supplying each property's default value.</param>
	internal static void AssertFixtureAssignsNonDefaultSimpleValues<T>(T populated, T freshDefault)
		where T : class
	{
		foreach (PropertyInfo property in SimpleProperties<T>())
		{
			object? populatedValue = property.GetValue(populated);
			object? defaultValue = property.GetValue(freshDefault);

			// Assert.False with a message (not Assert.NotEqual) so the failure names the offending property and its
			// stale value: a typed assertion carries no message, and this reflection loop must say which one failed.
			Assert.False(
				Equals(populatedValue, defaultValue),
				$"Fixture gap: '{typeof(T).Name}.{property.Name}' still holds its default ({Format(defaultValue)}). " +
				"Give it a distinctive value so a copy DeepClone() forgets becomes observable.");
		}
	}

	/// <summary>
	/// Asserts that <paramref name="clone"/> carries the same value as <paramref name="original"/> for every
	/// simple property. A mismatch means <c>DeepClone()</c> dropped that property from its initializer.
	/// </summary>
	/// <typeparam name="T">The options type under test.</typeparam>
	/// <param name="original">The instance that was cloned.</param>
	/// <param name="clone">The clone produced by <c>DeepClone()</c>.</param>
	internal static void AssertSimplePropertiesCopied<T>(T original, T clone)
		where T : class
	{
		foreach (PropertyInfo property in SimpleProperties<T>())
		{
			object? originalValue = property.GetValue(original);
			object? cloneValue = property.GetValue(clone);

			// Assert.True with a message (not Assert.Equal) so the failure names the dropped property: the typed
			// overload carries no message, and this reflection loop must say which property differed.
			Assert.True(
				Equals(originalValue, cloneValue),
				$"DeepClone() did not copy '{typeof(T).Name}.{property.Name}': original {Format(originalValue)} " +
				$"but clone {Format(cloneValue)}. Add it to the DeepClone() initializer.");
		}
	}

	/// <summary>
	/// Asserts that the reference-typed state properties of <typeparamref name="T"/> are exactly
	/// <paramref name="expected"/>. Pinning the set means a reference property added later fails this assertion
	/// until the author gives it dedicated deep-copy coverage (a fresh instance plus value checks) and lists it
	/// here.
	/// </summary>
	/// <typeparam name="T">The options type under test.</typeparam>
	/// <param name="expected">The names of every reference-typed property the clone test verifies by hand.</param>
	internal static void AssertReferencePropertiesAre<T>(params string[] expected)
		where T : class
	{
		string[] actual =
		[
			.. ReferenceProperties<T>()
				.Select(property => property.Name)
				.OrderBy(name => name, StringComparer.Ordinal)
		];
		string[] sortedExpected = [.. expected.OrderBy(name => name, StringComparer.Ordinal)];

		Assert.Equal(sortedExpected, actual);
	}

	/// <summary>
	/// Gets the public, readable-and-writable, non-indexer instance properties of <typeparamref name="T"/> whose
	/// type is simple (primitive, enum, <see cref="string"/>, <see cref="decimal"/>, or a
	/// <see cref="Nullable{T}"/> of one of those).
	/// </summary>
	/// <typeparam name="T">The type to inspect.</typeparam>
	/// <returns>The simple state properties, in reflection order.</returns>
	private static IReadOnlyList<PropertyInfo> SimpleProperties<T>() where T : class =>
	[
		.. StateProperties<T>().Where(property => IsSimple(property.PropertyType))
	];

	/// <summary>
	/// Gets the public, readable-and-writable, non-indexer instance properties of <typeparamref name="T"/> whose
	/// type is not simple (a nested object or collection a faithful clone copies into a fresh instance).
	/// </summary>
	/// <typeparam name="T">The type to inspect.</typeparam>
	/// <returns>The reference state properties, in reflection order.</returns>
	private static IReadOnlyList<PropertyInfo> ReferenceProperties<T>() where T : class =>
	[
		.. StateProperties<T>().Where(property => !IsSimple(property.PropertyType))
	];

	/// <summary>
	/// Gets the public instance properties of <typeparamref name="T"/> that represent stored state: readable,
	/// writable (including <see langword="init"/>-only), and not indexers. Computed get-only properties are
	/// excluded because a clone does not assign them.
	/// </summary>
	/// <typeparam name="T">The type to inspect.</typeparam>
	/// <returns>The state properties, in reflection order.</returns>
	private static IReadOnlyList<PropertyInfo> StateProperties<T>() where T : class =>
	[
		.. typeof(T)
			.GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Where(property => property is { CanRead: true, CanWrite: true } &&
			                   property.GetIndexParameters().Length == 0)
	];

	/// <summary>
	/// Determines whether <paramref name="type"/> is a simple value a clone reproduces directly: a primitive, an
	/// enum, a <see cref="string"/>, a <see cref="decimal"/>, or a <see cref="Nullable{T}"/> of one of those.
	/// </summary>
	/// <param name="type">The property type to classify.</param>
	/// <returns><see langword="true"/> when <paramref name="type"/> is simple; otherwise <see langword="false"/>.</returns>
	private static bool IsSimple(Type type)
	{
		Type underlying = Nullable.GetUnderlyingType(type) ?? type;
		return underlying.IsPrimitive ||
		       underlying.IsEnum ||
		       underlying == typeof(string) ||
		       underlying == typeof(decimal);
	}

	/// <summary>
	/// Formats a property value for an assertion message, quoting strings and rendering <see langword="null"/>
	/// explicitly so a blank or null value is unambiguous in the failure output.
	/// </summary>
	/// <param name="value">The value to format.</param>
	/// <returns>A readable representation of <paramref name="value"/>.</returns>
	private static string Format(object? value) => value switch
	{
		null        => "null",
		string text => $"\"{text}\"",
		var _       => value.ToString() ?? "null"
	};
}
