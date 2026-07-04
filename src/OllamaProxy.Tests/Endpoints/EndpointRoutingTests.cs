// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Diagnostics.CodeAnalysis;

using OllamaProxy.Configuration;
using OllamaProxy.Contracts.Ollama;
using OllamaProxy.Core;
using OllamaProxy.Endpoints;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Tests.Endpoints;

/// <summary>
/// Tests for <see cref="EndpointRouting"/>, the shared routing helper the request endpoints call. The file
/// is organized by member:
/// <list type="number">
///     <item>
///         <description>
///         <see cref="EndpointRouting.TryResolveBackend"/> — the two-step model-to-backend resolution:
///         a hit yields the model and its adapter/context pair; a miss leaves both <see langword="null"/>.
///         </description>
///     </item>
///     <item>
///         <description>
///         <see cref="EndpointRouting.TryValidateContextWindow"/> — the request-side guardrail that rejects
///         a client-requested context window larger than the model's resolved limit, turning an oversized
///         request into an explicit error instead of an opaque downstream failure.
///         </description>
///     </item>
/// </list>
/// </summary>
[Trait("Category", "Unit")]
public sealed class EndpointRoutingTests
{
	private const int ModelContextLimit           = 8192;
	private const int WithinLimitRequestedContext = 4096;
	private const int OversizedRequestedContext   = 16384;

	// Built from the constants above so the expected message can never drift from the values the tests feed in.
	private static readonly string OversizedContextMessage =
		$"Requested context window of {OversizedRequestedContext} tokens exceeds the limit of {ModelContextLimit} " +
		$"tokens for model 'model'. Reduce 'options.num_ctx' to {ModelContextLimit} or less.";

	private static readonly ModelCapabilities Caps = ModelCapabilities.CompletionOnly;

	private static RegisteredModel ModelWithLimit(long contextLength) =>
		new("model", "backend", "model", Caps, contextLength);

	#region TryValidateContextWindow()

	/// <summary>
	/// Verifies that an unspecified context window always passes: a client that omits <c>num_ctx</c>
	/// places no demand the guardrail could violate.
	/// </summary>
	[Fact]
	public void TryValidateContextWindow_WhenRequestIsNull_PassesWithoutMessage()
	{
		// Arrange
		RegisteredModel model = ModelWithLimit(ModelContextLimit);

		// Act
		bool ok = EndpointRouting.TryValidateContextWindow(null, model, out string? message);

		// Assert
		Assert.True(ok);
		Assert.Null(message);
	}

	/// <summary>
	/// Verifies that a request exactly at the limit is accepted; the limit is inclusive.
	/// </summary>
	[Fact]
	public void TryValidateContextWindow_WhenRequestEqualsLimit_Passes()
	{
		// Arrange
		RegisteredModel model = ModelWithLimit(ModelContextLimit);

		// Act
		bool ok = EndpointRouting.TryValidateContextWindow(ModelContextLimit, model, out string? message);

		// Assert
		Assert.True(ok);
		Assert.Null(message);
	}

	/// <summary>
	/// Verifies that a request below the limit is accepted.
	/// </summary>
	[Fact]
	public void TryValidateContextWindow_WhenRequestBelowLimit_Passes()
	{
		// Arrange
		RegisteredModel model = ModelWithLimit(ModelContextLimit);

		// Act
		bool ok = EndpointRouting.TryValidateContextWindow(WithinLimitRequestedContext, model, out string? message);

		// Assert
		Assert.True(ok);
		Assert.Null(message);
	}

	/// <summary>
	/// Verifies that a request exceeding the limit is rejected and the message reports both the
	/// requested value and the enforced limit so the client can correct the call.
	/// </summary>
	[Fact]
	public void TryValidateContextWindow_WhenRequestExceedsLimit_FailsWithDescriptiveMessage()
	{
		// Arrange
		RegisteredModel model = ModelWithLimit(ModelContextLimit);

		// Act
		bool ok = EndpointRouting.TryValidateContextWindow(OversizedRequestedContext, model, out string? message);

		// Assert
		Assert.False(ok);
		Assert.Equal(OversizedContextMessage, message);
	}

