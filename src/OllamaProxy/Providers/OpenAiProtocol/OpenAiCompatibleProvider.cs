// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Options;

using OllamaProxy.Configuration;
using OllamaProxy.Contracts.Ollama;
using OllamaProxy.Diagnostics;
using OllamaProxy.Providers.Abstractions;
using OllamaProxy.Providers.Http;
using OllamaProxy.Providers.OpenAiProtocol.Contracts;
using OllamaProxy.Providers.OpenAiProtocol.Mapping;
using OllamaProxy.Providers.OpenAiProtocol.Streaming;

namespace OllamaProxy.Providers.OpenAiProtocol;

/// <summary>
/// The shared base for every OpenAI-compatible <see cref="IProviderAdapter"/>. It implements the
/// entire protocol surface common to backends that speak the OpenAI REST format
/// (<c>/chat/completions</c>, <c>/embeddings</c>, <c>/models</c>): request and response translation,
/// streaming, model discovery across the several context-length and capability dialects, the verbatim
/// <see cref="IOpenAiForwarder"/> passthrough, and the shared JSON transport helpers. Everything that
/// differs between vendors is funnelled through a single seam, <see cref="ApplyReasoning"/>, so a
/// concrete provider only declares its <see cref="ProviderType"/> and, when its reasoning dialect
/// deviates from the standard <c>reasoning_effort</c> field, overrides that one method. The type is
/// stateless beyond its injected collaborators and therefore safe to register as a singleton.
/// </summary>
// The capability logging in DetermineCapabilitiesAsync runs once per model during startup discovery, so
// the LoggerMessage delegate ceremony (CA1848) and the lazy-evaluation guard (CA1873) buy nothing. The
// log arguments (model id, backend name) are already-materialized strings; the message template is a
// short constant concatenation with no interpolation to defer.
[SuppressMessage(
	"Performance",
	"CA1848:Use the LoggerMessage delegates",
	Justification = "Startup-only discovery logging; the LoggerMessage delegate ceremony is not worth it here.")]
[SuppressMessage(
	"Performance",
	"CA1873:Avoid potentially expensive logging",
	Justification = "Startup-only discovery logging with already-materialized arguments.")]
abstract class OpenAiCompatibleProvider : IProviderAdapter, IOpenAiForwarder
{
	private const string ChatCompletionsPath = "chat/completions";
	private const string EmbeddingsPath      = "embeddings";
	private const string ModelsPath          = "models";

	private readonly Dictionary<string, BackendOptions> mBackends;
	private readonly ICapabilityProber                  mCapabilityProber;
	private readonly ILogger                            mLogger;

	private readonly IBackendHttpClientProvider mHttpClientProvider;
	private readonly TimeProvider               mTimeProvider;
	private readonly IRequestTraceAccessor      mTraceAccessor;
	private readonly IReasoningDetailsCache     mReasoningDetailsCache;

	/// <summary>
	/// Initializes a new instance of the <see cref="OpenAiCompatibleProvider"/> class.
	/// </summary>
	/// <param name="httpClientProvider">Supplies the pre-configured per-backend HTTP clients.</param>
	/// <param name="capabilityProber">Actively probes a backend when a model carries no metadata capabilities.</param>
	/// <param name="timeProvider">The clock used for response timestamps and duration measurement.</param>
	/// <param name="options">The validated proxy options carrying each backend's reasoning default.</param>
	/// <param name="traceAccessor">Provides the ambient request trace for provenance recording.</param>
	/// <param name="reasoningDetailsCache">Carries opaque <c>reasoning_details</c> blobs across a tool-call conversation.</param>
	/// <param name="logger">The logger used to surface capabilities that stayed inconclusive after probing.</param>
	/// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
	protected OpenAiCompatibleProvider(
		IBackendHttpClientProvider httpClientProvider,
		ICapabilityProber          capabilityProber,
		TimeProvider               timeProvider,
		IOptions<ProxyOptions>     options,
		IRequestTraceAccessor      traceAccessor,
		IReasoningDetailsCache     reasoningDetailsCache,
		ILogger                    logger)
	{
		ArgumentNullException.ThrowIfNull(httpClientProvider);
		ArgumentNullException.ThrowIfNull(capabilityProber);
		ArgumentNullException.ThrowIfNull(timeProvider);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(traceAccessor);
		ArgumentNullException.ThrowIfNull(reasoningDetailsCache);
		ArgumentNullException.ThrowIfNull(logger);

		mHttpClientProvider = httpClientProvider;
		mCapabilityProber = capabilityProber;
		mTimeProvider = timeProvider;
		mTraceAccessor = traceAccessor;
		mReasoningDetailsCache = reasoningDetailsCache;
		mLogger = logger;
		mBackends = new Dictionary<string, BackendOptions>(options.Value.Backends, StringComparer.OrdinalIgnoreCase);
	}

	/// <inheritdoc/>
	public abstract string ProviderType { get; }

	/// <inheritdoc/>
	public async IAsyncEnumerable<OllamaChatResponse> StreamChatAsync(
		BackendContext                             backend,
		string                                     upstreamModel,
		OllamaChatRequest                          request,
		ReasoningEffort?                           pinnedEffort,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(backend);
		ArgumentNullException.ThrowIfNull(request);

		JsonObject payload = BuildChatPayload(backend, request, upstreamModel, pinnedEffort, stream: true);
		long startTimestamp = mTimeProvider.GetTimestamp();

		using HttpClient client = mHttpClientProvider.CreateClient(backend.Name);
		using HttpRequestMessage httpRequest = CreateJsonNodeRequest(ChatCompletionsPath, payload);

		using HttpResponseMessage response = await client
			                                     .SendAsync(
				                                     httpRequest,
				                                     HttpCompletionOption.ResponseHeadersRead,
				                                     cancellationToken)
			                                     .ConfigureAwait(false);

		await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

		Stream upstream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		await using (upstream.ConfigureAwait(false))
		{
			IAsyncEnumerable<OpenAiChatCompletionChunk> chunks =
				ServerSentEventReader.ReadChunksAsync(upstream, OpenAiSerialization.Options, cancellationToken);

			// Tee the raw chunk stream to capture any opaque reasoning_details blob before translation drops
			// it; the latest non-null observation is the one stored. A backend that never emits the field
			// produces no observation, so the tee is harmless on a non-reasoning stream.
			JsonNode? capturedDetails = null;
			chunks = ObserveReasoningDetails(chunks, details => capturedDetails = details, cancellationToken);

			IAsyncEnumerable<OllamaChatResponse> translated = OpenAiStreamTranslator.TranslateAsync(
				chunks,
				request.Model,
				() => FormatTimestamp(mTimeProvider.GetUtcNow()),
				() => ElapsedNanoseconds(startTimestamp),
				cancellationToken);

			// Assemble the streamed deltas into the single text the response carried so a trace records what
			// the model actually said, not the per-token chunk wire format. Reasoning (chain-of-thought)
			// deltas are gathered into a second buffer and recorded under their own stage. The finally
			// guarantees the text gathered so far is recorded even when the client disconnects mid-stream.
			StringBuilder assembled = new();
			StringBuilder reasoning = new();
			IReadOnlyList<OllamaToolCall>? terminalToolCalls = null;
			try
			{
				await foreach (OllamaChatResponse chunk in translated.ConfigureAwait(false))
				{
					assembled.Append(chunk.Message.Content);
					reasoning.Append(chunk.Message.Thinking);

					// The terminal chunk carries the assembled tool calls; remember them so the finally can
					// derive the correlation key the captured reasoning-details blob is stored under.
					if (chunk.Message.ToolCalls is { Count: > 0 } calls) terminalToolCalls = calls;

					yield return chunk;
				}
			}
			finally
			{
				// Store the captured reasoning-details blob keyed by the streamed turn's tool calls so the
				// follow-up request can replay it. A no-op when no blob was seen or the turn carried no tool
				// calls; the backend name scopes the key so the shared cache never crosses backends.
				StoreReasoningDetails(backend, capturedDetails, terminalToolCalls);

				// Only emit a reasoning entry when the backend actually streamed reasoning, so non-reasoning
				// responses do not litter the trace with an empty stage.
				if (reasoning.Length > 0)
					mTraceAccessor.Current.RecordBackendReasoning(backend.Name, reasoning.ToString());
				mTraceAccessor.Current.RecordBackendResponse(backend.Name, assembled.ToString());
			}
		}
	}

