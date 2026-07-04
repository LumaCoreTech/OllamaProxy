// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.ComponentModel.DataAnnotations;

namespace OllamaProxy.Configuration;

/// <summary>
/// Configuration for a single upstream backend the proxy can route to. Each backend has a logical
/// name (its key in the <see cref="ProxyOptions.Backends"/> map), a base address, a provider type
/// selecting the adapter, credentials, its own <see cref="Mode"/>, and an explicit model registry in
/// <see cref="Models"/>. The <see cref="ApiKey"/> is required, length-checked, and may be supplied
/// through an environment variable for production rather than a settings file. Declared
/// <see langword="public"/> so the admin UI's component parameters can accept it without forcing every
/// parent to expose internals.
/// </summary>
public sealed class BackendOptions : IValidatableObject
{
	/// <summary>
	/// The minimum accepted length for a non-empty <see cref="ApiKey"/>.
	/// </summary>
	public const int MinimumApiKeyLength = 8;

	/// <summary>
	/// The provider-type discriminator a backend defaults to when none is configured. It is the one neutral
	/// provider literal the configuration layer owns deliberately: the layer must not depend on the
	/// <c>Providers</c> namespace, so it cannot read the OpenAI adapter's descriptor for this default. The single
	/// duplication is made safe by the startup provider-type validation, which fails fast if this value does not
	/// resolve to a registered provider (for example because the OpenAI adapter was renamed or removed).
	/// </summary>
	public const string DefaultProviderType = "openai";

	/// <summary>
	/// Gets or sets the base address of the backend's OpenAI-compatible API (for example
	/// <c>https://api.openai.com/v1</c> or <c>http://localhost:1234/v1</c>). Required.
	/// </summary>
	public string BaseUrl { get; set; } = string.Empty;

	/// <summary>
	/// Gets or sets the provider-type discriminator selecting the adapter for this backend. Defaults to
	/// <see cref="DefaultProviderType"/>. The set of supported values is owned by the registered provider adapters
	/// (each publishes its type through its descriptor) rather than enumerated here; the startup provider-type
	/// validation rejects a value no adapter handles.
	/// </summary>
	public string ProviderType { get; set; } = DefaultProviderType;

	/// <summary>
	/// Gets or sets the API key sent as a bearer token to the backend. Required and length-checked.
	/// For local backends that accept any token, supply a placeholder of sufficient length. Prefer an
	/// environment variable in production so the secret never lands in a settings file.
	/// </summary>
	public string ApiKey { get; set; } = string.Empty;

	/// <summary>
	/// Gets or sets the operating mode that determines how this backend's exposed model list is
	/// assembled. When left <see langword="null"/>, the effective mode is derived from the
	/// <see cref="ProviderType"/> by the provider catalog: capability-rich providers (Venice, OpenRouter) default
	/// to <see cref="OperatingMode.PlugAndPlay"/>, while capability-poor providers (OpenAI, vLLM) default to
	/// <see cref="OperatingMode.Explicit"/>. The effective mode is resolved through
	/// <c>IProviderCatalog.ResolveMode</c>, since the provider-aware default lives with the providers rather than
	/// in this configuration type.
	/// </summary>
	public OperatingMode? Mode { get; set; }

	/// <summary>
	/// Gets this backend's explicit model registry. Optional in <see cref="OperatingMode.PlugAndPlay"/>
	/// and <see cref="OperatingMode.Hybrid"/>; in <see cref="OperatingMode.Explicit"/> it is the sole
	/// source of exposed models, but an empty list is still valid. The backend simply contributes
	/// nothing in that case.
	/// </summary>
	public IList<ModelRegistrationOptions> Models { get; init; } = new List<ModelRegistrationOptions>();

	/// <summary>
	/// Gets the active capability-probing settings for this backend. Every probe is enabled by default and
	/// consulted only when a model's backend metadata is inconclusive; see <see cref="CapabilityProbingOptions"/>.
	/// </summary>
	public CapabilityProbingOptions Probing { get; init; } = new();

