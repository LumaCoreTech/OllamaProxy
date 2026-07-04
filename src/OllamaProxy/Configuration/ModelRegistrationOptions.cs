// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.ComponentModel.DataAnnotations;

namespace OllamaProxy.Configuration;

/// <summary>
/// An explicit registry entry that pins how a single bare model name maps to an upstream model on its
/// owning backend, optionally overriding the detected capabilities. Each entry lives in its backend's
/// <see cref="BackendOptions.Models"/> list, so the backend is implied by position rather than carried as
/// a reference. The client-facing name is the entry's <see cref="Name"/> with the backend's
/// <see cref="BackendOptions.ModelPrefix"/> applied at exposure (when one is set), so the entry stores the
/// bare name. Registry entries are the sole source of exposed models for a backend in
/// <see cref="OperatingMode.Explicit"/> and augment auto-exposed models in <see cref="OperatingMode.Hybrid"/>.
/// </summary>
public sealed class ModelRegistrationOptions : IValidatableObject
{
	/// <summary>
	/// Gets or sets the bare model name this entry registers. When the owning backend sets a
	/// <see cref="BackendOptions.ModelPrefix"/> the client-facing name (the value clients send as
	/// <c>model</c> and see in <c>/api/tags</c>) is <c>prefix/Name</c>; without a prefix it is this value
	/// verbatim. The prefix is applied at exposure exactly as for a discovered model, so this value is stored
	/// unprefixed. Required.
	/// </summary>
	[Required(AllowEmptyStrings = false)]
	public string Name { get; set; } = string.Empty;

	/// <summary>
	/// Gets or sets the upstream model identifier requested from the backend. When omitted, the bare
	/// <see cref="Name"/> is forwarded unchanged (the upstream request is never prefixed).
	/// </summary>
	public string? UpstreamModel { get; set; }

	/// <summary>
	/// Gets or sets completion (chat/text) support for this pinned model. The additive
	/// <see cref="SupportsTools"/>, <see cref="SupportsVision"/>, and <see cref="SupportsEmbeddings"/>
	/// flags resolve a <see langword="null"/> value to <see langword="false"/>. Completion is different: it is
	/// the proxy's baseline modality, so a <see langword="null"/> value resolves to <see langword="true"/>.
	/// Set this explicitly to <see langword="false"/> for an embedding-only model, in which case
	/// <see cref="SupportsEmbeddings"/> must be <see langword="true"/> so the model still exposes a usable
	/// endpoint.
	/// </summary>
	public bool? SupportsCompletion { get; set; }

	/// <summary>
	/// Gets or sets tool-calling support for this pinned model. A registry entry is fully declared in
	/// configuration and never runs live capability detection, so a <see langword="null"/> value
	/// resolves to <see langword="false"/> rather than being detected. Set this explicitly to
	/// <see langword="true"/> when the model supports tools.
	/// </summary>
	public bool? SupportsTools { get; set; }

	/// <summary>
	/// Gets or sets vision support for this pinned model. As with <see cref="SupportsTools"/>, a
	/// <see langword="null"/> value resolves to <see langword="false"/>; capabilities are not detected
	/// for registry entries.
	/// </summary>
	public bool? SupportsVision { get; set; }

	/// <summary>
	/// Gets or sets embedding support for this pinned model. As with <see cref="SupportsTools"/>, a
	/// <see langword="null"/> value resolves to <see langword="false"/>; capabilities are not detected
	/// for registry entries.
	/// </summary>
	public bool? SupportsEmbeddings { get; set; }

	/// <summary>
	/// Gets or sets the context window (in tokens) advertised for this pinned model. When set, it is an explicit
	/// per-model override that always wins over the backend's reported and default values. It is the way to
	/// deliberately set a single model's window (or constrain it below what the backend reports) without lowering
	/// the backend-wide <see cref="BackendOptions.ContextLength"/> default. When left <see langword="null"/>, the
	/// model falls back to the backend's reported window or, absent that, the backend default; a registry entry
	/// on a backend that reports no window and defines no default must therefore specify this. Must be greater
	/// than zero when specified.
	/// </summary>
	public int? ContextLength { get; set; }

	/// <summary>
	/// Gets or sets a fixed reasoning effort pinned to this model. The backend-wide
	/// <see cref="BackendOptions.ReasoningEffort"/> default only applies when a request carries no
	/// <c>think</c> directive. A pinned effort is different: it is <b>authoritative</b> and overrides both the
	/// inbound <c>think</c> directive and the backend default, so a client can never push a value the model
	/// rejects. This is the deterministic way to expose a model with a known-safe effort (for example a separate
	/// registry entry per level, named at the operator's discretion). When left <see langword="null"/> the
	/// model keeps the normal resolution chain (request <c>think</c>, then backend default, then none); set it
	/// explicitly to <see cref="Configuration.ReasoningEffort.None"/> to pin reasoning hard off for this model.
	/// </summary>
	public ReasoningEffort? ReasoningEffort { get; set; }

	/// <inheritdoc/>
	public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
	{
		if (string.IsNullOrWhiteSpace(Name))
			yield return new ValidationResult("Model name is required.", [nameof(Name)]);

		if (ContextLength is <= 0)
		{
			yield return new ValidationResult(
				"Model context length must be greater than zero when specified.",
				[nameof(ContextLength)]);
		}

		// An entry that opts out of completion must opt into embeddings: a model resolving to neither has
		// no usable Ollama endpoint (the native API routes only completion and embeddings), mirroring the
		// discovery path that skips such models. SupportsEmbeddings defaults to false, so "not true" covers
		// both an explicit false and an unset flag.
		if (SupportsCompletion == false && SupportsEmbeddings != true)
		{
			yield return new ValidationResult(
				"Model must support completion or embeddings; an entry that disables completion must set " +
				"'SupportsEmbeddings' to true.",
				[nameof(SupportsCompletion), nameof(SupportsEmbeddings)]);
		}
	}

	/// <summary>
	/// Gets the effective upstream model identifier, falling back to <see cref="Name"/> when no
	/// explicit <see cref="UpstreamModel"/> was configured.
	/// </summary>
	/// <returns>The upstream model identifier to request from the backend.</returns>
	public string ResolveUpstreamModel() => string.IsNullOrWhiteSpace(UpstreamModel) ? Name : UpstreamModel;

	/// <summary>
	/// Creates a deep copy of this registry entry. Every property is value-typed or an immutable
	/// <see cref="string"/>, so the copy shares no mutable state with this instance and the editor can edit the
	/// copy without touching the live snapshot.
	/// </summary>
	/// <returns>A standalone copy carrying every property of this entry.</returns>
	public ModelRegistrationOptions DeepClone() => new()
	{
		Name = Name,
		UpstreamModel = UpstreamModel,
		SupportsCompletion = SupportsCompletion,
		SupportsTools = SupportsTools,
		SupportsVision = SupportsVision,
		SupportsEmbeddings = SupportsEmbeddings,
		ContextLength = ContextLength,
		ReasoningEffort = ReasoningEffort
	};
}