	/// <inheritdoc/>
	public async Task<OllamaChatResponse> CompleteChatAsync(
		BackendContext    backend,
		string            upstreamModel,
		OllamaChatRequest request,
		ReasoningEffort?  pinnedEffort,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(backend);
		ArgumentNullException.ThrowIfNull(request);

		JsonObject payload = BuildChatPayload(backend, request, upstreamModel, pinnedEffort, stream: false);
		long startTimestamp = mTimeProvider.GetTimestamp();

		using HttpClient client = mHttpClientProvider.CreateClient(backend.Name);
		using HttpRequestMessage httpRequest = CreateJsonNodeRequest(ChatCompletionsPath, payload);

		using HttpResponseMessage response = await client
			                                     .SendAsync(
				                                     httpRequest,
				                                     HttpCompletionOption.ResponseHeadersRead,
				                                     cancellationToken)
			                                     .ConfigureAwait(false);

		await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

		OpenAiChatCompletion completion = await ReadJsonAsync<OpenAiChatCompletion>(response, cancellationToken)
			                                  .ConfigureAwait(false);

		OpenAiChatMessage? responseMessage = completion.Choices is { Count: > 0 } choices ? choices[0].Message : null;

		// Capture the opaque reasoning-details blob (when the backend returned one) before the response is
		// projected to Ollama, which drops the field; the follow-up request re-attaches it by tool-call
		// correlation. A no-op when the turn carries no blob.
		CaptureReasoningDetails(backend, responseMessage);

		// Record the upstream response so a trace pairs the backend request with what came back; no-op when
		// the request is not traced. Reasoning, when present, is recorded under its own stage first so the
		// trace mirrors the streaming and /v1 paths.
		string? reasoning = OpenAiMessageConverter.ExtractReasoning(responseMessage);
		if (reasoning is not null) mTraceAccessor.Current.RecordBackendReasoning(backend.Name, reasoning);
		mTraceAccessor.Current.RecordBackendResponse(
			backend.Name,
			JsonSerializer.Serialize(completion, OpenAiSerialization.Options));

		return OpenAiResponseMapper.MapCompletion(
			completion,
			request.Model,
			FormatTimestamp(mTimeProvider.GetUtcNow()),
			ElapsedNanoseconds(startTimestamp));
	}

	/// <inheritdoc/>
	public async Task<OllamaEmbedResponse> CreateEmbeddingsAsync(
		BackendContext     backend,
		string             upstreamModel,
		OllamaEmbedRequest request,
		CancellationToken  cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(backend);
		ArgumentNullException.ThrowIfNull(request);

		OpenAiEmbeddingRequest payload = new(upstreamModel, request.Input);

		using HttpClient client = mHttpClientProvider.CreateClient(backend.Name);
		using HttpRequestMessage httpRequest = CreateJsonRequest(EmbeddingsPath, payload);

		using HttpResponseMessage response = await client
			                                     .SendAsync(
				                                     httpRequest,
				                                     HttpCompletionOption.ResponseHeadersRead,
				                                     cancellationToken)
			                                     .ConfigureAwait(false);

		await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

		OpenAiEmbeddingResponse embeddings = await ReadJsonAsync<OpenAiEmbeddingResponse>(response, cancellationToken)
			                                     .ConfigureAwait(false);

		IReadOnlyList<IReadOnlyList<float>> vectors = (embeddings.Data ?? [])
			.OrderBy(entry => entry.Index)
			.Select(entry => entry.Embedding)
			.ToArray();

		return new OllamaEmbedResponse(
			Model: request.Model,
			Embeddings: vectors,
			PromptEvalCount: embeddings.Usage?.PromptTokens);
	}

	/// <summary>
	/// Lists the models the backend offers. Each provider declares its own implementation because the
	/// model-listing schema is the chief point of vendor divergence: although <c>GET /models</c> is
	/// shared, the per-entry shape (context-length fields, capability metadata, vendor blocks) differs
	/// per provider. The implementation typically delegates the HTTP transport to
	/// <see cref="DiscoverModelsCoreAsync{TModel}"/> and supplies only the provider-specific entry
	/// contract and projection, so no provider-specific knowledge leaks into the shared base.
	/// </summary>
	/// <param name="backend">The backend to query.</param>
	/// <param name="cancellationToken">A token that may be used to cancel the operation.</param>
	/// <returns>The discovered models; empty when the backend reports none.</returns>
	public abstract Task<IReadOnlyList<DiscoveredModel>> DiscoverModelsAsync(
		BackendContext    backend,
		CancellationToken cancellationToken);

	/// <summary>
	/// Performs the shared <c>GET /models</c> transport (issuing the request, validating the response,
	/// and deserializing the provider-specific listing envelope) then projects each entry onto the
	/// provider-neutral <see cref="DiscoveredModel"/> with the supplied projection. This is the single
	/// seam every provider's <see cref="DiscoverModelsAsync"/> builds on: the base owns the wire
	/// mechanics, while <typeparamref name="TModel"/> and <paramref name="project"/> carry the entire
	/// provider-specific schema knowledge.
	/// </summary>
	/// <typeparam name="TModel">The provider-specific model-entry contract the listing deserializes into.</typeparam>
	/// <param name="backend">The backend to query.</param>
	/// <param name="project">Projects one provider-specific entry onto the neutral discovered model.</param>
	/// <param name="cancellationToken">A token that may be used to cancel the operation.</param>
	/// <returns>The discovered models; empty when the backend reports none.</returns>
	/// <exception cref="ArgumentNullException">
	/// <paramref name="backend"/> or <paramref name="project"/> is <see langword="null"/>.
	/// </exception>
	protected async Task<IReadOnlyList<DiscoveredModel>> DiscoverModelsCoreAsync<TModel>(
		BackendContext                backend,
		Func<TModel, DiscoveredModel> project,
		CancellationToken             cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(backend);
		ArgumentNullException.ThrowIfNull(project);

		using HttpClient client = mHttpClientProvider.CreateClient(backend);

		using HttpResponseMessage response = await client
			                                     .GetAsync(
				                                     ModelsPath,
				                                     HttpCompletionOption.ResponseHeadersRead,
				                                     cancellationToken)
			                                     .ConfigureAwait(false);

		await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

		OpenAiModelsResponse<TModel> models =
			await ReadJsonAsync<OpenAiModelsResponse<TModel>>(response, cancellationToken).ConfigureAwait(false);

		return (models.Data ?? [])
			.Select(project)
			.ToArray();
	}

