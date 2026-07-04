// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Providers.Abstractions;
using OllamaProxy.Providers.OpenAiProtocol;

namespace OllamaProxy.Tests.Integration.Live;

/// <summary>
/// An <see cref="ICapabilityProber"/> that reports every probe as inconclusive (<see langword="null"/>), so a
/// provider under test can be constructed without reaching its active-probing internals. The live conformance
/// suite drives capabilities through real chat/embeddings calls rather than the probe path, so a faithful
/// no-op prober is the right collaborator. Shared by every live test class instead of re-declaring the same
/// stub per file.
/// </summary>
sealed class LiveStubCapabilityProber : ICapabilityProber
{
	/// <inheritdoc/>
	public Task<bool?> ProbeCompletionSupportAsync(
		BackendContext    backend,
		string            modelId,
		CancellationToken cancellationToken) => Task.FromResult<bool?>(null);

	/// <inheritdoc/>
	public Task<bool?> ProbeToolSupportAsync(
		BackendContext    backend,
		string            modelId,
		CancellationToken cancellationToken) => Task.FromResult<bool?>(null);

	/// <inheritdoc/>
	public Task<bool?> ProbeVisionSupportAsync(
		BackendContext    backend,
		string            modelId,
		CancellationToken cancellationToken) => Task.FromResult<bool?>(null);

	/// <inheritdoc/>
	public Task<bool?> ProbeEmbeddingSupportAsync(
		BackendContext    backend,
		string            modelId,
		CancellationToken cancellationToken) => Task.FromResult<bool?>(null);
}
