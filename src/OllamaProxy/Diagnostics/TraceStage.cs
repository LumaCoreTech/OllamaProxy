// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Diagnostics;

/// <summary>
/// Identifies which point in a request-response flow a recorded <see cref="TraceEntry"/> describes.
/// The stages follow the data as it travels through the proxy: from the client's inbound request,
/// through the reasoning-effort decision and the translated backend request, to the backend's reasoning
/// stream and response, and finally the outbound response handed back to the client. <see cref="Note"/>
/// carries an ad-hoc annotation that does not belong to a single transport stage.
/// </summary>
enum TraceStage
{
	/// <summary>The raw request the client sent to the proxy.</summary>
	InboundRequest,

	/// <summary>The reasoning-effort decision: the resolved effort and where it came from.</summary>
	ReasoningResolution,

	/// <summary>The translated request the proxy sent upstream to the backend.</summary>
	BackendRequest,

	/// <summary>
	/// The backend's reasoning (chain-of-thought) text, aggregated from the streamed
	/// <c>reasoning_content</c> deltas. Recorded separately from the visible answer so a long reasoning
	/// stream does not crowd out the response text under a shared capture budget.
	/// </summary>
	BackendReasoning,

	/// <summary>The response the backend returned upstream (aggregated for streams).</summary>
	BackendResponse,

	/// <summary>The response the proxy wrote back to the client.</summary>
	OutboundResponse,

	/// <summary>A free-form annotation that does not map to a single transport stage.</summary>
	Note
}
