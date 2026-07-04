// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Contracts.Ollama;

namespace OllamaProxy.Endpoints;

/// <summary>
/// Maps and handles the Ollama compatibility status endpoints: <c>GET /api/version</c>,
/// <c>GET /api/ps</c>, and a non-Ollama <c>GET /health</c> liveness probe. All three answer from
/// static or trivially derived data without touching an upstream backend, so they remain responsive
/// even when every backend is unreachable, which is exactly what a client's reachability check and a
/// container orchestrator's health probe need.
/// </summary>
static class SystemEndpoints
{
	/// <summary>
	/// The Ollama-compatible version string reported by <c>/api/version</c>. Clients gate feature
	/// behavior on this, so it advertises a recent Ollama version whose API surface the proxy matches.
	/// </summary>
	public const string ReportedOllamaVersion = "0.5.4";

	/// <summary>
	/// Maps the <c>/api/version</c>, <c>/api/ps</c>, and <c>/health</c> routes onto the endpoint table.
	/// </summary>
	/// <param name="endpoints">The endpoint route builder to register the routes with.</param>
	/// <returns>The same <paramref name="endpoints"/> instance for chaining.</returns>
	public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder endpoints)
	{
		ArgumentNullException.ThrowIfNull(endpoints);

		endpoints.MapGet("/api/version", GetVersion);
		endpoints.MapGet("/api/ps", GetRunningModels);
		endpoints.MapGet("/health", GetHealth);
		return endpoints;
	}

	/// <summary>
	/// Handles <c>GET /api/version</c>, reporting the proxy's advertised Ollama-compatible version.
	/// </summary>
	/// <returns>The version response.</returns>
	private static OllamaVersionResponse GetVersion() => new(ReportedOllamaVersion);

	/// <summary>
	/// Handles <c>GET /api/ps</c>, always reporting an empty running-model list because the proxy
	/// does not load models locally; the upstream backends manage their own model lifecycle.
	/// </summary>
	/// <returns>The empty running-model response.</returns>
	private static OllamaPsResponse GetRunningModels() => new([]);

	/// <summary>
	/// Handles <c>GET /health</c>, returning a minimal liveness payload for orchestrator probes.
	/// </summary>
	/// <returns>A small object indicating the service is alive.</returns>
	private static IResult GetHealth() => Results.Ok(new { status = "ok" });
}
