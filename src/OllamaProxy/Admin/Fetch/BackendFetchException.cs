// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Admin.Fetch;

/// <summary>
/// The streaming counterpart of a failed <see cref="BackendFetchResult"/>: the classified failure of a
/// <see cref="IBackendModelFetcher.FetchStreamingAsync"/> enumeration. A streaming fetch cannot hand back a
/// failure <em>result</em> the way <see cref="IBackendModelFetcher.FetchAsync"/> does. By the time a fault
/// occurs it may already have yielded candidates, so the fault surfaces here as a throw instead. The exception
/// carries the same honest <see cref="ErrorKind"/> classification the result form would have recorded.
/// Candidates yielded before the throw remain valid; the consumer renders them and shows this failure alongside.
/// </summary>
/// <remarks>
/// A caller-requested cancellation is deliberately <em>not</em> wrapped in this exception. It stays an
/// <see cref="OperationCanceledException"/>, so the consumer can tell an abort it asked for apart from a genuine
/// backend failure. This matches how the non-streaming path keeps the two distinct.
/// </remarks>
sealed class BackendFetchException : Exception
{
	/// <summary>
	/// Initializes a new instance of the <see cref="BackendFetchException"/> class.
	/// </summary>
	/// <param name="errorKind">How far the failure could be attributed (credentials, upstream, or unknown).</param>
	/// <param name="message">A human-readable, English description of what went wrong.</param>
	/// <param name="innerException">The underlying fault that was classified, preserved for diagnostics.</param>
	public BackendFetchException(
		BackendFetchErrorKind errorKind,
		string                message,
		Exception?            innerException)
		: base(message, innerException)
	{
		ErrorKind = errorKind;
	}

	/// <summary>
	/// Gets how far the failure could be attributed: a credential problem
	/// (<see cref="BackendFetchErrorKind.Authentication"/>), an upstream fault
	/// (<see cref="BackendFetchErrorKind.Upstream"/>), or one the proxy could not place
	/// (<see cref="BackendFetchErrorKind.Unknown"/>).
	/// </summary>
	public BackendFetchErrorKind ErrorKind { get; }
}
