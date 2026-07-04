# Endpoints

> Part of the [OllamaProxy documentation](../README.md#documentation).

This is the **two surfaces, one port** idea made concrete. OllamaProxy exposes both inbound surfaces
on the same port, exactly like a real Ollama install: the native Ollama API under `/api`, and the
OpenAI-compatible API under `/v1`. Clients connect through whichever protocol they prefer — GitHub
Copilot, for example, discovers models over `/api` but sends chat over `/v1/chat/completions`.

## Ollama-native surface

| Method & path | Purpose |
| --- | --- |
| `POST /api/chat` | Chat completion (streaming JSON-Lines or single response). |
| `POST /api/generate` | Prompt completion (reuses the chat path with prompt wrapping). |
| `GET  /api/tags` | Aggregated list of exposed models in Ollama format. |
| `POST /api/show` | Model details, including `capabilities` (tools / vision / completion) and `model_info`. |
| `POST /api/embed` | Embeddings (current API; single string or array input). |
| `POST /api/embeddings` | Embeddings (legacy single-prompt API). |
| `GET  /api/version` | Reports an Ollama-compatible version string. |
| `GET  /api/ps` | Running models — always empty, since upstream backends manage their own lifecycle. |
| `GET  /health` | Liveness probe (`{"status":"ok"}`); answers even when all backends are unreachable. |

## OpenAI-compatible surface

| Method & path | Purpose |
| --- | --- |
| `GET  /v1/models` | Model catalog in OpenAI list format (`{"object":"list","data":[…]}`). |
| `POST /v1/chat/completions` | Chat completion (streaming SSE with a terminating `[DONE]`, or single JSON response). |
| `POST /v1/completions` | Legacy text completion. |
| `POST /v1/embeddings` | Embeddings (single string or array input). |

Because the upstream backends are themselves OpenAI-compatible, the `/v1` routes forward the request
body through verbatim — rewriting only the `model` field between the client-facing name and the
resolved upstream identifier — so provider extensions and streamed tool-call deltas survive the
round-trip losslessly.

Upstream failures are surfaced in the shape that matches the inbound surface: an Ollama-shaped error
body (`{"error": "..."}`) on `/api`, and an OpenAI error envelope
(`{"error": {"message": "...", "type": "..."}}`) on `/v1`, both in English. A genuine client error
(4xx) from the backend is passed through; anything else is normalized to `502 Bad Gateway` to signal
an upstream problem.

## Admin chassis probes

The outer chassis runs on its own port (default `11435`, see [Deployment](deployment.md)) and registers
two infrastructure probes independently of the inner proxy host. Both answer in JSON and serve no
Ollama API surface.

| Method & path | Purpose |
| --- | --- |
| `GET  /health` | Liveness — same `{"status":"ok"}` shape as the proxy-port `/health`, but served by the chassis process itself. Answers even when the inner proxy has not yet started or is being recycled, which is what keeps the admin UI and the SCM anchor reachable during a recycle. |
| `GET  /ready`  | Readiness — returns `{"status":"ready"}` (`200`) when the inner proxy host is actively serving, or `{"status":"not_ready"}` (`503`) when it has not yet started or is still recovering. Use this when an orchestrator or load balancer needs to distinguish a live chassis from one whose inner proxy host is up and running. |
