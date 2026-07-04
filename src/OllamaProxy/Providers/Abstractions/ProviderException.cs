// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Net;

namespace OllamaProxy.Providers.Abstractions;

/// <summary>
/// Signals that an upstream provider call failed in a way that should surface to the client as an
/// Ollama-formatted error. It carries the HTTP <see cref="StatusCode"/> to propagate (so a backend
/// 401 or 404 is not masked as a generic 500) alongside a human-readable, English message. The
/// endpoint layer catches this type and renders the Ollama error envelope.
/// </summary>
sealed class ProviderException : Exception
{
	/// <summary>
	/// Initializes a new instance of the <see cref="ProviderException"/> class.
	/// </summary>
	/// <param name="statusCode">The HTTP status code to surface to the client.</param>
	/// <param name="message">A human-readable, English description of the failure.</param>
	/// <param name="innerException">The underlying exception, when one triggered this failure.</param>
	public ProviderException(HttpStatusCode statusCode, string message, Exception? innerException = null)
		: base(message, innerException)
	{
		StatusCode = statusCode;
	}

	/// <summary>
	/// Gets the HTTP status code to surface to the client.
	/// </summary>
	public HttpStatusCode StatusCode { get; }
}
