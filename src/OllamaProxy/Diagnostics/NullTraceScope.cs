// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Diagnostics;

/// <summary>
/// The do-nothing <see cref="ITraceScope"/> used as the ambient default whenever a request is not
/// being traced, either because tracing is disabled globally or because the current asynchronous flow
/// is outside any traced request (for example, startup model discovery). Every member is a no-op, so
/// the endpoint and provider layers can record provenance unconditionally and incur no cost on the
/// untraced path. The single shared <see cref="Instance"/> is immutable and safe to use concurrently.
/// </summary>
sealed class NullTraceScope : ITraceScope
{
	/// <summary>The shared, stateless instance used wherever no active trace exists.</summary>
	public static readonly NullTraceScope Instance = new();

	private NullTraceScope() { }

	/// <inheritdoc/>
	public bool IsEnabled => false;

	/// <inheritdoc/>
	public void RecordReasoning(
		string? resolvedEffort,
		string  source,
		string? backendDefault,
		string? wireField) { }

	/// <inheritdoc/>
	public void RecordBackendRequest(string backendName, string path, string body) { }

	/// <inheritdoc/>
	public void RecordBackendReasoning(string backendName, string reasoning) { }

	/// <inheritdoc/>
	public void RecordBackendResponse(string backendName, string body) { }

	/// <inheritdoc/>
	public void Note(string summary) { }
}
