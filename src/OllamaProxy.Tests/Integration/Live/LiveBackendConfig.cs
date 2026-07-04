// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Microsoft.Extensions.Options;

using OllamaProxy.Configuration;

namespace OllamaProxy.Tests.Integration.Live;

/// <summary>
/// Names the environment variables a single live backend reads, plus the defaults for the two that have a
/// sensible canonical value (base URL and chat model). A per-backend test class declares these names as
/// <c>const</c> fields — so the gating attributes can reference them at compile time — and bundles them here
/// for <see cref="LiveBackendConfig.Create"/> to resolve. Keeping the names in one descriptor guarantees the
/// attribute that gates a test and the factory that builds its configuration read the exact same variables.
/// </summary>
/// <param name="BackendName">The logical backend name used in the <c>BackendContext</c> and options map.</param>
/// <param name="ProviderType">The provider-type discriminator selecting the adapter (for example <c>venice</c>).</param>
/// <param name="ApiKeyEnv">The variable carrying the bearer API key. Required; its absence skips the test.</param>
/// <param name="BaseUrlEnv">The variable overriding the backend base URL.</param>
/// <param name="DefaultBaseUrl">The base URL used when <paramref name="BaseUrlEnv"/> is unset.</param>
/// <param name="ChatModelEnv">The variable naming the chat/tool model. Required; its absence skips the test.</param>
/// <param name="DefaultChatModel">The chat model used when <paramref name="ChatModelEnv"/> is unset.</param>
/// <param name="VisionModelEnv">The variable naming the vision model. Optional; its absence skips vision.</param>
/// <param name="EmbeddingModelEnv">The variable naming the embedding model. Optional; its absence skips embeddings.</param>
/// <param name="ReasoningModelEnv">The variable naming the reasoning model. Optional; its absence skips reasoning.</param>
sealed record LiveBackendDescriptor(
	string BackendName,
	string ProviderType,
	string ApiKeyEnv,
	string BaseUrlEnv,
	string DefaultBaseUrl,
	string ChatModelEnv,
	string DefaultChatModel,
	string VisionModelEnv,
	string EmbeddingModelEnv,
	string ReasoningModelEnv);

/// <summary>
/// The resolved live configuration for one backend: a real <see cref="BackendOptions"/> (base URL, provider
/// type, bearer key) plus the model id for each capability under test. The chat model is always present (its
/// absence would have skipped the test at the attribute), whereas the vision, embedding, and reasoning models
/// are optional — when their environment variable is unset, the corresponding <c>SupportsXxx</c> flag is
/// <see langword="false"/> and the conformance helper skips that knob rather than failing it. That distinction
/// is deliberate: a backend that does not offer embeddings is an honest <em>absence</em> of a wired model, not
/// a defect in the proxy.
/// </summary>
sealed class LiveBackendConfig
{
	private LiveBackendConfig(
		string         backendName,
		BackendOptions options,
		string         chatModel,
		string?        visionModel,
		string?        embeddingModel,
		string?        reasoningModel)
	{
		BackendName = backendName;
		Options = options;
		ChatModel = chatModel;
		VisionModel = visionModel;
		EmbeddingModel = embeddingModel;
		ReasoningModel = reasoningModel;
	}

	/// <summary>Gets the logical backend name used in the <c>BackendContext</c> and the options map.</summary>
	public string BackendName { get; }

	/// <summary>Gets the backend options the provider under test resolves its client and credentials from.</summary>
	public BackendOptions Options { get; }

	/// <summary>Gets the chat/tool model id. Always present for a non-skipped live test.</summary>
	public string ChatModel { get; }

	/// <summary>Gets the vision model id, or <see langword="null"/> when vision is not wired for this backend.</summary>
	public string? VisionModel { get; }

	/// <summary>Gets the embedding model id, or <see langword="null"/> when embeddings are not wired.</summary>
	public string? EmbeddingModel { get; }

	/// <summary>Gets the reasoning model id, or <see langword="null"/> when reasoning is not wired.</summary>
	public string? ReasoningModel { get; }

	/// <summary>Gets a value indicating whether a vision model is wired and the vision knob should run.</summary>
	public bool SupportsVision => VisionModel is not null;

	/// <summary>Gets a value indicating whether an embedding model is wired and the embeddings knob should run.</summary>
	public bool SupportsEmbeddings => EmbeddingModel is not null;

	/// <summary>Gets a value indicating whether a reasoning model is wired and the reasoning knob should run.</summary>
	public bool SupportsReasoning => ReasoningModel is not null;

	/// <summary>
	/// Resolves the backend's configuration from the environment described by <paramref name="descriptor"/>,
	/// applying the descriptor's defaults for the base URL and chat model when those variables are unset.
	/// </summary>
	/// <param name="descriptor">The variable names and defaults describing the backend to resolve.</param>
	/// <returns>The resolved live configuration.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="descriptor"/> is <see langword="null"/>.</exception>
	/// <exception cref="InvalidOperationException">
	/// The required API key variable (<see cref="LiveBackendDescriptor.ApiKeyEnv"/>) is absent. A gated live
	/// test never reaches this, but calling the factory without the gate surfaces the misconfiguration loudly
	/// rather than building a half-configured backend.
	/// </exception>
	public static LiveBackendConfig Create(LiveBackendDescriptor descriptor)
	{
		ArgumentNullException.ThrowIfNull(descriptor);

		string apiKey = LiveEnvironment.Get(descriptor.ApiKeyEnv)
		                ?? throw new InvalidOperationException(
			                $"Cannot build live backend '{descriptor.BackendName}': the API key environment " +
			                $"variable '{descriptor.ApiKeyEnv}' is not set. This factory must be called only " +
			                "from a test gated by the same variable.");

		BackendOptions options = new()
		{
			BaseUrl = LiveEnvironment.GetOrDefault(descriptor.BaseUrlEnv, descriptor.DefaultBaseUrl),
			ProviderType = descriptor.ProviderType,
			ApiKey = apiKey
		};

		return new LiveBackendConfig(
			descriptor.BackendName,
			options,
			LiveEnvironment.GetOrDefault(descriptor.ChatModelEnv, descriptor.DefaultChatModel),
			LiveEnvironment.Get(descriptor.VisionModelEnv),
			LiveEnvironment.Get(descriptor.EmbeddingModelEnv),
			LiveEnvironment.Get(descriptor.ReasoningModelEnv));
	}

	/// <summary>
	/// Wraps this backend in an <see cref="IOptions{TOptions}"/> over a <see cref="ProxyOptions"/> carrying it
	/// as the sole registered backend, as the provider adapter constructors expect.
	/// </summary>
	/// <returns>The options instance to hand to a provider under test.</returns>
	public IOptions<ProxyOptions> ToProxyOptions() => Microsoft.Extensions.Options.Options.Create(
		new ProxyOptions { Backends = { [BackendName] = Options } });
}
