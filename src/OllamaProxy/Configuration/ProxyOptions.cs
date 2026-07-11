// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.ComponentModel.DataAnnotations;

namespace OllamaProxy.Configuration;

/// <summary>
/// The root options object binding the proxy's <c>OllamaProxy</c> configuration section. It declares
/// the HTTP endpoint the proxy listens on, the upstream <see cref="Backends"/> (each carrying its own
/// operating mode and explicit model registry), and the request-tracing diagnostics. An empty
/// <see cref="Backends"/> map is valid (the typical state after a fresh install); the proxy starts with
/// no models and the admin UI can be used to add backends. Per-backend rules, the listener URL, and the
/// request-tracing rules are enforced in <see cref="Validate"/>.
/// </summary>
public sealed class ProxyOptions : IValidatableObject
{
	/// <summary>
	/// The configuration section name this options object binds to.
	/// </summary>
	public const string SectionName = "OllamaProxy";

	/// <summary>
	/// Gets or sets the absolute URL the inner proxy host listens on. Defaults to
	/// <c>http://localhost:11434</c>; override it via configuration or the <c>OllamaProxy__ListenUrl</c>
	/// environment variable (for example to bind all interfaces inside a container).
	/// </summary>
	[Required]
	[ListenUrl]
	public string ListenUrl { get; set; } = "http://localhost:11434";

	/// <summary>
	/// Gets the upstream backends keyed by their logical name. The map may be empty (valid initial state
	/// after a fresh install); the proxy starts with no models in that case. The key is the backend
	/// reference used by the routing layer. Each backend owns its operating mode and its explicit model
	/// registry (see <see cref="BackendOptions.Mode"/> and <see cref="BackendOptions.Models"/>).
	/// </summary>
	public IDictionary<string, BackendOptions> Backends { get; init; } =
		new Dictionary<string, BackendOptions>(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Gets or sets the request-tracing diagnostics. Tracing is disabled by default; when enabled it
	/// records a file-per-flow trace of the inbound request, the translated backend request, the
	/// backend response, and the outbound response.
	/// </summary>
	public RequestTracingOptions RequestTracing { get; init; } = new();

	/// <summary>
	/// Gets or sets the server-side reasoning-details round-trip cache settings. Enabled by default, it
	/// lets the proxy carry a backend's opaque <c>reasoning_details</c> blob across a multi-turn tool-call
	/// conversation that the Ollama wire format cannot itself convey (see <see cref="ReasoningDetailsCacheOptions"/>).
	/// </summary>
	public ReasoningDetailsCacheOptions ReasoningDetailsCache { get; init; } = new();

	/// <inheritdoc/>
	public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
	{
		// ListenUrl is covered by the [Required] and [ListenUrl] data annotations that the framework already
		// enforces; no additional custom rule is needed here.

		// The options validation pipeline does not recurse into nested complex members, so each backend
		// is validated explicitly here to keep startup fail-fast for the secret, URL, provider, and
		// per-backend registry rules each one owns.
		foreach ((string name, BackendOptions backend) in Backends)
		foreach (ValidationResult result in ValidateChild(backend, $"{nameof(Backends)}[{name}]"))
		{
			yield return result;
		}

		// The tracing options own their own rules (directory presence, positive limits); validate them
		// here for the same reason as the backends: the pipeline does not recurse into nested members.
		foreach (ValidationResult result in ValidateChild(RequestTracing, nameof(RequestTracing)))
		{
			yield return result;
		}

		// The reasoning-details cache owns its own sizing rules (sliding expiration and entry-cap bounds);
		// validate it here too since the pipeline does not recurse into nested members.
		foreach (ValidationResult result in ValidateChild(ReasoningDetailsCache, nameof(ReasoningDetailsCache)))
		{
			yield return result;
		}
	}

	/// <summary>
	/// Runs both data-annotation and <see cref="IValidatableObject"/> validation against a nested
	/// options member, prefixing each member name with <paramref name="path"/> so failures point at
	/// the offending entry rather than a bare property name.
	/// </summary>
	/// <param name="child">The nested options instance to validate.</param>
	/// <param name="path">The configuration path of the child, used to prefix member names.</param>
	/// <returns>The validation failures for the child, with rewritten member names.</returns>
	private static IEnumerable<ValidationResult> ValidateChild(object child, string path)
	{
		List<ValidationResult> results = [];
		Validator.TryValidateObject(child, new ValidationContext(child), results, validateAllProperties: true);

		foreach (ValidationResult result in results)
		{
			string[] members = result.MemberNames
				.Select(member => $"{path}.{member}")
				.DefaultIfEmpty(path)
				.ToArray();

			yield return new ValidationResult(result.ErrorMessage, members);
		}
	}
}