	/// <inheritdoc/>
	public async Task<ModelCapabilities> DetermineCapabilitiesAsync(
		BackendContext    backend,
		DiscoveredModel   model,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(backend);
		ArgumentNullException.ThrowIfNull(model);

		// A provider that derived capabilities from its native listing during discovery projection has
		// already given an authoritative answer; honor it and skip the round trips entirely.
		if (model.Capabilities is { } fromMetadata) return fromMetadata;

		// No metadata signal: actively probe. Start from the conservative completion-only baseline and let
		// each conclusive probe refine its own flag. The probes run SEQUENTIALLY in a fixed
		// completion -> tool -> vision -> embedding order, not concurrently. The completion probe goes first
		// and loads the model into memory; the tool, vision and embedding probes that follow then run against
		// an already-warm, idle model and each gets the full per-attempt timeout to itself. Names are never
		// used to guess capabilities.
		CapabilityProbingOptions probing = GetProbingOptions(backend);

		// The conservative baseline is that the model supports completion only, with all flags defaulting to
		// false and the source set to "default" to indicate that no metadata or probes contributed. Each
		// conclusive probe result refines one flag and updates the source to "probed" to reflect that active
		// detection was required to determine that capability.
		ModelCapabilities capabilities = ModelCapabilities.CompletionOnly;

		// Capability: Completion. Probed first so its round trip warms the model for the probes that follow.
		// Unlike the other three, completion is fail-open: only a conclusive probe changes it (raising or, for
		// an embedding-only model, lowering it). An inconclusive result leaves the true baseline untouched so
		// a transient hiccup can never hide a working chat model.
		if (probing.ProbeCompletion)
		{
			bool? completionSupport = await mCapabilityProber
				                          .ProbeCompletionSupportAsync(backend, model.Id, cancellationToken)
				                          .ConfigureAwait(false);

			if (completionSupport is { } support)
			{
				capabilities = capabilities with
				{
					SupportsCompletion = support,
					Source = CapabilitySource.Probed
				};
			}
			else
			{
				// The probe could not confirm completion support, but completion is fail-open: SupportsCompletion
				// stays at its true baseline so a transient hiccup never hides a working chat model. Record that
				// this true is unmeasured (a flag, not a denial) so the admin surface can show the capability as
				// "supported but unconfirmed" rather than a plain confirmed capability; the model stays exposed.
				capabilities = capabilities with
				{
					Inconclusive = capabilities.Inconclusive | InconclusiveCapabilities.Completion
				};
				LogInconclusiveCompletionProbe(backend.Name, model.Id);
			}
		}

		// Capability: Tool Support
		if (probing.ProbeTools)
		{
			bool? toolSupport = await mCapabilityProber
				                    .ProbeToolSupportAsync(backend, model.Id, cancellationToken)
				                    .ConfigureAwait(false);

			if (toolSupport is { } support)
			{
				capabilities = capabilities with
				{
					SupportsTools = support,
					Source = CapabilitySource.Probed
				};
			}
			else
			{
				// The probe could not confirm tool support, so SupportsTools stays at its conservative false
				// baseline. Record that this false is unmeasured (a flag, not a denial) so the admin surface can
				// show "probe inconclusive" rather than a misleading "unsupported".
				capabilities = capabilities with
				{
					Inconclusive = capabilities.Inconclusive | InconclusiveCapabilities.Tools
				};
				LogInconclusiveOptionalProbe("tool", backend.Name, model.Id);
			}
		}

		// Capability: Vision Support
		if (probing.ProbeVision)
		{
			bool? visionSupport = await mCapabilityProber
				                      .ProbeVisionSupportAsync(backend, model.Id, cancellationToken)
				                      .ConfigureAwait(false);

			if (visionSupport is { } support)
			{
				capabilities = capabilities with
				{
					SupportsVision = support,
					Source = CapabilitySource.Probed
				};
			}
			else
			{
				// The probe could not confirm vision support, so SupportsVision stays at its conservative false
				// baseline. Record that this false is unmeasured so the admin surface can show "probe
				// inconclusive" rather than a misleading "unsupported".
				capabilities = capabilities with
				{
					Inconclusive = capabilities.Inconclusive | InconclusiveCapabilities.Vision
				};
				LogInconclusiveOptionalProbe("vision", backend.Name, model.Id);
			}
		}

		// Capability: Embedding Support
		if (probing.ProbeEmbeddings)
		{
			bool? embeddingSupport = await mCapabilityProber
				                         .ProbeEmbeddingSupportAsync(backend, model.Id, cancellationToken)
				                         .ConfigureAwait(false);

			if (embeddingSupport is { } support)
			{
				capabilities = capabilities with
				{
					SupportsEmbeddings = support,
					Source = CapabilitySource.Probed
				};
			}
			else
			{
				// The probe could not confirm embedding support, so SupportsEmbeddings stays at its conservative
				// false baseline. Record that this false is unmeasured so the admin surface can show "probe
				// inconclusive" rather than a misleading "unsupported".
				capabilities = capabilities with
				{
					Inconclusive = capabilities.Inconclusive | InconclusiveCapabilities.Embeddings
				};
				LogInconclusiveOptionalProbe("embedding", backend.Name, model.Id);
			}
		}

		// The (possibly probe-refined) conservative baseline is the final result.
		return capabilities;
	}

	/// <summary>
	/// Resolves the probing options for the supplied backend context. A draft context carries its own
	/// inline probing options and is authoritative, so no committed entry exists to look up. A committed
	/// context is resolved by name, returning a default (probes enabled) when the backend is not found
	/// (a defensive case that should not occur for a discovered model).
	/// </summary>
	/// <param name="backend">The backend context whose probing options are resolved.</param>
	/// <returns>The backend's probing options, or a default instance.</returns>
	private CapabilityProbingOptions GetProbingOptions(BackendContext backend)
	{
		return backend.Draft?.Probing != null
			       ? backend.Draft.Probing
			       : mBackends.TryGetValue(backend.Name, out BackendOptions? committed)
				       ? committed.Probing
				       : new CapabilityProbingOptions();
	}

	/// <summary>
	/// Logs that the completion probe stayed inconclusive after the prober's retries, so the model keeps
	/// its conservative completion-capable baseline. Logged at information level because completion
	/// remaining <see langword="true"/> is the safe, non-lossy outcome.
	/// </summary>
	/// <param name="backendName">The backend the probe targeted.</param>
	/// <param name="modelId">The model whose completion support could not be confirmed.</param>
	private void LogInconclusiveCompletionProbe(string backendName, string modelId) => mLogger.LogInformation(
		"Completion-support probe for model {Model} on backend {Backend} stayed inconclusive after " +
		"retries; keeping the conservative completion-capable default. The model stays exposed; expose " +
		"it via the registry instead if its capabilities must be fixed explicitly.",
		modelId,
		backendName);

	/// <summary>
	/// Logs that an optional (tool, vision, or embedding) probe stayed inconclusive after the prober's
	/// retries, so the model under-reports that capability. Logged at warning level because the proxy is
	/// withholding a capability the model might actually have, which an operator may want to pin.
	/// </summary>
	/// <param name="capability">The probed capability that could not be confirmed.</param>
	/// <param name="backendName">The backend the probe targeted.</param>
	/// <param name="modelId">The model whose capability could not be confirmed.</param>
	private void LogInconclusiveOptionalProbe(string capability, string backendName, string modelId) =>
		mLogger.LogWarning(
			"{Capability}-support probe for model {Model} on backend {Backend} stayed inconclusive after " +
			"retries; advertising it as unsupported. Pin the capability in the 'Models' registry if the " +
			"model supports it.",
			capability,
			modelId,
			backendName);

