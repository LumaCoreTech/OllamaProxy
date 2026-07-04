// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Microsoft.Extensions.Options;

using OllamaProxy.Configuration;
using OllamaProxy.Providers.OpenAiProtocol;

namespace OllamaProxy.Tests.Integration;

/// <summary>
/// Shared factory for the <see cref="IReasoningDetailsCache"/> collaborator the provider integration tests
/// must pass to a provider under test. Most of those tests do not exercise the reasoning-details round-trip,
/// so they take a default, enabled cache that simply behaves as a faithful (empty) collaborator: a
/// <c>Retrieve</c> misses and a <c>Store</c> is reached only when a canned response actually carries a blob,
/// which the unrelated fixtures never do. Tests that <em>do</em> drive the round-trip construct their own
/// instance so they can assert on its contents.
/// </summary>
static class TestReasoningDetailsCache
{
	/// <summary>
	/// Creates a reasoning-details cache backed by the default (enabled) options and the system clock.
	/// </summary>
	/// <returns>A ready-to-use <see cref="IReasoningDetailsCache"/> for a provider under test.</returns>
	public static IReasoningDetailsCache CreateDefault() => new ReasoningDetailsCache(
		Options.Create(new ProxyOptions()),
		TimeProvider.System);
}
