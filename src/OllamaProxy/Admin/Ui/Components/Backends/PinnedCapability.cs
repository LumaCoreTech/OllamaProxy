// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Admin.Ui.Components.Backends;

/// <summary>
/// Identifies one of a pinned model's four overridable capability flags for the editor's write-back,
/// decoupling the checkbox markup from the <c>ModelRegistrationOptions</c> property it targets. Lives in the
/// shared <see cref="BackendModels"/> component namespace so the page can route the enum through its
/// setter without the markup needing to know the model-registry's internal flag names.
/// </summary>
public enum PinnedCapability
{
	/// <summary>
	/// Completion (chat/text) support — <c>ModelRegistrationOptions.SupportsCompletion</c>.
	/// </summary>
	Completion,

	/// <summary>
	/// Tool-calling support — <c>ModelRegistrationOptions.SupportsTools</c>.
	/// </summary>
	Tools,

	/// <summary>
	/// Vision support — <c>ModelRegistrationOptions.SupportsVision</c>.
	/// </summary>
	Vision,

	/// <summary>
	/// Embedding support — <c>ModelRegistrationOptions.SupportsEmbeddings</c>.
	/// </summary>
	Embeddings
}
