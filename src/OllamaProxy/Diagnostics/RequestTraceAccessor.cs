// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Diagnostics;

/// <summary>
/// The <see cref="AsyncLocal{T}"/>-backed <see cref="IRequestTraceAccessor"/>. Because
/// <see cref="AsyncLocal{T}"/> flows its value across <c>await</c> points (including through
/// <c>ConfigureAwait(false)</c> and <c>await foreach</c>), the scope set by the middleware remains
/// visible to the provider layer servicing the same request, even though that layer runs on a
/// singleton instance. The accessor itself is stateless apart from the ambient slot and is therefore
/// safe to register as a singleton.
/// </summary>
sealed class RequestTraceAccessor : IRequestTraceAccessor
{
	private readonly AsyncLocal<ITraceScope?> mCurrent = new();

	/// <inheritdoc/>
	public ITraceScope Current => mCurrent.Value ?? NullTraceScope.Instance;

	/// <inheritdoc/>
	public void Set(ITraceScope scope)
	{
		ArgumentNullException.ThrowIfNull(scope);

		mCurrent.Value = scope;
	}

	/// <inheritdoc/>
	public void Clear() => mCurrent.Value = null;
}
