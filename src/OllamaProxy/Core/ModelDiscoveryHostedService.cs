// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Diagnostics.CodeAnalysis;
using System.Text;

using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Core;

/// <summary>
/// Runs model discovery once during application startup and publishes the result to the router before
/// the proxy begins serving traffic. Implemented as an <see cref="IHostedService"/> whose
/// <see cref="StartAsync"/> the host awaits during startup, so the catalog is guaranteed to be present
/// by the time the first request arrives. Discovery failures for individual backends are already
/// absorbed by <see cref="ModelCatalogBuilder"/>; a catalog that ends up empty is logged as a warning
/// rather than failing startup, so the proxy still answers status endpoints and surfaces clear
/// "model not found" errors instead of refusing to boot.
/// </summary>
sealed class ModelDiscoveryHostedService : IHostedService
{
	private readonly ModelCatalogBuilder                  mCatalogBuilder;
	private readonly IModelCatalogInitializer             mCatalogInitializer;
	private readonly ILogger<ModelDiscoveryHostedService> mLogger;

	/// <summary>
	/// Initializes a new instance of the <see cref="ModelDiscoveryHostedService"/> class.
	/// </summary>
	/// <param name="catalogBuilder">Builds the catalog from configuration and live discovery.</param>
	/// <param name="catalogInitializer">Receives the built catalog and publishes it to the router.</param>
	/// <param name="logger">Records the discovery lifecycle.</param>
	/// <exception cref="ArgumentNullException">
	/// Any of <paramref name="catalogBuilder"/>, <paramref name="catalogInitializer"/>, or
	/// <paramref name="logger"/> is <see langword="null"/>.
	/// </exception>
	public ModelDiscoveryHostedService(
		ModelCatalogBuilder                  catalogBuilder,
		IModelCatalogInitializer             catalogInitializer,
		ILogger<ModelDiscoveryHostedService> logger)
	{
		ArgumentNullException.ThrowIfNull(catalogBuilder);
		ArgumentNullException.ThrowIfNull(catalogInitializer);
		ArgumentNullException.ThrowIfNull(logger);

		mCatalogBuilder = catalogBuilder;
		mCatalogInitializer = catalogInitializer;
		mLogger = logger;
	}

	/// <inheritdoc/>
	[SuppressMessage(
		"Performance",
		"CA1848:Use the LoggerMessage delegates",
		Justification = "Runs once during startup; the LoggerMessage delegate ceremony is not worth it here.")]
	public async Task StartAsync(CancellationToken cancellationToken)
	{
		mLogger.LogInformation("Starting model discovery.");

		// Build the catalog from all configured sources and publish it to the router before accepting traffic.
		IReadOnlyList<RegisteredModel> models = await mCatalogBuilder
			                                        .BuildAsync(cancellationToken)
			                                        .ConfigureAwait(false);
		mCatalogInitializer.Initialize(models);

		if (models.Count == 0)
		{
			// An empty catalog is not a fatal error, but it likely indicates a problem with backend connectivity or
			// an incompatible operating mode, so log it as a warning to get the operator's attention.
			mLogger.LogWarning(
				"Model discovery produced an empty catalog. Verify backend reachability and operating mode.");
		}
		else
		{
			// Log a human-readable summary of the resolved catalog, so an operator can verify at a glance that the
			// expected backends and models were discovered and registered correctly. This is especially helpful
			// during troubleshooting or when validating configuration changes.
			if (mLogger.IsEnabled(LogLevel.Information))
			{
				mLogger.LogInformation("{Summary}", BuildCatalogSummary(models));
			}
		}
	}

	/// <inheritdoc/>
	public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	/// <summary>
	/// Renders the resolved catalog as a human-readable, backend-grouped overview for the startup log,
	/// so an operator can see at a glance which client-facing name maps to which backend and upstream
	/// model, the effective context window, and the resolved capabilities (with their provenance).
	/// Backends and models are listed in stable, name-sorted order for deterministic output.
	/// </summary>
	/// <param name="models">The resolved catalog just published to the router.</param>
	/// <returns>A multi-line summary suitable for a single log entry.</returns>
	internal static string BuildCatalogSummary(IReadOnlyList<RegisteredModel> models)
	{
		IGrouping<string, RegisteredModel>[] byBackend = models
			.GroupBy(model => model.BackendName, StringComparer.OrdinalIgnoreCase)
			.OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
			.ToArray();

		var builder = new StringBuilder();
		builder.Append("Model catalog ready: ")
			.Append(models.Count)
			.Append(" model(s) across ")
			.Append(byBackend.Length)
			.Append(" backend(s).");

		foreach (IGrouping<string, RegisteredModel> backend in byBackend)
		{
			RegisteredModel[] backendModels = backend
				.OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
				.ToArray();

			builder.Append("\n  Backend '")
				.Append(backend.Key)
				.Append("' (")
				.Append(backendModels.Length)
				.Append(" model(s)):");

			foreach (RegisteredModel model in backendModels)
			{
				builder.Append("\n    - ")
					.Append(model.Name)
					.Append(" -> upstream '")
					.Append(model.UpstreamModel)
					.Append("' | context ")
					.Append(model.ContextLength)
					.Append(" | caps: ")
					.Append(DescribeCapabilities(model.Capabilities))
					.Append(" | source ")
					.Append(model.Capabilities.Source);
			}
		}

		return builder.ToString();
	}

	/// <summary>
	/// Formats the enabled capability flags of a model as a compact, comma-separated list, falling back
	/// to <c>none</c> when a model advertises no capabilities at all.
	/// </summary>
	/// <param name="capabilities">The resolved capabilities to describe.</param>
	/// <returns>A comma-separated list of enabled capabilities, or <c>none</c>.</returns>
	internal static string DescribeCapabilities(ModelCapabilities capabilities)
	{
		List<string> flags = [];
		if (capabilities.SupportsCompletion) flags.Add("completion");
		if (capabilities.SupportsTools) flags.Add("tools");
		if (capabilities.SupportsVision) flags.Add("vision");
		if (capabilities.SupportsEmbeddings) flags.Add("embeddings");

		return flags.Count > 0 ? string.Join(", ", flags) : "none";
	}
}