	/// <inheritdoc/>
	public async Task<JsonObject> ForwardJsonAsync(
		BackendContext    backend,
		string            path,
		JsonObject        body,
		ReasoningEffort?  pinnedEffort,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(backend);
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentNullException.ThrowIfNull(body);

		// Apply the reasoning policy before forwarding so the verbatim passthrough matches the Ollama-native
		// path: a pinned effort wins over everything, else the backend default applies unless the client set one.
		ApplyPassthroughReasoning(backend, path, body, pinnedEffort);

		// Enforce the provider's forced vendor parameters (e.g. Venice's include_venice_system_prompt=false)
		// on the passthrough too, so the /v1 route keeps the same chassis guarantees as the Ollama-native path.
		ApplyPassthroughVendorParameters(backend, path, body);

		// Record the forwarded request so a /v1 trace pairs what the client sent with what came back; the
		// passthrough bypasses BuildChatPayload, so it must record its own request body. No-op when untraced.
		ITraceScope trace = mTraceAccessor.Current;
		trace.RecordBackendRequest(backend.Name, path, body.ToJsonString(OpenAiSerialization.Options));

		using HttpClient client = mHttpClientProvider.CreateClient(backend.Name);
		using HttpRequestMessage httpRequest = CreateJsonNodeRequest(path, body);

		using HttpResponseMessage response = await client
			                                     .SendAsync(
				                                     httpRequest,
				                                     HttpCompletionOption.ResponseHeadersRead,
				                                     cancellationToken)
			                                     .ConfigureAwait(false);

		await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

		JsonObject result = await ReadJsonObjectAsync(response, cancellationToken).ConfigureAwait(false);

		// Record reasoning under its own stage when the passthrough response carries it, so a /v1 trace is
		// consistent with the streaming and Ollama-native paths. The full JSON still lands in BackendResponse.
		string? reasoning = ExtractReasoningFromResponseObject(result);
		if (reasoning is not null) trace.RecordBackendReasoning(backend.Name, reasoning);
		trace.RecordBackendResponse(backend.Name, result.ToJsonString(OpenAiSerialization.Options));

		return result;
	}

	/// <inheritdoc/>
	public async IAsyncEnumerable<string> ForwardSseAsync(
		BackendContext                             backend,
		string                                     path,
		JsonObject                                 body,
		ReasoningEffort?                           pinnedEffort,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(backend);
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentNullException.ThrowIfNull(body);

		// Apply the reasoning policy before forwarding so the verbatim passthrough matches the Ollama-native
		// path: a pinned effort wins over everything, else the backend default applies unless the client set one.
		ApplyPassthroughReasoning(backend, path, body, pinnedEffort);

		// Enforce the provider's forced vendor parameters (e.g. Venice's include_venice_system_prompt=false)
		// on the passthrough too, so the /v1 route keeps the same chassis guarantees as the Ollama-native path.
		ApplyPassthroughVendorParameters(backend, path, body);

		// Record the forwarded request so a /v1 trace pairs what the client sent with what came back; the
		// passthrough bypasses BuildChatPayload, so it must record its own request body. No-op when untraced.
		ITraceScope trace = mTraceAccessor.Current;
		trace.RecordBackendRequest(backend.Name, path, body.ToJsonString(OpenAiSerialization.Options));

		using HttpClient client = mHttpClientProvider.CreateClient(backend.Name);
		using HttpRequestMessage httpRequest = CreateJsonNodeRequest(path, body);

		using HttpResponseMessage response = await client
			                                     .SendAsync(
				                                     httpRequest,
				                                     HttpCompletionOption.ResponseHeadersRead,
				                                     cancellationToken)
			                                     .ConfigureAwait(false);

		await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

		Stream upstream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		await using (upstream.ConfigureAwait(false))
		{
			IAsyncEnumerable<string> payloads =
				RawServerSentEventReader.ReadDataPayloadsAsync(upstream, cancellationToken);

			// Assemble the streamed deltas into the single text the response carried so a trace records what
			// the model actually said, not the raw SSE frames. Reasoning (chain-of-thought) deltas are
			// gathered into a second buffer and recorded under their own stage. The finally guarantees the
			// text gathered so far is recorded even when the client disconnects mid-stream.
			StringBuilder assembled = new();
			StringBuilder reasoning = new();
			try
			{
				await foreach (string payload in payloads.ConfigureAwait(false))
				{
					AppendSseDeltaText(assembled, reasoning, payload);
					yield return payload;
				}
			}
			finally
			{
				// Only emit a reasoning entry when the backend actually streamed reasoning, so non-reasoning
				// responses do not litter the trace with an empty stage.
				if (reasoning.Length > 0) trace.RecordBackendReasoning(backend.Name, reasoning.ToString());
				trace.RecordBackendResponse(backend.Name, assembled.ToString());
			}
		}
	}

	/// <summary>
	/// Appends the textual delta carried by one raw SSE chunk payload to <paramref name="assembled"/>,
	/// reading the first choice's <c>delta.content</c> (chat completions) or <c>text</c> (legacy
	/// completions), and appends any reasoning delta (<c>delta.reasoning_content</c>, or OpenRouter's
	/// <c>delta.reasoning</c>) to <paramref name="reasoning"/>. A payload that is not a JSON object
	/// (including the <c>[DONE]</c> sentinel, which the reader already excludes) contributes nothing.
	/// Parsing is best-effort: a malformed frame is ignored rather than aborting the assembled trace
	/// text, since the assembly is a diagnostic artifact, not a contract surface.
	/// </summary>
	/// <param name="assembled">The builder accumulating the visible response text.</param>
	/// <param name="reasoning">The builder accumulating the reasoning (chain-of-thought) text.</param>
	/// <param name="payload">The raw JSON payload of one SSE <c>data:</c> frame.</param>
	private static void AppendSseDeltaText(StringBuilder assembled, StringBuilder reasoning, string payload)
	{
		JsonNode? node;
		try
		{
			node = JsonNode.Parse(payload);
		}
		catch (JsonException)
		{
			return;
		}

		if (node is not JsonObject chunk ||
		    chunk["choices"] is not JsonArray choices ||
		    choices.Count == 0 ||
		    choices[0] is not JsonObject choice)
			return;

		// Chat completions carry the increment under delta.content; legacy completions use a flat text
		// field. ExtractText handles both the bare-string and content-part shapes of delta.content.
		if (choice["delta"] is JsonObject delta)
		{
			assembled.Append(OpenAiMessageConverter.ExtractText(delta["content"]));

			// Reasoning streams arrive on a parallel field: reasoning_content is the de-facto standard
			// (DeepSeek, vLLM, llama.cpp); OpenRouter exposes the same stream as plain reasoning.
			reasoning.Append(OpenAiMessageConverter.ExtractText(delta["reasoning_content"] ?? delta["reasoning"]));
		}
		else if (choice["text"] is JsonValue text && text.TryGetValue(out string? legacy))
		{
			assembled.Append(legacy);
		}
	}

	/// <summary>
	/// Extracts the reasoning (chain-of-thought) text from a non-streaming passthrough chat-completion
	/// response object, reading the first choice's <c>message.reasoning_content</c> (de-facto standard)
	/// or <c>message.reasoning</c> (OpenRouter). Returns <see langword="null"/> when the response carries
	/// no reasoning, so the caller can omit the trace stage rather than record an empty one.
	/// </summary>
	/// <param name="response">The parsed passthrough response object.</param>
	/// <returns>The reasoning text, or <see langword="null"/> when none is present.</returns>
	private static string? ExtractReasoningFromResponseObject(JsonObject response)
	{
		if (response["choices"] is not JsonArray choices ||
		    choices.Count == 0 ||
		    choices[0] is not JsonObject choice ||
		    choice["message"] is not JsonObject message)
		{
			return null;
		}

		JsonNode? reasoning = message["reasoning_content"] ?? message["reasoning"];
		return reasoning is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrEmpty(text)
			       ? text
			       : null;
	}

