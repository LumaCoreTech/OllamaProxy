# Configuration

> Part of the [OllamaProxy documentation](../README.md#documentation).

Everything here configures the inner proxy host. Most settings shape the **one model catalog** the proxy
advertises: `Backends` defines *where the models come from* — and, since each backend owns its own `Mode`
and `Models` registry, also *what goes into* the catalog. The same top-level **`OllamaProxy`** section also
owns the proxy listener URL and optional diagnostics / continuity features. The shape is:

| Key | Type | Default | Description |
| --- | --- | --- | --- |
| `ListenUrl` | string | `http://localhost:11434` | Absolute URL the inner proxy host listens on. Use `http://0.0.0.0:11434` to bind all interfaces, for example in a container. |
| `Backends` | map | `{}` | Named upstream backends. An empty map is valid and starts the proxy with no models; add backends by file, environment, installer, or admin UI. Each backend carries its own `Mode` and `Models` registry. |
| `RequestTracing` | object | _(off)_ | Optional per-request trace capture for debugging. Disabled by default. |
| `ReasoningDetailsCache` | object | _(on)_ | Server-side cache for opaque `reasoning_details` blobs that some backends require across tool-call turns. Enabled by default. |

## Listener URL

`ListenUrl` controls the data-plane address: the inner proxy host that serves the Ollama-native `/api/*`
and OpenAI-compatible `/v1/*` surfaces. The default is `http://localhost:11434`, matching Ollama's
conventional port so local Ollama-aware clients need no reconfiguration.

Set it explicitly when another process already occupies that port, or when a deployment must listen on a
non-loopback address:

```json
{
  "OllamaProxy": {
    "ListenUrl": "http://0.0.0.0:11434"
  }
}
```

The environment variable form is:

```text
OllamaProxy__ListenUrl=http://0.0.0.0:11434
```

Keep this address distinct from the chassis/admin address (`Admin:Url` in `hostsettings.json`, default
`http://localhost:11435`) because the two hosts bind separate Kestrel instances.

## Operating modes

Each backend carries its **own** mode — the single biggest lever over what that backend contributes to
the catalog. The three values form a deliberate progression — from *"show me everything, I'll sort it
out later"* to *"show me exactly what I listed, nothing else."* Because the mode is per backend, one
deployment can mix them freely: a metadata-rich cloud backend on `PlugAndPlay` sitting next to a pinned
production backend on `Explicit`. You can also move a single backend along that line as it matures,
without switching tools or rewriting your client config.

When a backend omits `Mode`, a **provider-aware default** applies: metadata-rich providers (`venice`,
`openrouter`) default to `PlugAndPlay`, since they advertise capabilities in their listing and can
publish a complete, accurate catalog immediately; providers that report little or no capability metadata
(`openai`, `vllm`) and any unrecognized type default to `Explicit`, keeping you in control rather than
auto-exposing an unreliable surface. The default is only a starting point — set `Mode` explicitly to
override it.

A **registry entry** is one element of a backend's `Models` array (see [Model registry](#model-registry))
— an explicit pin of a client-facing model name to an upstream model on that backend. A backend's mode
decides both whether its registry is honored at all and whether (and which) of its **discovered** models
are added on top:

| Mode | The backend contributes | Reach for it when |
| --- | --- | --- |
| **`PlugAndPlay`** | Every model the backend reports, with detected capabilities. Its registry is **ignored** (a non-empty `Models` list is logged as a warning). | The backend advertises rich metadata, or you just want zero friction. |
| **`Hybrid`** | The backend's registry entries **plus** its discovered models. | You want a few of that backend's models pinned exactly right, but still enjoy auto-discovery for the rest. |
| **`Explicit`** | Only the backend's registry entries — discovered models are never auto-exposed. | You want a fixed, reproducible surface from that backend that can't drift when it changes its listing. |

The progression is also a trust gradient, drawn per backend: `PlugAndPlay` trusts the backend's listing
completely (and ignores its registry), `Explicit` trusts only your pins, and `Hybrid` blends the two.

### How the catalog is merged

The catalog is keyed by **model name** (case-insensitive) and assembled in a fixed order, so the
outcome never depends on which backend happens to answer first:

1. **Discovery runs first**, across every backend, in parallel. A `PlugAndPlay` or `Hybrid` backend is
   queried in full — its discovered models become candidates for exposure. An `Explicit` backend is
   queried too, but only for **metadata** (no capability probing): its listing is used to *enrich* its
   pins, not to expose anything. Running discovery before the registry is what lets a `Hybrid` pin
   **inherit the context window its backend currently reports** instead of being capped at the backend
   default (see [Context window](#context-window)).
2. **Registry entries are materialized next.** Every `Hybrid` or `Explicit` backend's `Models` entries
   are added to the catalog, keyed by their `Name`, each enriched with whatever its backend's listing
   reported for it (a `Hybrid` pin's context window, and — for both `Hybrid` and `Explicit` — provider
   metadata such as creation date and pricing). `PlugAndPlay` backends skip this step — their registry
   is ignored.
3. **Discovered models are merged last**, in configured backend order, and a discovered model is added
   **only if its name is not already claimed**. An `Explicit` backend's metadata-only discovery
   contributes nothing here — it has already done its job enriching the pins in step 2.

So the precedence rule is simply: **a registry entry always wins over a discovered model of the same
name.** Because registry names are claimed before any discovered model is merged, a pin on one backend
even shadows a discovered model of the same name on another. This is what makes `Hybrid` useful — you
pin the handful of models you care about (names, upstream targets, capabilities) and let everything
else flow in automatically. A `PlugAndPlay` backend skips the registry step altogether, so pins on it
have no effect — switch it to `Hybrid` to keep auto-discovery while pinning.

A backend that fails discovery is logged and skipped, so one unreachable backend never blocks startup.
(An `Explicit` backend whose metadata fetch fails simply exposes its pins without the enrichment — the
pins themselves still stand.)

### Name collisions

Because the catalog is keyed by client-facing name, two backends that report the **same** model name
cannot both be auto-exposed under it — the first one discovered (in configured backend order) wins, and
the shadowed copy is logged as a warning and left unreachable under that name. There are two
deterministic ways to keep both reachable:

- **Pin distinct names** in a backend's registry (`Models`), which always win over discovery — available
  on any backend in `Hybrid` or `Explicit` (a `PlugAndPlay` backend ignores its registry).
- **Set a `ModelPrefix`** on one or both backends, so their models are published as `prefix/model`
  (for example `vllm/gemma4-31b` and `venice/gemma4-31b`). The prefix applies to **everything** the
  backend exposes — discovered models *and* pins alike — so a pinned name collides no more than a
  discovered one does.

The prefix is **opt-in and deterministic**: it is applied whenever it is configured, regardless of
whether a collision currently exists. This keeps published names stable — a model's client-facing
name never changes just because another backend started or stopped advertising the same name.
Single-backend deployments can leave `ModelPrefix` unset and keep the shorter, bare names.

## Backends

A backend is one upstream OpenAI-compatible API the proxy can route to — its URL, its key, and a few
knobs that shape how its models enter the catalog. Each entry in `Backends` is keyed by a **logical
name**:

| Key | Type | Default | Description |
| --- | --- | --- | --- |
| `BaseUrl` | string | _(required)_ | Absolute URL of the backend's OpenAI-compatible API (e.g. `https://api.openai.com/v1`). |
| `ProviderType` | string | `openai` | Selects the provider adapter: `openai` (generic OpenAI-compatible default), `venice`, `vllm`, or `openrouter`. |
| `ApiKey` | string | _(required)_ | Bearer token. Min. 8 chars. Prefer an environment variable (see below). |
| `Mode` | enum? | _(provider-aware)_ | How this backend's model list is assembled: `PlugAndPlay`, `Hybrid`, or `Explicit`. When omitted, defaults by `ProviderType` (`venice`/`openrouter` → `PlugAndPlay`; `openai`/`vllm`/unknown → `Explicit`). See [Operating modes](#operating-modes). |
| `Models` | array | `[]` | This backend's explicit model registry. Required (sole source of models) in `Explicit`; optional in `Hybrid`; ignored in `PlugAndPlay`. See [Model registry](#model-registry). |
| `ContextLength` | int? | _(auto)_ | Backend-scoped **fallback** for context window (tokens): applies only to models of this backend that report no window of their own. A value the backend reports always wins, so it never narrows a detected window — it merely fills the gap for backends that advertise none. To constrain a single model below its reported window, set a per-model `ContextLength` override instead. The effective window is still resolved **per model**. See [Context window](#context-window). |
| `ModelPrefix` | string? | _(none)_ | Optional prefix applied to the client-facing name of **every** model this backend contributes — both auto-exposed and pinned — producing `prefix/model`. Disambiguates the same model served by multiple backends. The prefix changes only the published name; the unprefixed id is still requested upstream, and a registry entry stores its `Name` bare (the prefix is applied at exposure, exactly as for a discovered model). Must be non-blank and contain no `/`. See [Name collisions](#name-collisions). |
| `ReasoningEffort` | enum? | _(none)_ | Backend-scoped fallback for reasoning effort: used for chat requests to this backend when the client sends no `think` directive. A per-request `think` always overrides it. Affects **request behavior**, not model capabilities. See [Reasoning effort](#reasoning-effort). |
| `Probing` | object | _(probes on)_ | Active capability probing (completion, tools, vision, and embeddings, all on by default), with per-attempt timeout and transient-failure retries. See [Capability detection](#capability-detection). |

> [!NOTE]
> `ContextLength` and `ReasoningEffort` are **backend-scoped defaults**, but they resolve at a finer
> granularity: `ContextLength` is ultimately resolved **per model** (each model gets its own effective
> value), and `ReasoningEffort` is resolved **per request** (a client's `think` field always takes
> precedence). They live on the backend so one entry covers all of that backend's models — a
> convenience, not a statement about their scope of effect. See [Context window](#context-window) and
> [Reasoning effort](#reasoning-effort) for the full resolution order.

> [!NOTE]
> **Venice provider behavior.** On a `venice` backend the proxy authoritatively writes
> `venice_parameters.include_venice_system_prompt = false` on every chat request, suppressing Venice's
> vendor-injected system prompt so the model sees only what the client sent. The flag is forced
> unconditionally — a client cannot override it.

## Model registry

Registry entries (a backend's `Models` array) pin how a client-facing model name maps to an upstream
model on that backend, and may override detected capabilities. Each entry's backend is implied by the
backend it is nested under:

| Key | Type | Default | Description |
| --- | --- | --- | --- |
| `Name` | string | _(required)_ | The model name clients send and see in `/api/tags`. Must be **unique within this backend's registry** (compared case-insensitively, after trimming, as the catalog keys it) — two entries sharing a name are rejected when the configuration is applied. The `UpstreamModel` may repeat freely; only the client-facing name must differ. |
| `UpstreamModel` | string | = `Name` | The model id requested from the backend. Several entries may share one `UpstreamModel` to expose the same upstream model under distinct names (see [Pinning a fixed reasoning effort](#pinning-a-fixed-reasoning-effort)). |
| `SupportsCompletion` | bool? | `true` | Completion (chat/text) support. Set to `false` only for embedding-only models, in which case `SupportsEmbeddings` must be `true`. |
| `SupportsTools` | bool? | `false` | Tool-calling support. |
| `SupportsVision` | bool? | `false` | Vision support. |
| `SupportsEmbeddings` | bool? | `false` | Embedding support. |
| `ContextLength` | int? | _(required¹)_ | Explicit per-model context window (tokens) that **overrides** both the backend-reported and default values — the way to set or narrow this model's window deliberately. ¹Required only when the backend reports no window for this model and no `ContextLength` default supplies one. See [Context window](#context-window). |
| `ReasoningEffort` | enum? | _(none)_ | A **fixed** reasoning effort pinned to this model. Unlike the backend-wide [`ReasoningEffort`](#reasoning-effort) default, a pin is **authoritative**: it overrides both the client's `think` directive and the backend default, so a client can never push a level the model rejects. `null` keeps the normal chain; `"None"` pins reasoning hard off. See [Pinning a fixed reasoning effort](#pinning-a-fixed-reasoning-effort). |

> [!IMPORTANT]
> A registry entry is **fully pinned** — it does **not** run live capability detection. The additive
> `SupportsTools`, `SupportsVision`, and `SupportsEmbeddings` flags each default to `false`, so if a
> registered model supports tools or vision you must set the flag explicitly (e.g.`"SupportsTools": true`).
> `SupportsCompletion` is the exception: it defaults to `true`, since completion is the proxy's
> baseline modality. Set it to `false` for an embedding-only model — but then `SupportsEmbeddings`
> must be `true`, otherwise the entry resolves to no usable endpoint and is rejected at startup.
> If you want capabilities to be *detected* instead of pinned, expose the model through discovery
> on a `PlugAndPlay` or `Hybrid` backend rather than pinning it in the registry.

## Capability detection

For every exposed model the proxy resolves four capabilities — **completion**, **tools**, **vision**,
and **embeddings** — through a staged strategy, stopping at the first conclusive stage:

1. **Backend metadata** — OpenRouter-style `input_modalities`, `output_modalities`, and
   `supported_parameters` from the `/v1/models` listing are authoritative when present. Venice's nested
   `model_spec.capabilities` (`supportsFunctionCalling`, `supportsVision`) is mapped onto the same
   metadata, so its models are detected without a provider-specific path. A model that can only produce
   embeddings is honestly marked as **not** supporting completion.
2. **Active probing** _(on by default, per capability)_ — issues a tiny throwaway request per capability
   to confirm it: a minimal chat completion for **completion** and **tools**/**vision**, and a short
   input to the embeddings endpoint for **embeddings**. A success (HTTP 2xx) implies support; a
   content-level rejection (a non-auth 4xx, including a 400, 404, or 422) implies the opposite. Only
   _transient_ failures — **HTTP 429, HTTP 5xx, and transport faults** — are retried with exponential
   backoff. A **timeout is not retried**: a model too slow to answer within the per-attempt window will
   not answer a second identical attempt any faster, so the whole budget funds **one** adequate attempt
   instead of several short ones. Authentication failures (**HTTP 401/403**) are not retried either —
   they are permanent, and say nothing about capability presence, so the probe ends *inconclusive* rather
   than *unsupported*. When a throttled backend returns a `Retry-After` header it is honored verbatim
   (capped at 60s) in preference to the computed backoff. A probe that stays inconclusive after its
   retries falls through to the conservative default. Each probe is independent and configured per backend:

   ```json
   "Probing": {
     "ProbeCompletion": true,
     "ProbeTools": true,
     "ProbeVision": true,
     "ProbeEmbeddings": true,
     "TimeoutSeconds": 10,
     "InteractiveTimeoutSeconds": 60,
     "MaxProbeRetries": 3,
     "RetryBaseDelaySeconds": 4,
     "MaxConcurrentProbes": 1
   }
   ```

   `TimeoutSeconds` (default `10`) bounds each individual attempt during **startup** discovery — not the
   whole retry chain, and since a timeout is never retried, it is effectively the entire budget one probe
   waits. The on-demand **[Probe capabilities](administration-ui.md#the-backends-page)** action in the
   admin UI is interactive — a person is waiting for a conclusive answer and accepts the latency, often
   including a model's cold-load time — so it uses a separate, larger `InteractiveTimeoutSeconds`
   (default `60`) instead. `MaxProbeRetries` (default `3`, so up to four attempts) and `RetryBaseDelaySeconds`
   (default `4`, giving `4s, 8s, 16s, …` backoff; set to `0` to retry immediately) govern how hard a
   _transient_ failure is retried — so a momentary backend hiccup during discovery does not mislabel a
   model. The defaults favour a conclusive scan over a fast one: a rate limit is usually a per-minute
   window, so a backoff that starts at one second merely re-hits the same wall. Each attempt costs one
   upstream round trip per model.
   Because metadata (stage 1) short-circuits before probing, probes only fire for backends that report
   **no** usable metadata — typically local servers where a round trip is cheap. Turn any probe off per
   backend if the extra call (or, for vision, sending an image to a text-only model) is unwelcome.

   Discovery probes models **in parallel**: a single model's four capability probes (completion, tools,
   vision, embeddings) run **sequentially** within that model — completion first, so its round trip warms
   the model for the probes that follow — and `MaxConcurrentProbes` (default `1`, a fully serialized scan)
   caps how many *models* of a backend are probed at once. The default of `1` is the safe choice against
   rate-limited backends, since concurrent probes are the surest way to trip an HTTP 429; raising it
   shortens the cold start of a backend you know tolerates parallelism — for a provider reporting dozens of
   models, serial probing would otherwise sum every round trip. When a backend does rate-limit, the
   `Retry-After` cooldown one probe earns is **shared across that backend's concurrent probes**, so a single
   throttled model paces the whole scan instead of the other in-flight probes each re-hitting the same
   limit. The limit is per backend, so one slow backend never throttles another.
3. **Conservative default** — when neither metadata nor a conclusive probe is available, the model is
   advertised as **completion-only**: tools, vision, and embeddings stay `false`. Completion is the
   exception to "withhold when unsure": it defaults to **`true`** and is only lowered to `false` by a
   _conclusive_ completion probe (the signal that recognizes an embedding-only model), so a transient
   backend hiccup can never hide a working chat model. Tools, vision, and embeddings are the opposite —
   withheld unless confirmed — because advertising one a backend cannot honor would make a client enable
   tool calling, send an image, or request an embedding the model rejects. An optional capability that
   stays inconclusive after probing is logged at warning level so an operator can pin it explicitly;
   completion staying `true` is logged at information level as the safe, non-lossy outcome. Capabilities
   are **never** guessed from the model name.

A discovered model that ends up supporting **neither completion nor embeddings** — the Ollama-native
surface exposes only those two endpoints — is **not exposed** at all. A pure image-generation model has
no route through the proxy, so listing it would only clutter the client's model picker with a model
every request would reject. Such models are skipped during discovery and logged at information level
(an expected outcome, not a fault). The moment a model also supports completion (a hybrid `image`+`text`
model), it is exposed normally.

> [!IMPORTANT]
> Tool **and vision** support are only ever advertised when confirmed by metadata, probing, or an
> explicit registry pin — never guessed from the model name. **Embedding** support is never inferred from
> backend metadata (no provider reports an embedding signal there), so it is advertised only when
> confirmed by probing or an explicit registry pin. If a model supports tools or vision but isn't
> advertised as such, either pin `"SupportsTools": true` / `"SupportsVision": true` in the registry, or
> rely on probing (on by default) for backends without metadata.

## Context window

Clients such as GitHub Copilot need to know each model's **context window** to size their requests.
If they have to guess and the real window is smaller, requests overflow and fail in confusing ways.
OllamaProxy therefore resolves a concrete context length for every exposed model and advertises it on
the native **`POST /api/show`** surface under `model_info` as **`openai.context_length`** (the
architecture-prefixed key Ollama clients read). The inbound **`GET /v1/models`** surface stays the
standard OpenAI model object and carries no context-window field, since no standard OpenAI client reads
one there.

The context window is fundamentally a **per-model** attribute, and the proxy treats it that way: the
effective value is resolved **for each model individually** from three sources, in strict **precedence
order — the first one present wins**:

1. **Explicit per-model override** — a `ContextLength` on the model's registry entry. This always wins,
   so it is the one place to deliberately **set or narrow** a single model's window below what the
   backend reports (e.g. to cap cost).
2. **Detected** — the backend's reported window, read **per model** from its `/v1/models` listing.
   There is no single standard for this across providers; each provider adapter reads the field its
   backend actually uses:
   - **vLLM** reads `max_model_len` (top-level in vLLM's model object).
   - **OpenRouter** reads the top-level `context_length`, falling back to the nested
     `top_provider.context_length` when the top-level is absent.
   - **Venice** reads `model_spec.availableContextTokens` (nested inside the Venice-specific
     `model_spec` block that also carries capability flags).
   - **OpenAI / generic** reports no context window at all — the official `/v1/models` schema
     does not include one, so this source is always absent for those backends.
3. **Backend default** — the backend's `ContextLength`, one value standing in for all of that backend's
   models that report no window of their own.

The backend default is a **fallback, not a ceiling**: a value the backend reports always wins over it,
so the default never narrows or overrides what the backend actually serves — it only fills the gap for
backends (typically plain OpenAI-compatible ones) that advertise no window at all. To deliberately
constrain a specific model below its reported window, pin it and set the explicit per-model
`ContextLength` override (source 1) rather than lowering the backend-wide default. This keeps the rule
honest in both directions:

- Want to **cap** one model's window (e.g. to control cost)? Pin it and set a smaller `ContextLength`
  override — it wins over the reported value.
- Backend **reports a larger** window than your backend default? The reported value wins automatically,
  so a capable model is never silently throttled to the default.

> [!IMPORTANT]
> If a backend reports **no** context length and you configure none, startup **fails loudly** with a
> message naming the model and the exact keys to set — the proxy never silently guesses a window.
> Plain OpenAI-compatible backends (OpenAI itself, for example) do not advertise one, so set either
> the registry entry's `ContextLength` or the backend's `ContextLength` default for them. Backends
> that do advertise a window in any of the recognized dialects above need no configuration.

**Request guardrail.** On `/api/chat` and `/api/generate`, a request whose `options.num_ctx` exceeds
the model's resolved window is rejected with `400 Bad Request` and a message stating the requested and
allowed sizes — an explicit failure instead of an opaque downstream one.

> [!NOTE]
> The guardrail checks the **declared** `num_ctx`, not the true token count of the prompt (which would
> require a model-specific tokenizer). Accurate advertising via `/api/show` remains the primary
> mechanism; the guardrail is a deterministic backstop against obvious misconfiguration.

## Reasoning effort

Modern "thinking" models can spend a variable amount of internal deliberation before answering.
OllamaProxy exposes this through Ollama's standard **`think`** field on `/api/chat`, resolves it to a
provider-neutral effort, and lets each backend's adapter encode it in that backend's own wire dialect.

Reasoning effort is a **per-request** concern: its primary source is the client's `think` field on each
individual call. The backend's `ReasoningEffort` is only a *fallback default* for requests that don't
carry one — it is not a model attribute and never changes a model's advertised capabilities.

**What clients send.** The inbound `think` field accepts either of Ollama's two shapes:

- **Boolean** — `"think": true` maps to a balanced **`medium`** budget; `"think": false` turns
  reasoning **off**.
- **Level string** — `"think": "low"` (also `none`, `minimal`, `low`, `medium`, `high`, `xhigh`, `max`),
  matched case-insensitively.

**Precedence.** A model's **pinned** [`ReasoningEffort`](#pinning-a-fixed-reasoning-effort) (a registry
entry) wins over everything — it overrides both the request `think` and the backend default. Absent a
pin, a per-request `think` wins; when the request omits it, the backend's `ReasoningEffort` default
applies. If none of these is set, **no reasoning directive is sent at all** — the backend keeps its own
default behavior. An unrecognized level string is ignored rather than guessed.

**How each provider encodes it.** The neutral effort is mapped onto the backend's dialect by its
`ProviderType` adapter:

| `ProviderType` | Encoding | Notes |
| --- | --- | --- |
| `openai` | `reasoning_effort: "<level>"` | The canonical flat field; understood by OpenAI and most compatible servers. |
| `venice` | `venice_parameters.disable_thinking: true` for **off**; `reasoning_effort` otherwise | Assumed to accept the extended `max` level (unverified — not yet measured against a live Venice backend). |
| `vllm` | `reasoning_effort` **and** `chat_template_kwargs.enable_thinking` | Both are written so modern and older vLLM (and template-only setups) all work. |
| `openrouter` | `reasoning: { "effort": "<level>" }` | Uses OpenRouter's unified nested `reasoning` object, then maps a level a given model lacks down to its nearest supported one. Its gateway enum tops out at `xhigh`; `max` is rejected with HTTP 400 (measured 2026), so the ceiling stays at the inherited `xhigh`. |

**Per-provider dialect ceiling.** Each provider declares the highest token its API accepts: `xhigh` for
`openai`, `vllm`, and `openrouter`; `max` for `venice`. A **non-pinned** effort
(from the request `think` or the backend default) that exceeds a provider's ceiling is **clamped** down to
it — for example a `max` request to an `openai` or `openrouter` backend is sent as `xhigh`. This clamp is only a
*token-validity* guard: it stops the proxy from emitting a level the API rejects as unknown, but it
**cannot** know whether the specific model accepts the level, so a strict backend may still
reject it. The only real guarantee is a [pinned effort](#pinning-a-fixed-reasoning-effort), which is sent **verbatim** and is never clamped.

> [!NOTE]
> The neutral vocabulary mirrors OpenAI's published set (`none`, `minimal`, `low`, `medium`, `high`,
> `xhigh`) plus a `max` level that some models and providers accept. Each provider clamps a non-pinned
> effort to its own ceiling, so the same client request behaves sensibly across every backend.

### Pinning a fixed reasoning effort

Some backends — Venice in particular — **reject** a request outright when a model does not support the
reasoning level it carries, instead of clamping to a nearby one. Because there is no API that tells the
proxy which levels a given model accepts, the safe, deterministic way to expose such a model is to **pin
a fixed effort** on its registry entry:

```json
"Models": [
  { "Name": "gpt-5-high", "UpstreamModel": "gpt-5", "SupportsTools": true, "ReasoningEffort": "High" },
  { "Name": "gpt-5-low",  "UpstreamModel": "gpt-5", "SupportsTools": true, "ReasoningEffort": "Low"  }
]
```

A pinned effort is **authoritative**: it overrides both the client's `think` directive and the backend
default, so a client can never push a level the model rejects. It is also exempt from the [per-provider
dialect ceiling](#reasoning-effort) — a pin is sent **verbatim**, never clamped — so pinning is the way to
deliberately use a level above a provider's default ceiling (for example pinning `Max` for a Claude model on
`openai`, whose ceiling is otherwise `xhigh`). The flip side: a pin the backend's API does not accept is
forwarded as-is and may be rejected, by the operator's explicit choice. The resolved value is recorded in
the request trace (as a `pinned (registry)` source) rather than silently changed. Pin the **same upstream
model under several names**, one per level, to expose each effort as its own entry in the client's model
picker (`gpt-5-high`, `gpt-5-low`, …) — the operator decides the naming. The shared part is the
`UpstreamModel`; the `Name` of each entry must be **distinct** (two entries claiming the same client-facing
name collide in the catalog and are rejected when the configuration is applied). Pin `"None"` to turn
reasoning hard off for a model.

> [!NOTE]
> A pinned effort only protects **pinned** models (registry entries, available in `Hybrid` and
> `Explicit`). A model exposed purely through discovery (`PlugAndPlay`, or an unpinned model on `Hybrid`)
> still resolves its effort from the client `think` or the backend default — so for a strict backend
> where a wrong level crashes, **pin the model** (or set a known-safe backend `ReasoningEffort` default)
> rather than relying on discovery.

## Request tracing

`RequestTracing` is an opt-in debugging aid. When enabled, the proxy writes one indented JSON trace file per
request/response flow, covering the inbound client request, the translated backend request, the backend
response, and the outbound client response. The trace also records reasoning provenance, so a surprising
`reasoning_effort` can be traced back to a model pin, a request `think` value, or a backend default.

Tracing is disabled by default and should normally be enabled only for short diagnostic sessions:

| Key | Type | Default | Description |
| --- | --- | --- | --- |
| `Enabled` | bool | `false` | Turns request tracing on. All other settings are ignored while disabled. |
| `Directory` | string | `traces` | Directory where trace files are written. Relative paths resolve against the proxy data directory; absolute paths are honored verbatim. Created on first write. Must be non-blank when tracing is enabled. |
| `MaxFiles` | int | `10000` | Maximum retained trace files. Once the cap is reached, the oldest files are deleted first. Must be greater than zero. |
| `MaxBodyBytes` | int? | `null` | Optional per-body capture limit in bytes. `null` captures bodies in full; a positive value truncates larger bodies and marks them as truncated. |
| `RedactAttachments` | bool | `true` | Replaces inline attachment payloads such as base64 images and data URLs with compact metadata placeholders. Set to `false` only when the exact bytes must be inspected. |

Example:

```json
"RequestTracing": {
  "Enabled": true,
  "Directory": "traces",
  "MaxFiles": 1000,
  "MaxBodyBytes": 1048576,
  "RedactAttachments": true
}
```

Credential-bearing headers such as `Authorization`, `Cookie`, and `api-key` are redacted by the tracing
middleware. Body redaction is deliberately narrower: it omits large inline attachment payloads while leaving
the request and response structure readable.

## Reasoning details cache

Some OpenAI-compatible backends return an opaque `reasoning_details` blob on an assistant turn that includes
tool calls. The blob is not human-readable chain-of-thought; it is a provider-specific continuity token that
some models expect to see again when the follow-up tool-result request is sent. Ollama's wire format has no
field for it, so the proxy can keep the blob server-side and re-attach it to the matching upstream assistant
message on the next turn.

`ReasoningDetailsCache` controls that server-side round-trip:

| Key | Type | Default | Description |
| --- | --- | --- | --- |
| `Enabled` | bool | `true` | Enables capture and re-attachment of `reasoning_details`. Set to `false` to suppress the feature entirely. |
| `SlidingExpirationSeconds` | int | `300` | How long a captured blob is retained after its last read or write. Must be between `1` and `3600` while enabled. |
| `MaxEntries` | int | `1024` | Maximum retained blobs. When full, the least-recently-used entry is evicted. Must be between `1` and `65536` while enabled. |

Example:

```json
"ReasoningDetailsCache": {
  "Enabled": true,
  "SlidingExpirationSeconds": 300,
  "MaxEntries": 1024
}
```

The cache is in-memory and process-local. It is a bounded continuity aid for active tool-calling
conversations, not durable storage. Disabling it is safe, but backends that rely on those blobs may lose
reasoning continuity across tool calls.

## Secrets and environment variables

API keys may be set in `appsettings.json` **or** through environment variables. For production and
containers, **prefer environment variables** — the configuration key path is joined with `__`
(double underscore):

```text
OllamaProxy__Backends__<backendName>__ApiKey
```

For example, the key for a backend named `cloud`:

```bash
export OllamaProxy__Backends__cloud__ApiKey="sk-..."
```

Whether a key is persisted to the file is a deliberate choice, not a fixed rule. The Windows installer
writes your entered key into the **ACL-restricted** `%ProgramData%\OllamaProxy\appsettings.json` (readable
only by `SYSTEM`, Administrators, and the service account — see [Deployment (Windows installer)](deployment.md#deployment-windows-installer)).
The [Administration UI](administration-ui.md#applying-changes) defaults to the same self-contained behavior.
To keep secrets out of the file instead, set `Admin:ApiKeyPersistencePolicy` to `EnvironmentOnly` in
`hostsettings.json` (the outer chassis file); the admin surface then writes every `ApiKey` **blank** and you
supply each one through `OllamaProxy__Backends__<name>__ApiKey`. This is a **deployment-level** setting that
applies to every apply, shown read-only in the admin UI; it is not a per-apply selector. The default,
`WriteToFile`, keeps the self-contained behavior.
Either way, an environment variable always wins over the file at runtime, so it is the safest place for a
key regardless of what the file contains.

> [!NOTE]
> This guidance is about **process-scoped** environment variables (a container's `-e`, a shell
> `export`, a unit file). On a **Windows Service** install, do **not** reach for a *machine-wide*
> environment variable — it is readable by every process on the box. Prefer the ACL-protected
> `appsettings.json` the installer writes, or a service-scoped variable. See
> [Deployment (Windows installer)](deployment.md#deployment-windows-installer).