	/// <summary>
	/// Verifies that a <see langword="null"/> model is rejected up front rather than producing a
	/// misleading validation result.
	/// </summary>
	[Fact]
	public void TryValidateContextWindow_WhenModelIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception =
			Assert.Throws<ArgumentNullException>(() =>
				EndpointRouting.TryValidateContextWindow(1024, null!, out string? _));
		Assert.Equal("model", exception.ParamName);
	}

	#endregion

	#region TryResolveBackend()

	/// <summary>
	/// Verifies that a resolvable model name yields both the registered model and the adapter/context pair the
	/// resolver returns, so the caller has everything it needs to forward the request.
	/// </summary>
	[Fact]
	public void TryResolveBackend_WhenModelResolves_ReturnsModelAndBackend()
	{
		// Arrange: the router resolves "model" to a backend, and the resolver pairs that backend with an adapter.
		RegisteredModel registered = ModelWithLimit(ModelContextLimit);
		StubProviderAdapter adapter = new();
		ResolvedBackend expectedBackend = new(adapter, new BackendContext(registered.BackendName));
		StubModelRouter router = new(registered);
		StubProviderResolver resolver = new(expectedBackend);

		// Act
		bool ok = EndpointRouting.TryResolveBackend(
			router,
			resolver,
			"model",
			out RegisteredModel? model,
			out ResolvedBackend? resolved);

		// Assert: both outputs are populated, and the resolver was asked for the model's backend by name.
		Assert.True(ok);
		Assert.Same(registered, model);
		Assert.Same(expectedBackend, resolved);
		Assert.Equal(registered.BackendName, resolver.LastRequestedBackendName);
	}

	/// <summary>
	/// Verifies that an unresolvable model name returns <see langword="false"/> with both outputs left
	/// <see langword="null"/>, and that the provider resolver is never consulted for a model that does not exist.
	/// </summary>
	[Fact]
	public void TryResolveBackend_WhenModelDoesNotResolve_ReturnsFalseWithoutConsultingResolver()
	{
		// Arrange: a router that resolves nothing, so resolution must stop before reaching the resolver.
		StubModelRouter router = new(model: null);
		StubProviderResolver resolver = new(expectedBackend: null);

		// Act
		bool ok = EndpointRouting.TryResolveBackend(
			router,
			resolver,
			"missing",
			out RegisteredModel? model,
			out ResolvedBackend? resolved);

		// Assert: the miss is reported and neither output is populated.
		Assert.False(ok);
		Assert.Null(model);
		Assert.Null(resolved);

		// And the resolver was never consulted — a miss short-circuits before backend selection.
		Assert.Null(resolver.LastRequestedBackendName);
	}

	#endregion

	/// <summary>
	/// An <see cref="IModelRouter"/> that resolves a single preset model by any name, or resolves nothing when
	/// constructed with <see langword="null"/>. Only <see cref="TryResolve"/> is exercised by these tests;
	/// <see cref="GetModels"/> is never called.
	/// </summary>
	private sealed class StubModelRouter(RegisteredModel? model) : IModelRouter
	{
		private readonly RegisteredModel? mModel = model;

		/// <inheritdoc/>
		public IReadOnlyList<RegisteredModel> GetModels() => throw new NotSupportedException();

		/// <inheritdoc/>
		public bool TryResolve(string modelName, [NotNullWhen(true)] out RegisteredModel? model)
		{
			model = mModel;
			return model is not null;
		}
	}

	/// <summary>
	/// An <see cref="IProviderResolver"/> that returns a preset <see cref="ResolvedBackend"/> and records the last
	/// backend name it was asked to resolve, so a test can assert whether resolution reached this step. A
	/// <see langword="null"/> preset makes <see cref="Resolve"/> a failing call, proving it is never invoked on a
	/// model miss. <see cref="ResolveDraft"/> is outside the scope of these tests.
	/// </summary>
	private sealed class StubProviderResolver(ResolvedBackend? expectedBackend) : IProviderResolver
	{
		private readonly ResolvedBackend? mExpectedBackend = expectedBackend;

		/// <summary>
		/// Gets the last backend name passed to <see cref="Resolve"/>, or <see langword="null"/> when it was never
		/// called.
		/// </summary>
		public string? LastRequestedBackendName { get; private set; }

		/// <inheritdoc/>
		public ResolvedBackend Resolve(string backendName)
		{
			LastRequestedBackendName = backendName;
			return mExpectedBackend ?? throw new InvalidOperationException("Resolve should not be called on a miss.");
		}

		/// <inheritdoc/>
		public ResolvedBackend ResolveDraft(BackendOptions draft) => throw new NotSupportedException();
	}

	/// <summary>
	/// A stand-in <see cref="IProviderAdapter"/> used only as the identity carried by a
	/// <see cref="ResolvedBackend"/>; <see cref="TryResolveBackend"/> never invokes any of its members, so every
	/// call throws to prove it stays untouched.
	/// </summary>
	private sealed class StubProviderAdapter : IProviderAdapter
	{
		/// <inheritdoc/>
		public string ProviderType => "openai";

		/// <inheritdoc/>
		public IAsyncEnumerable<OllamaChatResponse> StreamChatAsync(
			BackendContext    backend,
			string            upstreamModel,
			OllamaChatRequest request,
			ReasoningEffort?  pinnedEffort,
			CancellationToken cancellationToken) => throw new NotSupportedException();

		/// <inheritdoc/>
		public Task<OllamaChatResponse> CompleteChatAsync(
			BackendContext    backend,
			string            upstreamModel,
			OllamaChatRequest request,
			ReasoningEffort?  pinnedEffort,
			CancellationToken cancellationToken) => throw new NotSupportedException();

		/// <inheritdoc/>
		public Task<OllamaEmbedResponse> CreateEmbeddingsAsync(
			BackendContext     backend,
			string             upstreamModel,
			OllamaEmbedRequest request,
			CancellationToken  cancellationToken) => throw new NotSupportedException();

		/// <inheritdoc/>
		public Task<IReadOnlyList<DiscoveredModel>> DiscoverModelsAsync(
			BackendContext    backend,
			CancellationToken cancellationToken) => throw new NotSupportedException();

		/// <inheritdoc/>
		public Task<ModelCapabilities> DetermineCapabilitiesAsync(
			BackendContext    backend,
			DiscoveredModel   model,
			CancellationToken cancellationToken) => throw new NotSupportedException();
	}
}
