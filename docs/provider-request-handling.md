# Provider request handling

How each provider adapter transforms requests on their way to an OpenAI-compatible backend,
and on which path each transformation happens.

This document describes the behavior that is implemented in the code today. Where the code relies
on a deliberate, undocumented assumption (for example accepting a reasoning token that a vendor's
public enum does not list), that is called out explicitly rather than presented as a verified fact.

---

## Two inbound request paths

Every backend request enters through one of two surfaces, and they are handled very differently:

| Path | Inbound surface | Entry points | What happens to the body |
|------|-----------------|--------------|--------------------------|
| **Ollama-native** | `/api/chat`, `/api/embeddings`, … | `CompleteChatAsync()`, `StreamChatAsync()`, `CreateEmbeddingsAsync()` | The Ollama request is **translated** into the OpenAI wire shape, then provider seams stamp on the dialect. |
| **OpenAI passthrough** | `/v1/chat/completions`, `/v1/completions`, … | `ForwardJsonAsync()`, `ForwardSseAsync()` | The client's OpenAI body is forwarded **verbatim**, except for two narrowly-scoped policy mutations (reasoning and forced vendor parameters). |

The shared base class for all four providers is [`OpenAiCompatibleProvider`](../src/OllamaProxy/Providers/OpenAiProtocol/OpenAiCompatibleProvider.cs).
The provider-specific adapters ([`OpenAiProvider`](../src/OllamaProxy/Providers/OpenAi/OpenAiProvider.cs), [`VeniceProvider`](../src/OllamaProxy/Providers/Venice/VeniceProvider.cs), [`OpenRouterProvider`](../src/OllamaProxy/Providers/OpenRouter/OpenRouterProvider.cs), [`VllmProvider`](../src/OllamaProxy/Providers/Vllm/VllmProvider.cs)) override only the seams where their wire dialect differs.

---

## The Ollama-native chat path

`CompleteChatAsync()` / `StreamChatAsync()` build the outgoing body in `BuildChatPayload()`, which runs a
fixed pipeline:

1. **Map** the inbound `OllamaChatRequest` to the typed `OpenAiChatRequest`
   ([`OpenAiRequestMapper`](../src/OllamaProxy/Providers/OpenAiProtocol/Mapping/OpenAiRequestMapper.cs)).
   Only **specification** fields are mapped here — `temperature`, `top_p`, `seed`,
   `max_completion_tokens` (from Ollama's `num_predict`), `stop`, `frequency_penalty`,
   `presence_penalty`, `logprobs`/`top_logprobs`, `tools`, and `response_format` (from `format`).
   The non-standard `top_k`/`min_p` are **deliberately not** mapped here, so the shared output stays
   safe for a strict OpenAI backend.
2. **Serialize** the typed record to a mutable `JsonObject`.
3. **`ApplyReasoning()`** — stamp the resolved reasoning effort in the provider's dialect (see
   [Reasoning resolution](#reasoning-resolution)).
4. **`ApplySamplingExtensions()`** — stamp the provider's non-standard sampling fields (`top_k`/`min_p`).
5. **`ApplyVendorParameters()`** — authoritatively write the provider's forced vendor switches.
6. **Trace** the reasoning provenance and the final upstream body.

The response is mapped back to the Ollama shape by [`OpenAiResponseMapper`](../src/OllamaProxy/Providers/OpenAiProtocol/Mapping/OpenAiResponseMapper.cs) (non-streaming)
or the streaming translator (streaming).

---

## The OpenAI passthrough path

`ForwardJsonAsync()` / `ForwardSseAsync()` forward the client's body **verbatim**, with exactly two
policy mutations — both gated to the `chat/completions` path only (legacy `/v1/completions` and
embeddings are never touched):

1. **`ApplyPassthroughReasoning()`** — applies the same reasoning policy as the native path so a `/v1`
   client gets consistent behavior (see [Reasoning resolution](#reasoning-resolution)).
2. **`ApplyPassthroughVendorParameters()`** — enforces the provider's **forced** vendor switches (it
   reuses the same `ApplyVendorParameters()` seam as the native path). This exists so chassis
   guarantees — most importantly Venice's vendor-prompt suppression — apply on `/v1` too, not only
   on `/api/chat`.

Everything else in the body — sampling parameters, message content, tools — is forwarded unchanged.

> **Why these two and nothing else?** The `/v1` route is intentionally a transparent passthrough.
> Reasoning is mutated because the proxy's pin/default policy must hold regardless of route. Forced
> vendor parameters are mutated because they encode a chassis guarantee (e.g. "do not let the vendor
> inject a system prompt") that the operator — not the client — owns. Sampling extensions and other
> fields are the client's own business and pass through untouched.

---

## Transformation seams

All four providers share these virtual seams on `OpenAiCompatibleProvider`. The base implementation
is the strict-OpenAI behavior; each provider overrides only what its dialect needs.

| Seam | Base (OpenAI) behavior | Runs on native path | Runs on passthrough |
|------|------------------------|:-------------------:|:-------------------:|
| `ApplyReasoning()` | writes flat `reasoning_effort` | ✅ | ✅ (via `ApplyPassthroughReasoning()`) |
| `ApplySamplingExtensions()` | no-op (strict OpenAI) | ✅ | ❌ (client's own) |
| `ApplyVendorParameters()` | no-op | ✅ | ✅ (via `ApplyPassthroughVendorParameters()`) |
| `HasClientReasoningDirective()` | recognizes flat `reasoning_effort` | — | ✅ (passthrough only) |
| `StripClientReasoningDirectives()` | removes flat `reasoning_effort` | — | ✅ (passthrough, pinned only) |
| `MaxDialectReasoningEffort` | `XHigh` | ✅ | ✅ |

---

## Per-provider summary

### OpenAI — [`OpenAiProvider`](../src/OllamaProxy/Providers/OpenAi/OpenAiProvider.cs)

The default for the `openai` provider type and any generic OpenAI-compatible backend. Inherits every
seam unchanged.

- **Reasoning:** flat `reasoning_effort` token. Dialect ceiling `xhigh` (the base default), so a
  non-pinned `max` is clamped to `xhigh`.
- **Sampling extensions:** none — `top_k`/`min_p` are dropped so the strict OpenAI API never sees a
  field it would reject.
- **Vendor parameters:** none.
- **Discovery:** `GET /v1/models` returns only `id` + `created`; context length and capabilities are
  left unset (deferred to operator config and probing). Operating mode: **Explicit**.

### Venice — [`VeniceProvider`](../src/OllamaProxy/Providers/Venice/VeniceProvider.cs)

- **Reasoning:** positive efforts → flat `reasoning_effort`; `none` →
  `venice_parameters.disable_thinking = true` (Venice's documented off switch). Dialect ceiling
  raised to **`max`**.
  - ⚠️ **Unverified assumption:** that Venice accepts the extended `max` token (rationale: the Claude
    models it serves) has **not** been measured against a live Venice backend. The analogous OpenRouter
    assumption was disproven by a 2026 live probe (see below), so this one is suspect too; if it proves
    wrong, lower Venice's ceiling to `xhigh` exactly as OpenRouter was corrected.
- **Sampling extensions:** forwards `top_k`/`min_p`.
- **Vendor parameters:** **forces `venice_parameters.include_venice_system_prompt = false`** on every
  chat request, overwriting any client value — on **both** the native and passthrough paths. This
  suppresses Venice's vendor-injected system prompt, which the chassis treats as undesirable by
  default; only the operator (currently via source) should be able to enable it.
- **Reasoning-details round-trip:** captures and replays the opaque `reasoning_details` blob
  server-side whenever the backend returns it (Venice does, notably for the Claude models it serves);
  see [Reasoning-details round-trip](#reasoning-details-round-trip).
- **Discovery:** rich `model_spec` block → context (`availableContextTokens`), vision, function
  calling, output modality (`type`), quantization, and pricing. Operating mode: **PlugAndPlay**.

### OpenRouter — [`OpenRouterProvider`](../src/OllamaProxy/Providers/OpenRouter/OpenRouterProvider.cs)

- **Reasoning:** writes the nested unified `reasoning.effort` object. OpenRouter also accepts the flat
  `reasoning_effort` field (it is OpenAI-compatible), so the nested form is the **recommended**
  encoding chosen by this adapter, **not a required** one. Inherits the base **`xhigh`** dialect ceiling.
  - ✅ **Measured (2026):** a live probe against `openai/gpt-5.2` and `anthropic/claude-opus-4.8` showed
    OpenRouter's gateway rejects `reasoning.effort = "max"` with HTTP 400 for **every** model — `max` is
    not in its global enum (`xhigh, high, medium, low, minimal, none`) and is rejected before any model is
    consulted. An in-enum over-cap token such as `xhigh` is accepted and mapped down to a model's nearest
    level, so keeping the ceiling at `xhigh` is what makes a non-pinned `max` default forwardable.
- **Sampling extensions:** forwards `top_k`/`min_p`.
- **Vendor parameters:** none.
- **Reasoning-details round-trip:** captures and replays the opaque `reasoning_details` blob
  server-side whenever the backend returns it (OpenRouter does, notably for Claude, which pauses
  mid-response to await a tool result); see [Reasoning-details round-trip](#reasoning-details-round-trip).
- **Discovery:** top-level `context_length` (falling back to `top_provider.context_length`),
  `architecture` input/output modalities, `supported_parameters`, and per-single-token pricing
  (scaled to per-million). Operating mode: **PlugAndPlay**.

### vLLM — [`VllmProvider`](../src/OllamaProxy/Providers/Vllm/VllmProvider.cs)

- **Reasoning:** writes **both** the portable `reasoning_effort` token (honored by modern vLLM, which
  derives `enable_thinking` from it) **and** the explicit `chat_template_kwargs.enable_thinking`
  boolean (honored by older vLLM and templates that only read the kwarg). `none` sets the flag
  `false`; any positive effort sets it `true`. No vLLM-specific dialect ceiling is set, so it inherits
  the base `xhigh`. Which tokens a given vLLM build accepts depends on the served model and its chat
  template, which this adapter does not enumerate.
- **Sampling extensions:** forwards `top_k`/`min_p`.
- **Vendor parameters:** none.
- **Discovery:** reads `max_model_len` as the context window; no capability metadata (deferred to
  probing). Operating mode: **Explicit**.

---

## Reasoning resolution

The same precedence applies on both paths. From strongest to weakest:

1. **Pinned effort** (operator-configured per model) — authoritative. On the native path it is sent
   **verbatim** (not clamped); on the passthrough path the client's directive is stripped first so it
   cannot collide, then the pin is written.
2. **Client directive** — if the request already expresses a reasoning preference (in the provider's
   dialect), it wins over the backend default and is left untouched.
3. **Backend default** (configured per backend) — applied when neither of the above is present, and
   **clamped** to the provider's dialect ceiling (`ClampToDialect()`) so the proxy never emits a token
   the API would reject.
4. **None** — no reasoning field is written.

`ReasoningEffort` is ordered weakest-to-strongest, so the clamp is a simple minimum:

```
None < Minimal < Low < Medium < High < XHigh < Max
```

Wire tokens: `none`, `minimal`, `low`, `medium`, `high`, `xhigh`, `max`.

| Provider | Dialect ceiling | Effect on a non-pinned `max` |
|----------|-----------------|------------------------------|
| OpenAI | `xhigh` | clamped to `xhigh` |
| vLLM | `xhigh` (inherited) | clamped to `xhigh` |
| Venice | `max` | forwarded as `max` (unverified assumption — see above) |
| OpenRouter | `xhigh` (inherited) | clamped to `xhigh` (`max` is rejected with HTTP 400 — measured) |

> Only request- and backend-default-sourced efforts are clamped. A **pinned** effort is
> operator-authoritative and bypasses the clamp.

### Response-side reasoning

Chain-of-thought is read back from the response under either spelling: `reasoning_content` (the
de-facto field used by DeepSeek, vLLM, llama.cpp) **or** `reasoning` (OpenRouter, and newer vLLM,
which renamed `reasoning_content` → `reasoning`). Both the typed message contract
(`ReasoningContent ?? Reasoning`) and the trace extractors check both, so old and new servers both
work without a provider-specific branch.

---

## Tool-call correlation

Parallel tool calls are correlated by id end-to-end:

- **Inbound (client → backend):** an assistant turn's `tool_calls` carry each call's `id`; a tool
  result message is stamped with `tool_call_id` (falling back to the tool name when the client
  supplied no id — the best-effort value a name-only Ollama client can offer).
- **Outbound (backend → client), non-streaming:** each returned tool call's distinct `id` is carried
  through to the Ollama response.
- **Outbound, streaming:** the streaming accumulator keeps each call's `id` per index while buffering
  its argument fragments, and reassembles both on the terminal chunk.

This wiring lives in the shared mapping layer, so all four providers inherit it. Each provider has
parallel-tool-call regression tests: [OpenAI](../src/OllamaProxy.Tests/Integration/OpenAiProviderIntegrationTests.cs), [Venice](../src/OllamaProxy.Tests/Integration/VeniceProviderIntegrationTests.cs), [OpenRouter](../src/OllamaProxy.Tests/Integration/OpenRouterProviderIntegrationTests.cs), [vLLM](../src/OllamaProxy.Tests/Integration/VllmProviderIntegrationTests.cs).

---

## Discovery (`GET /models`)

The model-listing schema is the chief point of vendor divergence. The base owns the HTTP transport
(`DiscoverModelsCoreAsync()`); each provider supplies only its own entry contract and a projection onto
the neutral `DiscoveredModel`.

| Provider | Context length | Capability metadata | Operating mode |
|----------|----------------|---------------------|----------------|
| OpenAI | none (operator config) | none (probing) | Explicit |
| vLLM | `max_model_len` | none (probing) | Explicit |
| Venice | `model_spec.availableContextTokens` | `model_spec` (vision, function calling, `type`) | PlugAndPlay |
| OpenRouter | `context_length` → `top_provider.context_length` | `architecture` modalities + `supported_parameters` | PlugAndPlay |

When a provider reports no authoritative capabilities, the model falls back to active probing.

---

## Reasoning-details round-trip

Some backends (Venice and OpenRouter in practice, notably for the Claude/Gemini models they serve)
return an opaque `reasoning_details` blob on a tool-calling assistant turn that, per their specs, must
be replayed verbatim on the follow-up request to preserve reasoning blocks / thought signatures
(Claude, for instance, pauses mid-response to await a tool result). Ollama's wire format has no field
to carry it, and the proxy must not expose the opaque blob to the Ollama client — so the proxy holds
it **server-side** instead of round-tripping it through the client.

The round-trip is **data-driven, not dialect-gated**: every provider captures and re-attaches the
field whenever the backend actually returns it. There is no per-provider opt-in flag — a backend that
never emits `reasoning_details` simply produces nothing to capture and the field is left off, while a
strict-OpenAI or vLLM backend that *does* emit it is handled correctly rather than having a field it
could preserve silently discarded.

- **Capture.** The shared base reads `reasoning_details` off the assistant response — on the
  non-streaming path from the mapped message, on the streaming path via a tee that observes the raw
  chunks (`CaptureReasoningDetails()` / `ObserveReasoningDetails()`) — and stores it.
- **Correlation key.** The cache is keyed not by the backend-assigned `tool_call_id` (backend-controlled,
  sometimes very short, and not a reliable anchor) but by a stable SHA-256 over **the originating
  backend's name plus the turn's tool-call content**: each call's function name plus its canonicalized
  arguments (object keys sorted), and the per-call fragments themselves sorted, so a client that re-serializes
  the history or reorders parallel calls still produces the same key
  ([`ReasoningDetailsCorrelation`](../src/OllamaProxy/Providers/OpenAiProtocol/ReasoningDetailsCorrelation.cs), format `rd-v1`).
- **Backend scoping.** Folding the backend name into the key is **mandatory**, not cosmetic: the cache
  is a single process-wide singleton shared by every backend, so without it two backends that emit the
  same tool call (say `get_weather({"city":"Berlin"})`) would collide on one key and one backend could
  be handed the vendor-specific blob the other produced — a contract it never emitted. The scoped key
  guarantees a blob is only ever replayed to the backend that captured it, which matters as soon as you
  run, say, Venice and OpenRouter side by side.
- **Re-attach.** When a later request replays that assistant turn's tool calls, `BuildChatPayload()`
  recomputes the key, looks up the blob, and stamps it back onto the matching **upstream** assistant
  message (`ReattachReasoningDetails()`) — never onto the Ollama client's response.
- **Cache.** The store is a singleton, in-memory, sliding-expiration, size-capped LRU
  ([`ReasoningDetailsCache`](../src/OllamaProxy/Providers/OpenAiProtocol/ReasoningDetailsCache.cs)),
  configured by [`ReasoningDetailsCacheOptions`](../src/OllamaProxy/Configuration/ReasoningDetailsCacheOptions.cs)
  (`Enabled`, `SlidingExpirationSeconds`, `MaxEntries`). The TTL and entry cap exist specifically so a
  conversation that never returns (its tool result never arrives) cannot pin a blob indefinitely.
  Disabling the cache fully suppresses the round-trip.

> ⚠️ **Precaution, not a measured fact:** this correlation and round-trip have **not** been measured
> against a live Claude/Gemini backend. They are exercised by tests with mocked backends only —
> [cache semantics](../src/OllamaProxy.Tests/Providers/OpenAiProtocol/ReasoningDetailsCacheTests.cs),
> [key canonicalization and backend scoping](../src/OllamaProxy.Tests/Providers/OpenAiProtocol/ReasoningDetailsCorrelationTests.cs),
> and [end-to-end capture/re-attach plus cross-backend isolation](../src/OllamaProxy.Tests/Integration/ReasoningDetailsRoundTripIntegrationTests.cs).
> A hash collision merely re-attaches a slightly wrong blob and degrades gracefully; it never throws
> or corrupts the conversation.
