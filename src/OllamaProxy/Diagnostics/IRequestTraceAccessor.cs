// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Diagnostics;

/// <summary>
/// Publishes and resolves the <see cref="ITraceScope"/> for the current asynchronous flow. The
/// provider adapters are registered as singletons and so cannot receive a request-scoped trace through
/// constructor injection; this accessor bridges that gap by flowing the active scope through the
/// asynchronous call chain. The tracing middleware sets the scope at the start of a traced request and
/// clears it when the request completes; everywhere else <see cref="Current"/> resolves to
/// <see cref="NullTraceScope.Instance"/>, so callers can record provenance unconditionally.
/// </summary>
interface IRequestTraceAccessor
{
	/// <summary>
	/// Gets the trace scope for the current asynchronous flow, or <see cref="NullTraceScope.Instance"/>
	/// when no request is being traced.
	/// </summary>
	ITraceScope Current { get; }

	/// <summary>
	/// Establishes <paramref name="scope"/> as the ambient scope for the current flow and the
	/// asynchronous work it spawns.
	/// </summary>
	/// <param name="scope">The scope to publish.</param>
	void Set(ITraceScope scope);

	/// <summary>
	/// Clears the ambient scope, restoring the no-op default. Called by the middleware once the request
	/// completes so the scope does not leak into a reused thread's next flow.
	/// </summary>
	void Clear();
}