	/// <summary>
	/// Gets the highest reasoning-effort token this provider's API accepts, its <em>dialect ceiling</em>.
	/// The base value is <see cref="ReasoningEffort.XHigh"/>, the top of OpenAI's published vocabulary; a
	/// provider whose API accepts a higher token overrides this to raise it. It is purely a
	/// <em>token-validity</em> bound: it guarantees the proxy never emits a level the provider's API would
	/// reject for being unknown, but it cannot know whether a specific model accepts the level. Only a
	/// registry pin gives that guarantee, which is why a pinned effort bypasses this ceiling and is sent
	/// verbatim.
	/// </summary>
	protected virtual ReasoningEffort MaxDialectReasoningEffort => ReasoningEffort.XHigh;

	/// <summary>
	/// Clamps a resolved effort down to this provider's <see cref="MaxDialectReasoningEffort"/> dialect ceiling,
	/// so a non-pinned level the provider's API does not recognize is lowered to its nearest accepted token
	/// rather than rejected. The <see cref="ReasoningEffort"/> values are ordered weakest-to-strongest, so the
	/// clamp is a simple minimum. This is applied only to request- and backend-default-sourced efforts; a pinned
	/// effort is operator-authoritative and bypasses it.
	/// </summary>
	/// <param name="effort">The resolved effort to bound.</param>
	/// <returns>The effort, lowered to the dialect ceiling when it exceeds it.</returns>
	private ReasoningEffort ClampToDialect(ReasoningEffort effort) =>
		effort > MaxDialectReasoningEffort ? MaxDialectReasoningEffort : effort;

	/// <summary>
	/// Captures a non-streamed tool-calling turn's opaque <c>reasoning_details</c> blob into the round-trip
	/// cache so the follow-up request can replay it. Reads both the blob and the tool calls that anchor the
	/// correlation key from the single response message, then delegates to <see cref="StoreReasoningDetails"/>.
	/// A no-op for any backend that did not return the field.
	/// </summary>
	/// <param name="backend">The backend that produced the response, whose name scopes the cache key.</param>
	/// <param name="message">The upstream assistant message, or <see langword="null"/> when none was returned.</param>
	private void CaptureReasoningDetails(BackendContext backend, OpenAiChatMessage? message)
	{
		if (message?.ReasoningDetails is not { } details) return;

		StoreReasoningDetails(backend, details, OpenAiMessageConverter.ConvertToolCalls(message.ToolCalls));
	}

	/// <summary>
	/// Stores an opaque <c>reasoning_details</c> blob in the round-trip cache, keyed by a content hash of the
	/// originating backend's name plus the supplied tool calls (<see cref="ReasoningDetailsCorrelation"/>):
	/// the one part of the assistant turn a client must replay verbatim for tool calling to function, so the
	/// key survives a client that strips every other non-standard field. A no-op unless a blob is present and
	/// the turn carries tool calls (a turn without them has no successor request to replay the blob on, so
	/// there is nothing to preserve). A disabled cache turns <see cref="IReasoningDetailsCache.Store"/> itself
	/// into a no-op, so this method does not re-check the global switch.
	/// </summary>
	/// <param name="backend">The backend that produced the blob, whose name scopes the cache key.</param>
	/// <param name="details">The opaque reasoning-details node, or <see langword="null"/> when none was returned.</param>
	/// <param name="toolCalls">The turn's tool calls, in Ollama shape, used to derive the correlation key.</param>
	private void StoreReasoningDetails(
		BackendContext                 backend,
		JsonNode?                      details,
		IReadOnlyList<OllamaToolCall>? toolCalls)
	{
		if (details is null) return;
		if (ReasoningDetailsCorrelation.TryComputeKey(backend.Name, toolCalls) is not { } key) return;

		mReasoningDetailsCache.Store(key, details);
	}

	/// <summary>
	/// Passes a streamed chunk sequence through unchanged while observing each delta's opaque
	/// <c>reasoning_details</c>, invoking <paramref name="onDetails"/> with the most recent non-null blob. A
	/// backend that never emits the field simply yields no observation, so the tee is harmless on a
	/// non-reasoning stream. Assumption (not yet measured against a live backend): a backend that does emit it
	/// sends the complete <c>reasoning_details</c> array on a single delta (typically the terminal one
	/// carrying the tool calls) rather than as fragments spread across deltas; the last non-null blob is
	/// therefore the complete one. If a backend is observed to fragment it, this is where reassembly would be
	/// added.
	/// </summary>
	/// <param name="source">The upstream chunk sequence to observe and forward.</param>
	/// <param name="onDetails">Invoked with each non-null reasoning-details node as it is seen.</param>
	/// <param name="cancellationToken">A token observed while consuming the source sequence.</param>
	/// <returns>The same chunks, in order, as a pass-through sequence.</returns>
	private static async IAsyncEnumerable<OpenAiChatCompletionChunk> ObserveReasoningDetails(
		IAsyncEnumerable<OpenAiChatCompletionChunk> source,
		Action<JsonNode>                            onDetails,
		[EnumeratorCancellation] CancellationToken  cancellationToken)
	{
		await foreach (OpenAiChatCompletionChunk chunk in source
			               .WithCancellation(cancellationToken)
			               .ConfigureAwait(false))
		{
			if (chunk.Choices is { Count: > 0 } choices && choices[0].Delta?.ReasoningDetails is { } details)
				onDetails(details);

			yield return chunk;
		}
	}

