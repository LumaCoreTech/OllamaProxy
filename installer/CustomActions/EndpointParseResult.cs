// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.CustomActions;

/// <summary>
/// The outcome of parsing a local endpoint URL: either the extracted host and port or a failure message.
/// </summary>
readonly struct EndpointParseResult
{
	/// <summary>
	/// Initializes a new instance of the <see cref="EndpointParseResult"/> struct.
	/// </summary>
	/// <param name="success">Whether parsing succeeded.</param>
	/// <param name="host">The parsed host name or IP address literal.</param>
	/// <param name="port">The parsed port number.</param>
	/// <param name="errorMessage">The error message when parsing failed.</param>
	private EndpointParseResult(
		bool   success,
		string host,
		int    port,
		string errorMessage)
	{
		IsSuccess = success;
		Host = host;
		Port = port;
		ErrorMessage = errorMessage;
	}

	/// <summary>
	/// Gets a value indicating whether parsing succeeded.
	/// </summary>
	public bool IsSuccess { get; }

	/// <summary>
	/// Gets the parsed host name or IP address literal.
	/// </summary>
	public string Host { get; }

	/// <summary>
	/// Gets the parsed port number.
	/// </summary>
	public int Port { get; }

	/// <summary>
	/// Gets the error message when <see cref="IsSuccess"/> is <see langword="false"/>.
	/// </summary>
	public string ErrorMessage { get; }

	/// <summary>
	/// Creates a successful parse result.
	/// </summary>
	/// <param name="host">The parsed host.</param>
	/// <param name="port">The parsed port.</param>
	/// <returns>A successful result.</returns>
	public static EndpointParseResult Success(string host, int port) => new(true, host, port, null);

	/// <summary>
	/// Creates a failed parse result.
	/// </summary>
	/// <param name="errorMessage">The reason parsing failed.</param>
	/// <returns>A failed result.</returns>
	public static EndpointParseResult Failure(string errorMessage) => new(false, null, 0, errorMessage);
}
