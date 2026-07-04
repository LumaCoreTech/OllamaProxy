// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Diagnostics.CodeAnalysis;

using OllamaProxy.Core;

namespace OllamaProxy.Endpoints;

/// <summary>
/// Shared routing helper for the request endpoints. It collapses the two-step model resolution every
/// handler performs (mapping a client-supplied model name to its <see cref="RegisteredModel"/> and
/// then pairing the backend with its provider adapter) into a single call. The endpoints keep
/// ownership of request validation and of shaping their protocol-specific error responses; this helper
/// only removes the duplicated resolution plumbing between the Ollama and OpenAI surfaces.
/// </summary>
static class EndpointRouting
{
	/// <summary>
	/// Resolves a client-supplied model name to its registered entry and the adapter/context pair that
	/// services it. A successful lookup yields both <paramref name="model"/> and
	/// <paramref name="resolved"/>; a miss leaves both <see langword="null"/> so the caller can emit
	/// the not-found response in its own error shape.
	/// </summary>
	/// <param name="router">Resolves the model name to its backend and upstream identifier.</param>
	/// <param name="providerResolver">Selects the provider adapter for the resolved backend.</param>
	/// <param name="modelName">The model name supplied by the client.</param>
	/// <param name="model">When resolved, the registered model entry; otherwise <see langword="null"/>.</param>
	/// <param name="resolved">When resolved, the adapter/context pair; otherwise <see langword="null"/>.</param>
	/// <returns><see langword="true"/> when the model was resolved; otherwise <see langword="false"/>.</returns>
	public static bool TryResolveBackend(
		IModelRouter                             router,
		IProviderResolver                        providerResolver,
		string                                   modelName,
		[NotNullWhen(true)] out RegisteredModel? model,
		[NotNullWhen(true)] out ResolvedBackend? resolved)
	{
		if (!router.TryResolve(modelName, out model))
		{
			resolved = null;
			return false;
		}

		resolved = providerResolver.Resolve(model.BackendName);
		return true;
	}

	/// <summary>
	/// Tests whether a client-requested context window fits within the model's resolved limit. A
	/// <see langword="null"/> request (the client did not specify <c>num_ctx</c>) always fits. When the
	/// request exceeds the limit the call fails so the endpoint can reject it explicitly rather than
	/// letting the oversized request fail opaquely at the backend.
	/// </summary>
	/// <param name="requestedContext">The context window the client asked for, or <see langword="null"/>.</param>
	/// <param name="model">The resolved model carrying the enforced context limit.</param>
	/// <param name="message">
	/// When the request exceeds the limit, a client-facing explanation; otherwise <see langword="null"/>.
	/// </param>
	/// <returns><see langword="true"/> when the request fits; otherwise <see langword="false"/>.</returns>
	public static bool TryValidateContextWindow(
		int?                             requestedContext,
		RegisteredModel                  model,
		[NotNullWhen(false)] out string? message)
	{
		ArgumentNullException.ThrowIfNull(model);

		if (requestedContext is not { } requested || requested <= model.ContextLength)
		{
			message = null;
			return true;
		}

		message =
			$"Requested context window of {requested} tokens exceeds the limit of {model.ContextLength} " +
			$"tokens for model '{model.Name}'. Reduce 'options.num_ctx' to {model.ContextLength} or less.";
		return false;
	}
}