	/// <summary>
	/// Gets or sets a fallback context window (in tokens) for this backend's exposed models. It applies only
	/// when neither the backend's discovery metadata nor a registry entry supplies a context length: a value the
	/// backend reports always wins, so this never narrows or overrides what the backend advertises. It merely
	/// fills the gap for backends that advertise none (typically metadata-poor providers such as OpenAI or
	/// vLLM). To deliberately constrain a specific model below what the backend reports, pin it in
	/// <see cref="Models"/> with an explicit per-model
	/// <see cref="ModelRegistrationOptions.ContextLength"/> override rather than lowering this backend-wide
	/// default. Must be greater than zero when specified.
	/// </summary>
	public int? ContextLength { get; set; }

	/// <summary>
	/// Gets or sets an optional prefix applied to the client-facing name of <em>every</em> model this
	/// backend exposes. This covers both auto-exposed (discovered) models and explicit <see cref="Models"/>
	/// registry entries, producing names of the form <c>prefix/model</c> (for example <c>vllm/gemma2-27b</c>). It
	/// disambiguates the same model served by multiple backends so each remains reachable under a distinct,
	/// stable name; leave it unset for single-backend deployments that want the shorter bare name. A registry
	/// entry stores its bare <see cref="ModelRegistrationOptions.Name"/> and has the prefix applied at exposure
	/// exactly as a discovered model does, so a model is named identically whether pinned or discovered. The
	/// prefix changes only the client-facing name; the unprefixed model identifier is still what the proxy
	/// requests upstream. Must not be blank or contain a <c>/</c> when specified.
	/// </summary>
	public string? ModelPrefix { get; set; }

	/// <summary>
	/// Gets or sets the default reasoning effort applied to chat requests routed to this backend when
	/// the inbound request does not specify one via the Ollama <c>think</c> field. A per-request
	/// directive always overrides this default; leave it unset to send no reasoning directive unless a
	/// client asks for one. The configured value is mapped onto the backend's own wire dialect by its
	/// provider adapter.
	/// </summary>
	public ReasoningEffort? ReasoningEffort { get; set; }