	/// <summary>
	/// Re-attaches cached <c>reasoning_details</c> blobs onto the outgoing payload's assistant messages so a
	/// backend receives back the opaque reasoning it emitted on an earlier tool-calling turn. For each inbound
	/// assistant message that carries tool calls, the correlation key is recomputed from the backend name plus
	/// those tool calls (the same content hash the capture side stored under) and, on a cache hit, the
	/// retrieved blob is stamped onto the positionally-corresponding payload message as a raw
	/// <c>reasoning_details</c> key. The 1:1 alignment holds because the request mapper projects each inbound
	/// message onto exactly one payload message in order. Every miss (a backend that never emitted the field,
	/// a restart, a different instance, an expired entry, or a turn that was never captured) degrades
	/// gracefully by simply leaving the field off. The backend-scoped key guarantees a blob is only ever
	/// replayed to the backend that produced it.
	/// </summary>
	/// <param name="backend">The target backend, whose name scopes the correlation key.</param>
	/// <param name="payload">The outgoing chat payload whose <c>messages</c> array is stamped in place.</param>
	/// <param name="request">The inbound Ollama chat request whose assistant turns anchor the correlation keys.</param>
	private void ReattachReasoningDetails(BackendContext backend, JsonObject payload, OllamaChatRequest request)
	{
		if (payload["messages"] is not JsonArray messages) return;

		IReadOnlyList<OllamaChatMessage> inbound = request.Messages;

		// Walk the inbound messages and the payload's message array in lockstep; the mapper builds them 1:1,
		// so index i refers to the same turn on both sides. Guard the upper bound defensively in case the
		// payload was reshaped before this runs.
		int count = Math.Min(inbound.Count, messages.Count);
		for (int i = 0; i < count; i++)
		{
			OllamaChatMessage message = inbound[i];

			// Only an assistant turn that emitted tool calls can carry a reasoning-details blob; everything
			// else (user, tool result, plain assistant text) has no key and nothing to re-attach.
			if (!string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)) continue;
			if (ReasoningDetailsCorrelation.TryComputeKey(backend.Name, message.ToolCalls) is not { } key) continue;

			if (mReasoningDetailsCache.Retrieve(key) is not { } details) continue;
			if (messages[i] is not JsonObject target) continue;

			// The cache hands back a detached clone, so parenting it onto the payload is safe.
			target["reasoning_details"] = details;
		}
	}

	/// <summary>
	/// Applies the resolved reasoning effort to the outgoing chat payload using the provider's wire
	/// dialect. The base implementation writes the standard OpenAI <c>reasoning_effort</c> field, which
	/// the official OpenAI API and most compatible backends understand; a provider whose dialect differs
	/// overrides this method. It is invoked only when an effort was resolved, so an unspecified request
	/// never carries a reasoning directive.
	/// </summary>
	/// <param name="payload">The chat request body to augment in place.</param>
	/// <param name="effort">The resolved reasoning effort to encode.</param>
	/// <returns>A short description of the wire field(s) written, for provenance tracing.</returns>
	protected virtual string ApplyReasoning(JsonObject payload, ReasoningEffort effort)
	{
		payload["reasoning_effort"] = effort.ToWireValue();
		return "reasoning_effort";
	}

	/// <summary>
	/// Stamps this provider's non-standard sampling extensions onto the outgoing chat payload. The base
	/// implementation writes nothing, so the strict OpenAI dialect never carries a sampling field the
	/// official API would reject. Providers whose backend honors the de-facto <c>top_k</c>/<c>min_p</c>
	/// extensions override this to forward them, typically by delegating to <see cref="WriteTopKAndMinP"/>.
	/// </summary>
	/// <param name="payload">The chat request body to augment in place.</param>
	/// <param name="request">The inbound Ollama chat request whose options carry the extension values.</param>
	protected virtual void ApplySamplingExtensions(JsonObject payload, OllamaChatRequest request) { }

	/// <summary>
	/// Writes the de-facto <c>top_k</c> and <c>min_p</c> sampling extensions onto the payload, reading
	/// each value from the inbound Ollama options and omitting the one the client left unset rather than
	/// writing it as <see langword="null"/>. A shared helper for any provider whose backend honors this
	/// common extension pair, so the extraction logic lives in one place.
	/// </summary>
	/// <param name="payload">The chat request body to augment in place.</param>
	/// <param name="request">The inbound Ollama chat request whose options carry the extension values.</param>
	protected static void WriteTopKAndMinP(JsonObject payload, OllamaChatRequest request)
	{
		OllamaOptions? options = request.Options;
		if (options?.TopK is { } topK) payload["top_k"] = topK;
		if (options?.MinP is { } minP) payload["min_p"] = minP;
	}

	/// <summary>
	/// Writes provider-specific vendor parameters that the proxy forces on every chat request to this
	/// provider, overriding any value the client may have supplied. The base implementation is a no-op; a
	/// provider whose backend accepts vendor extensions overrides this to authoritatively write its switch
	/// set. The call is invoked after <see cref="ApplySamplingExtensions"/> and runs against the same
	/// mutable payload, so a vendor override can use the same in-place update pattern as the sampling
	/// extensions.
	/// </summary>
	/// <param name="backend">The backend the request targets, available for context the override may want to read.</param>
	/// <param name="payload">The chat request body to augment in place.</param>
	protected virtual void ApplyVendorParameters(BackendContext backend, JsonObject payload) { }

	/// <summary>
	/// Determines whether a verbatim <c>/v1</c> passthrough payload already carries a reasoning directive
	/// in this provider's wire dialect. When it does, the backend default must not override the client's
	/// explicit per-request wish. The base implementation recognizes the standard <c>reasoning_effort</c>
	/// field; a provider whose dialect adds a vendor switch overrides this to also recognize it, mirroring
	/// <see cref="ApplyReasoning"/>.
	/// </summary>
	/// <param name="payload">The inbound passthrough request body to inspect.</param>
	/// <returns><see langword="true"/> when the client already specified a reasoning directive.</returns>
	protected virtual bool HasClientReasoningDirective(JsonObject payload) => payload.ContainsKey("reasoning_effort");

	/// <summary>
	/// Removes any reasoning directive a client placed on a verbatim <c>/v1</c> passthrough payload so a
	/// pinned effort can be written cleanly without leaving a conflicting field behind (for example a flat
	/// <c>reasoning_effort</c> alongside a nested <c>reasoning.effort</c>). The base implementation removes the
	/// standard flat <c>reasoning_effort</c> field; providers whose dialect adds a vendor switch override this
	/// to also strip it, mirroring <see cref="HasClientReasoningDirective"/> and <see cref="ApplyReasoning"/>.
	/// </summary>
	/// <param name="payload">The passthrough request body to strip in place.</param>
	protected virtual void StripClientReasoningDirectives(JsonObject payload) => payload.Remove("reasoning_effort");

	/// <summary>
	/// Applies the reasoning policy to a verbatim <c>/v1</c> passthrough payload so the forwarding paths honor
	/// the same policy as the Ollama-native path, then records the resolution for provenance. Reasoning is a
	/// chat concept, so only the chat-completions path is augmented; the legacy completions and embeddings
	/// passthroughs are left untouched. A model's pinned effort is authoritative: it overrides any client
	/// directive already in the body. Absent a pin, a client directive wins over the backend default.
	/// </summary>
	/// <param name="backend">The backend the request targets, used to read its reasoning default.</param>
	/// <param name="path">The backend-relative request path the body is being forwarded to.</param>
	/// <param name="body">The passthrough request body, augmented in place when a policy applies.</param>
	/// <param name="pinnedEffort">The resolved model's pinned reasoning effort, or <see langword="null"/> when none is pinned.</param>
	private void ApplyPassthroughReasoning(
		BackendContext   backend,
		string           path,
		JsonObject       body,
		ReasoningEffort? pinnedEffort)
	{
		// Reasoning only applies to chat completions; legacy text completions and embeddings carry none.
		if (!string.Equals(path, ChatCompletionsPath, StringComparison.Ordinal)) return;

		ReasoningEffort? backendDefault = mBackends.TryGetValue(backend.Name, out BackendOptions? options)
			                                  ? options.ReasoningEffort
			                                  : null;

		ITraceScope trace = mTraceAccessor.Current;

		// A pinned effort wins over everything: strip whatever reasoning directive the client supplied so the
		// pinned value cannot collide with a leftover field, then write the operator's known-safe level.
		if (pinnedEffort is { } pinned)
		{
			StripClientReasoningDirectives(body);
			string pinnedWireField = ApplyReasoning(body, pinned);
			trace.RecordReasoning(
				pinned.ToWireValue(),
				DescribeReasoningSource(ReasoningEffortSource.Pinned),
				backendDefault?.ToWireValue(),
				pinnedWireField);
			return;
		}

		// A client that already expressed a reasoning preference in the OpenAI-native body wins; the proxy
		// must not override an explicit per-request wish with the backend default.
		if (HasClientReasoningDirective(body))
		{
			trace.RecordReasoning(
				ReadClientReasoningEffort(body),
				DescribeReasoningSource(ReasoningEffortSource.Request),
				backendDefault?.ToWireValue(),
				null);
			return;
		}

		if (backendDefault is not { } effort)
		{
			trace.RecordReasoning(null, DescribeReasoningSource(ReasoningEffortSource.Unspecified), null, null);
			return;
		}

		// The backend default is clamped to the provider's dialect ceiling, mirroring the Ollama-native path,
		// so the verbatim passthrough never injects a token the API rejects. (A pin is handled verbatim above;
		// a client's own directive is left untouched.) The trace records the effective, post-clamp value.
		ReasoningEffort clampedDefault = ClampToDialect(effort);
		string wireField = ApplyReasoning(body, clampedDefault);
		trace.RecordReasoning(
			clampedDefault.ToWireValue(),
			DescribeReasoningSource(ReasoningEffortSource.BackendDefault),
			effort.ToWireValue(),
			wireField);
	}

	/// <summary>
	/// Reads the client's reasoning effort token from a passthrough payload for provenance recording,
	/// returning the literal <c>reasoning_effort</c> value when present or a marker when the directive
	/// was expressed only through a provider-specific switch.
	/// </summary>
	/// <param name="payload">The passthrough request body carrying a client reasoning directive.</param>
	/// <returns>The client-supplied effort token, or a marker when only a vendor switch was used.</returns>
	private static string ReadClientReasoningEffort(JsonObject payload) =>
		payload["reasoning_effort"] is JsonValue value && value.TryGetValue(out string? token) && token is not null
			? token
			: "(client-specified)";

	/// <summary>
	/// Enforces this provider's forced vendor parameters on a verbatim <c>/v1</c> passthrough payload, so the
	/// forwarding paths apply the same authoritative vendor policy as the Ollama-native path. This matters most
	/// for switches the chassis sets to protect transparent request semantics, which would otherwise be left
	/// unset on <c>/v1</c>. The forced value overrides any the client supplied, matching
	/// <see cref="ApplyVendorParameters"/>. Vendor parameters are a chat concept, so only the chat-completions
	/// path is augmented; the legacy completions and embeddings passthroughs are left untouched. The base
	/// <see cref="ApplyVendorParameters"/> is a no-op, so providers without vendor switches forward verbatim.
	/// </summary>
	/// <param name="backend">The backend the request targets, forwarded to <see cref="ApplyVendorParameters"/>.</param>
	/// <param name="path">The backend-relative request path the body is being forwarded to.</param>
	/// <param name="body">The passthrough request body, augmented in place when the provider forces vendor parameters.</param>
	private void ApplyPassthroughVendorParameters(BackendContext backend, string path, JsonObject body)
	{
		// Vendor parameters only apply to chat completions; legacy text completions and embeddings carry none.
		if (!string.Equals(path, ChatCompletionsPath, StringComparison.Ordinal)) return;

		ApplyVendorParameters(backend, body);
	}

	/// <summary>
	/// Builds the outgoing chat request body: it maps the inbound Ollama request to the typed OpenAI
	/// shape, serializes it to a mutable JSON object, then (when a reasoning effort is resolved from
	/// the request or the backend default) lets the provider stamp its reasoning dialect onto it, and
	/// finally lets the provider add any non-standard sampling extensions its backend honors.
	/// </summary>
	/// <param name="backend">The backend the request targets, used to read its reasoning default.</param>
	/// <param name="request">The inbound Ollama chat request.</param>
	/// <param name="upstreamModel">The resolved upstream model identifier.</param>
	/// <param name="pinnedEffort">The model's pinned reasoning effort, or <see langword="null"/> when none is pinned.</param>
	/// <param name="stream">Whether a streamed response is requested.</param>
	/// <returns>The JSON request body, ready to post to the backend.</returns>
	private JsonObject BuildChatPayload(
		BackendContext    backend,
		OllamaChatRequest request,
		string            upstreamModel,
		ReasoningEffort?  pinnedEffort,
		bool              stream)
	{
		OpenAiChatRequest mapped = OpenAiRequestMapper.MapRequest(request, upstreamModel, stream);

		// The typed record carries explicit snake_case wire names, so the serialized node already matches
		// the OpenAI format; reasoning fields are then stamped on as raw keys in the provider's dialect.
		JsonObject payload = JsonSerializer.SerializeToNode(mapped, OpenAiSerialization.Options) as JsonObject
		                     ?? throw new ProviderException(
			                     HttpStatusCode.InternalServerError,
			                     "Failed to serialize the chat request payload.");

		ReasoningResolution reasoning = ResolveReasoning(backend, request, pinnedEffort);

		// A pinned effort is operator-authoritative and sent verbatim; a request- or backend-default-sourced
		// effort is clamped to the provider's dialect ceiling so the proxy never emits a token the API rejects.
		ReasoningEffort? effective = reasoning.Effort is { } resolved
			                             ? reasoning.Source == ReasoningEffortSource.Pinned
				                               ? resolved
				                               : ClampToDialect(resolved)
			                             : null;

		string? wireField = null;
		if (effective is { } applied) wireField = ApplyReasoning(payload, applied);

		// Stamp on this provider's non-standard sampling extensions (top_k/min_p). The base no-op leaves
		// the strict OpenAI payload clean; supporting providers add the fields the mapper deliberately omits.
		ApplySamplingExtensions(payload, request);

		// Authoritatively write provider-specific vendor parameters (e.g. Venice's include_venice_system_prompt),
		// overriding any value the client may have supplied. The base no-op; providers with vendor switches
		// override ApplyVendorParameters to enforce them.
		ApplyVendorParameters(backend, payload);

		// Re-attach any cached reasoning-details blob onto the assistant turn that replayed its tool calls, so a
		// backend can resume the reasoning it paused to call the tool. A graceful miss when no blob was cached
		// for the turn (including any backend that never emits the field).
		ReattachReasoningDetails(backend, payload, request);

		// Record the reasoning provenance and the translated upstream body so a trace shows both the
		// decision and exactly what was sent; both calls are no-ops when the request is not traced. The
		// recorded effort is the effective (post-clamp) value, matching what actually went on the wire.
		ITraceScope trace = mTraceAccessor.Current;
		trace.RecordReasoning(
			effective?.ToWireValue(),
			DescribeReasoningSource(reasoning.Source),
			reasoning.BackendDefault?.ToWireValue(),
			wireField);
		trace.RecordBackendRequest(
			backend.Name,
			ChatCompletionsPath,
			payload.ToJsonString(OpenAiSerialization.Options));

		return payload;
	}

	/// <summary>
	/// Resolves the reasoning effort for a request together with its provenance. A model's pinned effort is
	/// authoritative and short-circuits the chain: it overrides both the inbound <c>think</c> directive and
	/// the backend default, so a pinned model can never be pushed to a level it rejects. Absent a pin, an
	/// inbound <c>think</c> directive is preferred over the backend's configured default.
	/// </summary>
	/// <param name="backend">The backend whose configured default is consulted.</param>
	/// <param name="request">The inbound Ollama chat request carrying the optional <c>think</c> directive.</param>
	/// <param name="pinnedEffort">The model's pinned reasoning effort, or <see langword="null"/> when none is pinned.</param>
	/// <returns>The resolved effort and the source it came from.</returns>
	private ReasoningResolution ResolveReasoning(
		BackendContext    backend,
		OllamaChatRequest request,
		ReasoningEffort?  pinnedEffort)
	{
		ReasoningEffort? backendDefault = mBackends.TryGetValue(backend.Name, out BackendOptions? options)
			                                  ? options.ReasoningEffort
			                                  : null;

		// A pinned effort wins over everything: the client's think directive and the backend default are both
		// ignored so the operator's known-safe level is the only one ever sent for this model.
		if (pinnedEffort is { } pinned)
			return new ReasoningResolution(pinned, ReasoningEffortSource.Pinned, backendDefault);

		return ReasoningEffortParser.Resolve(request.Think, backendDefault);
	}

	/// <summary>
	/// Renders a <see cref="ReasoningEffortSource"/> as the short provenance token recorded in a trace.
	/// </summary>
	/// <param name="source">The source to describe.</param>
	/// <returns>A short, human-readable token identifying the decision source.</returns>
	private static string DescribeReasoningSource(ReasoningEffortSource source) => source switch
	{
		ReasoningEffortSource.Request        => "request",
		ReasoningEffortSource.BackendDefault => "backend default",
		ReasoningEffortSource.Pinned         => "pinned (registry)",
		ReasoningEffortSource.Unspecified    => "unspecified",
		// All enum values are handled above; a new value would be a programming error, not a runtime case.
		var _ => throw new UnreachableException($"Unhandled reasoning effort source '{source}'.")
	};

	/// <summary>
	/// Builds a JSON <c>POST</c> request to a relative path from a pre-serialized JSON node, used by
	/// the chat path and the passthrough forwarder which carry the body as a mutable node rather than a
	/// typed payload.
	/// </summary>
	/// <param name="path">The backend-relative request path.</param>
	/// <param name="body">The JSON body to send.</param>
	/// <returns>The constructed request message, ready to send.</returns>
	private static HttpRequestMessage CreateJsonNodeRequest(string path, JsonNode body) => new(HttpMethod.Post, path)
	{
		Content = JsonContent.Create(body, options: OpenAiSerialization.Options)
	};

	/// <summary>
	/// Reads a successful JSON response body as a <see cref="JsonObject"/>, raising a
	/// <see cref="ProviderException"/> when the payload is missing or not a JSON object.
	/// </summary>
	/// <param name="response">The successful upstream response.</param>
	/// <param name="cancellationToken">A token observed while reading the body.</param>
	/// <returns>The parsed JSON object.</returns>
	private static async Task<JsonObject> ReadJsonObjectAsync(
		HttpResponseMessage response,
		CancellationToken   cancellationToken)
	{
		try
		{
			JsonNode? node = await response.Content
				                 .ReadFromJsonAsync<JsonNode>(OpenAiSerialization.Options, cancellationToken)
				                 .ConfigureAwait(false);

			return node as JsonObject ?? throw new ProviderException(
				       HttpStatusCode.BadGateway,
				       "The upstream provider returned an empty or non-object response body.");
		}
		catch (JsonException exception)
		{
			throw new ProviderException(
				HttpStatusCode.BadGateway,
				"The upstream provider returned a malformed response body.",
				exception);
		}
	}

	/// <summary>
	/// Builds a JSON <c>POST</c> request to a relative path using the shared serializer options.
	/// </summary>
	/// <typeparam name="TPayload">The payload type to serialize into the request body.</typeparam>
	/// <param name="path">The backend-relative request path.</param>
	/// <param name="payload">The payload to serialize.</param>
	/// <returns>The constructed request message, ready to send.</returns>
	private static HttpRequestMessage CreateJsonRequest<TPayload>(string path, TPayload payload) =>
		new(HttpMethod.Post, path)
		{
			Content = JsonContent.Create(payload, options: OpenAiSerialization.Options)
		};

	/// <summary>
	/// Deserializes a successful JSON response body into the requested type, raising a
	/// <see cref="ProviderException"/> when the payload is missing or malformed.
	/// </summary>
	/// <typeparam name="TResult">The type to deserialize the body into.</typeparam>
	/// <param name="response">The successful upstream response.</param>
	/// <param name="cancellationToken">A token observed while reading the body.</param>
	/// <returns>The deserialized result.</returns>
	private static async Task<TResult> ReadJsonAsync<TResult>(
		HttpResponseMessage response,
		CancellationToken   cancellationToken)
	{
		try
		{
			TResult? result = await response.Content
				                  .ReadFromJsonAsync<TResult>(OpenAiSerialization.Options, cancellationToken)
				                  .ConfigureAwait(false);

			return result ?? throw new ProviderException(
				       HttpStatusCode.BadGateway,
				       "The upstream provider returned an empty response body.");
		}
		catch (JsonException exception)
		{
			throw new ProviderException(
				HttpStatusCode.BadGateway,
				"The upstream provider returned a malformed response body.",
				exception);
		}
	}

	/// <summary>
	/// Verifies an upstream response succeeded, translating a non-success status into a
	/// <see cref="ProviderException"/> that carries the status code and the backend's error message
	/// (when present in the OpenAI error envelope).
	/// </summary>
	/// <param name="response">The upstream response to inspect.</param>
	/// <param name="cancellationToken">A token observed while reading an error body.</param>
	private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		if (response.IsSuccessStatusCode) return;

		string detail = await ReadErrorDetailAsync(response, cancellationToken).ConfigureAwait(false);

		throw new ProviderException(
			response.StatusCode,
			$"The upstream provider responded with {(int)response.StatusCode} {response.StatusCode}: {detail}");
	}

	/// <summary>
	/// Extracts a human-readable error message from a failed response, preferring the OpenAI error
	/// envelope's message and falling back to the raw body or the reason phrase. The returned detail is
	/// length-capped so a backend that emits a very large error body does not bloat the resulting
	/// <see cref="ProviderException"/> message or the logs that record it, mirroring the capping the
	/// capability prober applies to its own rejection snippets.
	/// </summary>
	/// <param name="response">The failed upstream response.</param>
	/// <param name="cancellationToken">A token observed while reading the body.</param>
	/// <returns>The best-available error description, truncated when it exceeds the cap.</returns>
	private static async Task<string> ReadErrorDetailAsync(
		HttpResponseMessage response,
		CancellationToken   cancellationToken)
	{
		const int maxDetailLength = 500;

		string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

		if (string.IsNullOrWhiteSpace(body)) return response.ReasonPhrase ?? "no additional detail";

		string detail = body;

		try
		{
			var envelope =
				JsonSerializer.Deserialize<OpenAiErrorEnvelope>(body, OpenAiSerialization.Options);

			if (!string.IsNullOrWhiteSpace(envelope?.Error?.Message)) detail = envelope.Error.Message;
		}
		catch (JsonException)
		{
			// The error body was not the expected envelope; fall through to the raw text.
		}

		return detail.Length <= maxDetailLength
			       ? detail
			       : string.Concat(detail.AsSpan(0, maxDetailLength), "… (truncated)");
	}

	/// <summary>
	/// Formats a timestamp as the ISO-8601 string Ollama uses for <c>created_at</c>.
	/// </summary>
	/// <param name="timestamp">The instant to format.</param>
	/// <returns>The round-trip ISO-8601 representation.</returns>
	private static string FormatTimestamp(DateTimeOffset timestamp) =>
		timestamp.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

	/// <summary>
	/// Computes the elapsed time since a starting timestamp, expressed in nanoseconds for the Ollama
	/// duration fields.
	/// </summary>
	/// <param name="startTimestamp">The starting timestamp from <see cref="TimeProvider.GetTimestamp"/>.</param>
	/// <returns>The elapsed duration in nanoseconds.</returns>
	private long ElapsedNanoseconds(long startTimestamp)
	{
		TimeSpan elapsed = mTimeProvider.GetElapsedTime(startTimestamp);
		return (long)(elapsed.TotalMilliseconds * 1_000_000d);
	}
}