	/// <inheritdoc/>
	public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
	{
		if (string.IsNullOrWhiteSpace(BaseUrl))
		{
			yield return new ValidationResult(
				"Backend base URL is required.",
				[nameof(BaseUrl)]);
		}
		else if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out Uri? _))
		{
			yield return new ValidationResult(
				"Backend base URL must be an absolute URI.",
				[nameof(BaseUrl)]);
		}

		// Whether ProviderType names a provider the proxy actually ships is validated at startup by the
		// provider-type validator (Providers namespace), which can consult the registered provider catalog this
		// configuration layer deliberately does not depend on. Only the provider-independent shape rules live here.

		if (ContextLength is <= 0)
		{
			yield return new ValidationResult(
				"Backend context length must be greater than zero when specified.",
				[nameof(ContextLength)]);
		}

		// A prefix that is present but blank, or that embeds the separator, would yield confusing or
		// ambiguous client-facing names, so it is rejected rather than silently normalized.
		if (ModelPrefix is not null && (string.IsNullOrWhiteSpace(ModelPrefix) || ModelPrefix.Contains('/')))
		{
			yield return new ValidationResult(
				"Backend model prefix must be non-blank and must not contain '/' when specified.",
				[nameof(ModelPrefix)]);
		}

		// The API key is required and length-checked, but the proxy does not attempt to validate it beyond that since
		// the backends have different formats and the proxy is agnostic to them. A length check weeds out obvious
		// misconfigurations like an unset environment variable or a truncated secret, while still allowing flexibility
		// for different formats and future changes.
		if (string.IsNullOrEmpty(ApiKey))
		{
			yield return new ValidationResult(
				"Backend API key is required. Provide it via configuration or an environment variable.",
				[nameof(ApiKey)]);
		}
		else if (ApiKey.Length < MinimumApiKeyLength)
		{
			yield return new ValidationResult(
				$"Backend API key must be at least {MinimumApiKeyLength} characters long.",
				[nameof(ApiKey)]);
		}

		// Recurse into the probing sub-options so their timeout bounds are enforced at startup too.
		List<ValidationResult> probingResults = [];
		Validator.TryValidateObject(
			Probing,
			new ValidationContext(Probing),
			probingResults,
			validateAllProperties: true);

		foreach (ValidationResult result in probingResults)
		{
			string[] members = result.MemberNames
				.Select(member => $"{nameof(Probing)}.{member}")
				.DefaultIfEmpty(nameof(Probing))
				.ToArray();

			yield return new ValidationResult(result.ErrorMessage, members);
		}

		// Recurse into each registry entry. The options validation pipeline does not descend into nested
		// collection members, so the per-model rules (name presence, context length, embedding-only
		// consistency) would otherwise go unchecked at startup. An empty registry is intentionally allowed,
		// including for Explicit-mode backends, which then simply contribute no models.
		for (int index = 0; index < Models.Count; index++)
		{
			List<ValidationResult> modelResults = [];
			Validator.TryValidateObject(
				Models[index],
				new ValidationContext(Models[index]),
				modelResults,
				validateAllProperties: true);

			foreach (ValidationResult result in modelResults)
			{
				string[] members = result.MemberNames
					.Select(member => $"{nameof(Models)}[{index}].{member}")
					.DefaultIfEmpty($"{nameof(Models)}[{index}]")
					.ToArray();

				yield return new ValidationResult(result.ErrorMessage, members);
			}
		}

		// Two registry entries that resolve to the same client-facing name collide silently in the catalog
		// (ModelCatalogBuilder keys exposed models by name, so the last entry wins and the earlier one vanishes
		// without a trace). Reject the duplicate here so the clash surfaces at startup and through the admin
		// dry-run instead. FindDuplicateModelNames is the shared rule the admin editor also consults to mark the
		// offending rows inline, so the editor's inline warning and this domain check cannot drift apart.
		foreach (string duplicate in FindDuplicateModelNames(Models))
		{
			yield return new ValidationResult(
				$"Model name '{duplicate}' is registered more than once on this backend; each entry must expose " +
				"a distinct name. Rename one of the entries — for example to offer the same upstream model at a " +
				"different fixed reasoning effort.",
				[nameof(Models)]);
		}
	}

	/// <summary>
	/// Finds the client-facing model names that <paramref name="models"/> registers more than once, comparing
	/// names exactly as the runtime catalog keys them: trimmed and case-insensitive
	/// (<see cref="StringComparer.OrdinalIgnoreCase"/>). Blank names are skipped: a missing name is already a
	/// per-entry failure, so grouping blanks would mask it as a spurious duplicate. Each duplicated name is
	/// reported once, as its trimmed key.
	/// </summary>
	/// <param name="models">The registry entries to scan for colliding client-facing names.</param>
	/// <returns>
	/// Each trimmed name that <paramref name="models"/> registers more than once, in first-seen order, or an
	/// empty sequence when every non-blank name is unique. Never <see langword="null"/>.
	/// </returns>
	/// <exception cref="ArgumentNullException"><paramref name="models"/> is <see langword="null"/>.</exception>
	/// <remarks>
	/// This is the single source of truth for the duplicate-name rule. <see cref="Validate"/> turns each result
	/// into a <see cref="ValidationResult"/> so the clash surfaces at startup and through the admin dry-run, and
	/// the admin editor consults the same method to mark the offending rows inline before Apply. Sharing one
	/// implementation keeps the inline warning and the domain rule from drifting apart. Within one backend the
	/// <see cref="ModelPrefix"/> is constant, so comparing the bare names is equivalent to comparing the prefixed
	/// exposed names.
	/// </remarks>
	public static IEnumerable<string> FindDuplicateModelNames(IEnumerable<ModelRegistrationOptions> models)
	{
		ArgumentNullException.ThrowIfNull(models);

		return models
			.Select(model => model.Name)
			.Where(name => !string.IsNullOrWhiteSpace(name))
			.GroupBy(name => name.Trim(), StringComparer.OrdinalIgnoreCase)
			.Where(group => group.Count() > 1)
			.Select(group => group.Key);
	}

	/// <summary>
	/// Creates a deep copy of this backend. The two reference-typed members are cloned, not shared: the copy
	/// gets a fresh <see cref="Models"/> list whose entries are each deep-cloned, and its own deep-cloned
	/// <see cref="Probing"/> instance. Editing the copy (adding or removing a model, toggling a probe) therefore
	/// never reaches back into this instance. Every other property is value-typed or an immutable
	/// <see cref="string"/>, so a member-wise copy is already independent.
	/// </summary>
	/// <returns>A standalone copy that shares no mutable state with this backend.</returns>
	public BackendOptions DeepClone()
	{
		List<ModelRegistrationOptions> clonedModels = new(Models.Count);
		foreach (ModelRegistrationOptions model in Models)
		{
			clonedModels.Add(model.DeepClone());
		}

		return new BackendOptions
		{
			BaseUrl = BaseUrl,
			ProviderType = ProviderType,
			ApiKey = ApiKey,
			Mode = Mode,
			Models = clonedModels,
			Probing = Probing.DeepClone(),
			ContextLength = ContextLength,
			ModelPrefix = ModelPrefix,
			ReasoningEffort = ReasoningEffort
		};
	}

	/// <summary>
	/// Creates a copy of this backend that carries every setting forward unchanged except its model registry,
	/// which is replaced with <paramref name="models"/>. The copy is shallow: the returned instance exposes the
	/// supplied <see cref="Models"/> list while sharing this backend's other reference-typed members (such as
	/// <see cref="Probing"/>), so callers must not mutate those shared members through the copy.
	/// </summary>
	/// <param name="models">The model registry the copy exposes in place of this backend's own.</param>
	/// <returns>A backend identical to this one apart from its <see cref="Models"/> list.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="models"/> is <see langword="null"/>.</exception>
	public BackendOptions WithModels(IList<ModelRegistrationOptions> models)
	{
		ArgumentNullException.ThrowIfNull(models);

		return new BackendOptions
		{
			BaseUrl = BaseUrl,
			ProviderType = ProviderType,
			ApiKey = ApiKey,
			Mode = Mode,
			Models = models,
			Probing = Probing,
			ContextLength = ContextLength,
			ModelPrefix = ModelPrefix,
			ReasoningEffort = ReasoningEffort
		};
	}

	/// <summary>
	/// Creates a copy of this backend that carries every setting forward unchanged except its
	/// <see cref="ApiKey"/>, which is replaced with <paramref name="apiKey"/>. The copy is shallow: the returned
	/// instance shares this backend's other reference-typed members (such as <see cref="Models"/> and
	/// <see cref="Probing"/>), so callers must not mutate those shared members through the copy.
	/// </summary>
	/// <param name="apiKey">The API key the copy carries in place of this backend's own.</param>
	/// <returns>A backend identical to this one apart from its <see cref="ApiKey"/>.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="apiKey"/> is <see langword="null"/>.</exception>
	public BackendOptions WithApiKey(string apiKey)
	{
		ArgumentNullException.ThrowIfNull(apiKey);

		return new BackendOptions
		{
			BaseUrl = BaseUrl,
			ProviderType = ProviderType,
			ApiKey = apiKey,
			Mode = Mode,
			Models = Models,
			Probing = Probing,
			ContextLength = ContextLength,
			ModelPrefix = ModelPrefix,
			ReasoningEffort = ReasoningEffort
		};
	}
}
