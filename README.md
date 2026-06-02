# OllamaProxy

<div align="center">
  <div style="width: 550px;">
    <table width="550">
      <thead>
        <tr>
          <th align="center" width="150">Platform</th>
          <th align="left">&nbsp;&nbsp;Build Status & Metrics</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td align="center"><b>Windows</b></td>
          <td align="left">
            &nbsp;&nbsp;
            <a href="https://github.com/LumaCoreTech/OllamaProxy/actions/workflows/build.yml"><img src="https://img.shields.io/github/actions/workflow/status/LumaCoreTech/OllamaProxy/build.yml?style=flat-square&label=Build" alt="Windows Build"></a>
            &nbsp;
            <a href="https://github.com/LumaCoreTech/OllamaProxy/actions/workflows/build.yml"><img src="https://img.shields.io/endpoint?url=https://raw.githubusercontent.com/LumaCoreTech/OllamaProxy/badges/ollamaproxy-windows-test-badge.json&style=flat-square" alt="Windows Tests"></a>
            &nbsp;
            <a href="https://github.com/LumaCoreTech/OllamaProxy/actions/workflows/build.yml"><img src="https://img.shields.io/endpoint?url=https://raw.githubusercontent.com/LumaCoreTech/OllamaProxy/badges/ollamaproxy-windows-coverage-badge.json&style=flat-square" alt="Windows Coverage"></a>
          </td>
        </tr>
        <tr>
          <td align="center"><b>Ubuntu</b></td>
          <td align="left">
            &nbsp;&nbsp;
            <a href="https://github.com/LumaCoreTech/OllamaProxy/actions/workflows/build.yml"><img src="https://img.shields.io/github/actions/workflow/status/LumaCoreTech/OllamaProxy/build.yml?style=flat-square&label=Build" alt="Ubuntu Build"></a>
            &nbsp;
            <a href="https://github.com/LumaCoreTech/OllamaProxy/actions/workflows/build.yml"><img src="https://img.shields.io/endpoint?url=https://raw.githubusercontent.com/LumaCoreTech/OllamaProxy/badges/ollamaproxy-ubuntu-test-badge.json&style=flat-square" alt="Ubuntu Tests"></a>
            &nbsp;
            <a href="https://github.com/LumaCoreTech/OllamaProxy/actions/workflows/build.yml"><img src="https://img.shields.io/endpoint?url=https://raw.githubusercontent.com/LumaCoreTech/OllamaProxy/badges/ollamaproxy-ubuntu-coverage-badge.json&style=flat-square" alt="Ubuntu Coverage"></a>
          </td>
        </tr>
      </tbody>
    </table>
  </div>
</div>

**Speak Ollama, run OpenAI.**

OllamaProxy is a lightweight reverse proxy that exposes the **Ollama API** on the outside while
forwarding and translating every request to one or more **OpenAI-compatible backends**
(OpenAI, Groq, OpenRouter, LM Studio, vLLM, llama.cpp, …).

It lets you point any Ollama-aware client — such as **GitHub Copilot Chat in Visual Studio** or
**Continue.dev** — at cloud or local OpenAI-compatible models, with per-model routing so you can,
for example, serve a lightweight local model and a heavy cloud model side by side and pick per task.

> [!NOTE]
> Built on **.NET 10** / ASP.NET Core Minimal API. Single self-contained service, configured via a
> small `appsettings.json` and environment variables.

---

## Contents

- [How it works](#how-it-works)
- [Features](#features)
- [Quick start](#quick-start)
- [Configuration](#configuration)
  - [Operating modes](#operating-modes)
  - [Backends](#backends)
  - [Model registry](#model-registry)
  - [Capability detection](#capability-detection)
  - [Secrets and environment variables](#secrets-and-environment-variables)
  - [The `appsettings.generated.json` workflow](#the-appsettingsgeneratedjson-workflow)
- [Endpoints](#endpoints)
- [Using with GitHub Copilot (Visual Studio)](#using-with-github-copilot-visual-studio)
- [Using with Continue.dev](#using-with-continuedev)
- [Example configurations](#example-configurations)
- [Deployment (Docker)](#deployment-docker)
- [Development conventions](#development-conventions)

---

## How it works

The whole proxy rests on one observation: **the Ollama API and the OpenAI API describe the same
operations in two different dialects.** A chat request, a model listing, an embeddings call — each
exists on both sides, just with different field names and a different streaming format. OllamaProxy
sits in that gap and translates.

A request makes the following trip:

1. **A client speaks Ollama.** It connects to `http://localhost:11434` — Ollama's conventional
   port — and calls a route like `POST /api/chat`, exactly as it would against a real Ollama install.
2. **The proxy resolves the model.** The client-facing model name is looked up in the **catalog**
   (more on that below) to find which **backend** serves it and under which upstream name.
3. **The request is translated and forwarded.** The Ollama request becomes an OpenAI-compatible one
   and is sent to the chosen backend with its own URL and key.
4. **The response is translated back.** OpenAI's streaming SSE — including tool-call deltas — is
   converted to Ollama's JSON-Lines on the fly, so the client sees a response indistinguishable from
   native Ollama.

Two ideas carry the rest of this document:

- **One catalog.** At startup the proxy builds a single list of the models it offers, assembled from
  the models you **pin** (the registry) and the models it **discovers** by asking each backend what it
  serves. Everything in [Configuration](#configuration) is ultimately about shaping that catalog.
- **Two surfaces, one port.** The same catalog is advertised through *both* a native Ollama API
  (`/api/*`) and an OpenAI-compatible API (`/v1/*`) on the same port. A client uses whichever it
  prefers — and some, like GitHub Copilot, use both at once (discovery over `/api`, chat over `/v1`).

Keep those two ideas in mind and every configuration knob below has an obvious place to live.

---

## Features

Most of these fall out of the translate-and-route model above; a few exist to make that model
pleasant to operate in practice.

- **A complete dual surface.** Both APIs are served in full, not just the chat route:
  - **Ollama-native:** `/api/chat`, `/api/generate`, `/api/tags`, `/api/show`, `/api/embeddings`,
    `/api/embed`, `/api/version`, `/api/ps`, plus a `/health` liveness probe.
  - **OpenAI-compatible:** `/v1/models`, `/v1/chat/completions`, `/v1/completions`, `/v1/embeddings`.

  Because both are present, OpenAI-native clients (including GitHub Copilot's chat path) connect
  unchanged — there is no feature that only works through one dialect.
- **Faithful streaming translation.** Responses are converted between OpenAI SSE and Ollama
  JSON-Lines *as they stream*, token by token, so nothing buffers. Tool-call deltas are carried
  across intact, which is what lets Copilot's tool and agent features light up.
- **Many backends behind one endpoint.** Route by model name to send a lightweight local model and a
  heavy cloud model side by side — the client only ever sees one Ollama URL and one flat model list.
- **Three operating modes on the same machinery.** The same catalog builder powers a spectrum from
  zero-config to fully pinned, so you can start loose and tighten later without changing tools:
  - **Plug-and-Play** — just a backend URL and key; every model and capability is auto-discovered.
  - **Hybrid** — pin the few models you care about, let the rest flow in automatically.
  - **Explicit** — a fully pinned registry with no discovery, for reproducible production behavior.
- **Capability detection that Copilot relies on.** Tools, vision, and completion support are resolved
  from backend metadata, an optional active probe, and a name heuristic — the tool flag in particular
  is what unlocks Copilot's agent mode.
- **A graduation path, not a cliff.** Set one flag and the proxy writes a fully resolved
  `appsettings.generated.json` on startup, turning a two-line quick start into a pinned configuration
  you can review and adopt — no guesswork about what was discovered.
- **Room to grow.** A provider abstraction keeps the translation core separate from each backend's
  quirks, so new upstreams (e.g. Anthropic, Grok) can be added without touching the request path.

---

## Quick start

The fastest path to the model above is Plug-and-Play: point the proxy at one backend, give it a key,
and let it discover everything else. You need the **.NET 10 SDK** and an API key for an
OpenAI-compatible backend.

**1. Configure one backend.** Edit [`src/OllamaProxy/appsettings.json`](src/OllamaProxy/appsettings.json) —
the shipped defaults already point at OpenAI in Plug-and-Play mode; you only need to supply a key
(preferably via an environment variable, see [Secrets](#secrets-and-environment-variables)):

```json
{
  "OllamaProxy": {
    "Mode": "PlugAndPlay",
    "Backends": {
      "default": {
        "BaseUrl": "https://api.openai.com/v1",
        "ProviderType": "openai",
        "AutoExpose": true
      }
    }
  }
}
```

**2. Provide the API key** via an environment variable so it never lands in a file:

```bash
# bash / zsh
export OllamaProxy__Backends__default__ApiKey="sk-..."
```

```powershell
# PowerShell
$env:OllamaProxy__Backends__default__ApiKey = "sk-..."
```

**3. Run it:**

```bash
dotnet run --project src/OllamaProxy
```

On startup the proxy queries each backend, builds the model catalog, and begins listening on the
conventional Ollama port (default `http://localhost:11434`). Verify it is up:

```bash
curl http://localhost:11434/api/version
curl http://localhost:11434/api/tags
```

Point any Ollama client at `http://localhost:11434` and you are done — because that is Ollama's
default port, most clients need no configuration at all.

> [!TIP]
> If startup fails immediately with a validation error, the most common cause is a missing or
> too-short API key. Every backend requires a key of at least **8 characters** — for local backends
> that ignore auth, supply any placeholder of sufficient length.

---

## Configuration

Everything here exists to shape the **one catalog** the proxy advertises. All settings live under the
top-level **`OllamaProxy`** section, and the four top-level keys map cleanly onto that job: `Mode` and
`Models` decide *what goes into* the catalog, `Backends` defines *where the models come from*, and
`WriteEffectiveConfig` lets you *snapshot the result*. The shape is:

| Key | Type | Default | Description |
| --- | --- | --- | --- |
| `Mode` | enum | `PlugAndPlay` | How the published model list is assembled. See [Operating modes](#operating-modes). |
| `WriteEffectiveConfig` | bool | `false` | Write `appsettings.generated.json` on startup, then self-disable. |
| `Backends` | map | _(required)_ | One or more named upstream backends. At least one is required. |
| `Models` | array | `[]` | The explicit model registry. Required in `Explicit` mode. |

### Operating modes

The mode is the single biggest lever over the catalog, and the three values form a deliberate
progression — from *"show me everything, I'll sort it out later"* to *"show me exactly what I
listed, nothing else."* You can move along that line as a deployment matures without switching tools
or rewriting your client config.

A **registry entry** is one element of the `Models` array (see [Model registry](#model-registry)) — an
explicit mapping of a client-facing model name to a backend and upstream model. The published catalog
is always assembled the same way; the mode only decides whether (and which) **discovered** models are
added on top of the registry:

| Mode | Published models | Reach for it when |
| --- | --- | --- |
| **`PlugAndPlay`** | Every model each backend reports, with detected capabilities. Registry entries (if any) still take precedence. | You're getting started, exploring a backend, or just want zero friction. |
| **`Hybrid`** | Registry entries **plus** the discovered models of any backend with `AutoExpose: true`. | You want a few models pinned exactly right, but still enjoy auto-discovery for the rest. |
| **`Explicit`** | Only registry entries. No discovery runs at all. | You're in production and want a fixed, reproducible surface that can't drift when a backend changes its listing. |

The progression is also a trust gradient: `PlugAndPlay` trusts the backend's listing completely,
`Explicit` trusts only your file, and `Hybrid` lets you draw that line per backend.

#### How the catalog is merged

The catalog is keyed by **model name** (case-insensitive), built in two passes:

1. **Registry first.** Every `Models` entry is materialized and keyed by its `Name`.
2. **Discovery second** (skipped entirely in `Explicit`). Each eligible backend is queried, and a
   discovered model is added **only if its name is not already in the catalog**.

So the precedence rule is simply: **a registry entry always wins over a discovered model of the same
name.** This is what makes `Hybrid` useful — you pin the handful of models you care about (names,
upstream targets, capabilities) and let everything else flow in automatically. Which backends are
queried in the discovery pass depends on the mode:

- In **`PlugAndPlay`**, *every* backend is discovered (its `AutoExpose` is implied).
- In **`Hybrid`**, only backends with `AutoExpose: true` are discovered; a backend with
  `AutoExpose: false` contributes **only** the models you pin in the registry.

A backend that fails discovery is logged and skipped, so one unreachable backend never blocks startup.

#### Name collisions

Because the catalog is keyed by client-facing name, two backends that report the **same** model name
cannot both be auto-exposed under it — the first one discovered wins, and the shadowed copy is logged
as a warning and left unreachable under that name. There are two deterministic ways to keep both
reachable:

- **Pin distinct names** in the registry (`Models`), which always win over discovery.
- **Set a `ModelPrefix`** on one or both backends, so their auto-exposed models are published as
  `prefix/model` (for example `vllm/gemma4-31b` and `venice/gemma4-31b`).

The prefix is **opt-in and deterministic**: it is applied whenever it is configured, regardless of
whether a collision currently exists. This keeps published names stable — a model's client-facing
name never changes just because another backend started or stopped advertising the same name.
Single-backend deployments can leave `ModelPrefix` unset and keep the shorter, bare names.

### Backends

A backend is one upstream OpenAI-compatible API the proxy can route to — its URL, its key, and a few
knobs that shape how its models enter the catalog. Each entry in `Backends` is keyed by a **logical
name** (used by the registry and for routing):

| Key | Type | Default | Description |
| --- | --- | --- | --- |
| `BaseUrl` | string | _(required)_ | Absolute URL of the backend's OpenAI-compatible API (e.g. `https://api.openai.com/v1`). |
| `ProviderType` | string | `openai` | Selects the provider adapter: `openai` (generic OpenAI-compatible default), `venice`, `vllm`, or `openrouter`. Specialized adapters differ only in how they encode reasoning. See [Reasoning effort](#reasoning-effort). |
| `ApiKey` | string | _(required)_ | Bearer token. Min. 8 chars. Prefer an environment variable (see below). |
| `AutoExpose` | bool | `false` | Expose every discovered model. Honored in `Hybrid`, implied in `PlugAndPlay`, ignored in `Explicit`. |
| `ContextLength` | int? | _(auto)_ | Backend-scoped fallback for context window (tokens): applies to every model of this backend that has no individual `ContextLength` of its own, and acts as a ceiling that can only narrow a detected value. The effective window is still resolved **per model**. See [Context window](#context-window). |
| `ModelPrefix` | string? | _(none)_ | Optional prefix applied to the client-facing name of every **auto-exposed** model of this backend, producing `prefix/model`. Disambiguates the same model served by multiple backends. Only auto-exposed models are prefixed — registry entries are exposed verbatim. The prefix changes only the published name; the unprefixed id is still requested upstream. Must be non-blank and contain no `/`. See [Name collisions](#name-collisions). |
| `ReasoningEffort` | enum? | _(none)_ | Backend-scoped fallback for reasoning effort: used for chat requests to this backend when the client sends no `think` directive. A per-request `think` always overrides it. Affects **request behavior**, not model capabilities. See [Reasoning effort](#reasoning-effort). |
| `Probing` | object | _(disabled)_ | Active capability probing. See [Capability detection](#capability-detection). |

> [!NOTE]
> `ContextLength` and `ReasoningEffort` are **backend-scoped defaults**, but they resolve at a finer
> granularity: `ContextLength` is ultimately resolved **per model** (each model gets its own effective
> value), and `ReasoningEffort` is resolved **per request** (a client's `think` field always takes
> precedence). They live on the backend so one entry covers all of that backend's models — a
> convenience, not a statement about their scope of effect. See [Context window](#context-window) and
> [Reasoning effort](#reasoning-effort) for the full resolution order.

### Model registry

Registry entries (the `Models` array) pin how a client-facing model name maps to a backend and an
upstream model, and may override detected capabilities:

| Key | Type | Default | Description |
| --- | --- | --- | --- |
| `Name` | string | _(required)_ | The model name clients send and see in `/api/tags`. |
| `Backend` | string | _(required)_ | The logical backend name that serves this model. |
| `UpstreamModel` | string | = `Name` | The model id requested from the backend. |
| `SupportsTools` | bool? | `false` | Tool-calling support. |
| `SupportsVision` | bool? | `false` | Vision support. |
| `SupportsEmbeddings` | bool? | `false` | Embedding support. |
| `ContextLength` | int? | _(required¹)_ | Context window (tokens) for this model. ¹Required when neither the backend nor its `ContextLength` default supplies one. See [Context window](#context-window). |

> [!IMPORTANT]
> A registry entry is **fully pinned** — it does **not** run live capability detection. Each
> capability flag you leave unset defaults to `false`, so if a registered model supports tools or
> vision you must set the flag explicitly (e.g. `"SupportsTools": true`). Completion is always
> assumed. If you want capabilities to be *detected* instead of pinned, expose the model through
> discovery (`PlugAndPlay`/`Hybrid` with `AutoExpose`) rather than the registry.

### Capability detection

For every exposed model the proxy resolves three capabilities — **completion**, **tools**, and
**vision** (plus **embeddings**) — through a staged strategy, stopping at the first conclusive stage:

1. **Backend metadata** — OpenRouter-style `input_modalities` and `supported_parameters` from the
   `/v1/models` listing are authoritative when present. Venice's nested
   `model_spec.capabilities` (`supportsFunctionCalling`, `supportsVision`) is mapped onto the same
   metadata, so its models are detected without a provider-specific path.
2. **Active probing** _(optional, off by default)_ — issues a tiny throwaway completion that
   advertises a dummy tool and caps generation at one token; a success implies tool support. Enable
   per backend:

   ```json
   "Probing": { "Enabled": true, "TimeoutSeconds": 10 }
   ```

   Probing costs one upstream round trip per model, so it is **disabled by default**.
3. **Name heuristic** — a last-resort inference from the model name (e.g. embedding-style names are
   treated as embeddings-only).

> [!IMPORTANT]
> Tool support is what allows GitHub Copilot Chat to enable tool/agent calling. If a model that does
> support tools is not advertised as such, either pin `"SupportsTools": true` in the registry or turn
> on probing for that backend.

### Context window

Clients such as GitHub Copilot need to know each model's **context window** to size their requests.
If they have to guess and the real window is smaller, requests overflow and fail in confusing ways.
OllamaProxy therefore resolves a concrete context length for every exposed model and advertises it on
**both** inbound surfaces:

- **`POST /api/show`** reports it under `model_info` as **`openai.context_length`** (the
  architecture-prefixed key Ollama clients read).
- **`GET /v1/models`** reports it as **`max_model_len`** (the field vLLM-native clients read).

The context window is fundamentally a **per-model** attribute, and the proxy treats it that way: the
effective value is resolved **for each model individually** from two sources, **smallest wins**:

1. **Detected** — the backend's reported window, read **per model** from its `/v1/models` listing.
   There is no single standard for this on `/v1/models`, so the proxy understands the common dialects
   and uses the first one a backend provides (in this precedence order):
   - **`max_model_len`** — vLLM and compatible servers (top-level).
   - **`context_length`** — OpenRouter, Together, Fireworks, Mistral (top-level).
   - **`top_provider.context_length`** — OpenRouter's nested underlying-provider window.
   - **`model_spec.availableContextTokens`** — Venice (nested), alongside its
     `model_spec.capabilities` block, which is also mapped onto tool/vision detection.
   - **`meta.n_ctx_train`** — llama.cpp server (nested; the model's trained context length).
2. **Configured** — a `ContextLength` on the registry entry (per model), or, as a fallback, the
   backend's `ContextLength` default (one value standing in for all of that backend's models that
   don't specify their own).

The per-model value always leads; the backend default exists only so you can cover many models at once
without repeating yourself — typically for a plain backend that advertises no window at all. When both
a detected and a configured value are present the **smaller** is used, so configuration can only
**narrow** the window, never widen it past what the backend will actually serve. That covers both
directions safely:

- Want to **cap** the window (e.g. to control cost)? Set a smaller `ContextLength` — it wins.
- Backend later **shrinks** its window below your configured value? The detected (smaller) value wins
  automatically, so you never advertise more than the backend serves.

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
> require a model-specific tokenizer). Accurate advertising via `/api/show` and `/v1/models` remains
> the primary mechanism; the guardrail is a deterministic backstop against obvious misconfiguration.

### Reasoning effort

Modern "thinking" models can spend a variable amount of internal deliberation before answering.
OllamaProxy exposes this through Ollama's standard **`think`** field on `/api/chat`, resolves it to a
provider-neutral effort, and lets each backend's adapter encode it in that backend's own wire dialect.

Reasoning effort is a **per-request** concern: its primary source is the client's `think` field on each
individual call. The backend's `ReasoningEffort` is only a *fallback default* for requests that don't
carry one — it is not a model attribute and never changes a model's advertised capabilities.

**What clients send.** The inbound `think` field accepts either of Ollama's two shapes:

- **Boolean** — `"think": true` maps to a balanced **`medium`** budget; `"think": false` turns
  reasoning **off**.
- **Level string** — `"think": "low"` (also `none`, `minimal`, `medium`, `high`, `xhigh`, `max`),
  matched case-insensitively.

**Precedence.** A per-request `think` always wins. When the request omits it, the backend's
`ReasoningEffort` default applies. If neither is set, **no reasoning directive is sent at all** — the
backend keeps its own default behavior. An unrecognized level string is ignored rather than guessed.

**How each provider encodes it.** The neutral effort is mapped onto the backend's dialect by its
`ProviderType` adapter:

| `ProviderType` | Encoding | Notes |
| --- | --- | --- |
| `openai` | `reasoning_effort: "<level>"` | The canonical flat field; understood by OpenAI and most compatible servers. |
| `venice` | `venice_parameters.disable_thinking: true` for **off**; `reasoning_effort` otherwise | Venice also accepts the extended `max` level. |
| `vllm` | `reasoning_effort` **and** `chat_template_kwargs.enable_thinking` | Both are written so modern and older vLLM (and template-only setups) all work. |
| `openrouter` | `reasoning_effort: "<level>"` | Accepts the full OpenAI-style set (`none`, `minimal`, `low`, `medium`, `high`, `xhigh`) on the flat field, so it inherits the generic encoding unchanged. |

> [!NOTE]
> The neutral vocabulary mirrors OpenAI's published set (`none`, `minimal`, `low`, `medium`, `high`,
> `xhigh`) and adds Venice's `max`. Providers with a narrower range clamp accordingly, so the same
> client request behaves sensibly across every backend.

### Secrets and environment variables

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

Secrets supplied via environment variables are **not** baked into the generated effective
configuration — only file-based values are echoed there.

### The `appsettings.generated.json` workflow

Set `WriteEffectiveConfig: true` and start the proxy once. It resolves the full configuration —
including every model the backends reported and its detected capabilities — and writes
`appsettings.generated.json` next to the app. That file:

- has `Mode` pinned to **`Explicit`** with every discovered model spelled out,
- sets `WriteEffectiveConfig` back to **`false`**, so the mechanism **deactivates itself**,
- leaves environment-variable secrets blank (they are **not** written out).

Review it, copy the parts you want into your own `appsettings.json`, and you have graduated from a
two-line quick start to a fully pinned production surface with no guesswork.

> [!NOTE]
> `appsettings.generated.json` is git-ignored by default. Treat it as generated output, not a source
> of truth.

---

## Endpoints

This is the **two surfaces, one port** idea made concrete. OllamaProxy exposes both inbound surfaces
on the same port, exactly like a real Ollama install: the native Ollama API under `/api`, and the
OpenAI-compatible API under `/v1`. Clients connect through whichever protocol they prefer — GitHub
Copilot, for example, discovers models over `/api` but sends chat over `/v1/chat/completions`.

### Ollama-native surface

| Method & path | Purpose |
| --- | --- |
| `POST /api/chat` | Chat completion (streaming JSON-Lines or single response). |
| `POST /api/generate` | Prompt completion (reuses the chat path with prompt wrapping). |
| `GET /api/tags` | Aggregated list of exposed models in Ollama format. |
| `POST /api/show` | Model details, including `capabilities` (tools / vision / completion) and `model_info`. |
| `POST /api/embed` | Embeddings (current API; single string or array input). |
| `POST /api/embeddings` | Embeddings (legacy single-prompt API). |
| `GET /api/version` | Reports an Ollama-compatible version string. |
| `GET /api/ps` | Running models — always empty, since upstream backends manage their own lifecycle. |
| `GET /health` | Liveness probe (`{"status":"ok"}`); answers even when all backends are unreachable. |

### OpenAI-compatible surface

| Method & path | Purpose |
| --- | --- |
| `GET /v1/models` | Model catalog in OpenAI list format (`{"object":"list","data":[…]}`). |
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

---

## Using with GitHub Copilot (Visual Studio)

GitHub Copilot Chat can use a local Ollama endpoint as a model provider. Point it at the proxy:

1. Start OllamaProxy and confirm `GET /api/tags` lists the model(s) you want.
2. In Visual Studio, open Copilot Chat's model picker and **add an Ollama provider** with the base
   URL `http://localhost:11434`.
3. Pick a model exposed by the proxy. For **tool/agent** features to appear, the model must be
   advertised as tool-capable via `/api/show` — see [Capability detection](#capability-detection).

> [!IMPORTANT]
> Copilot's Ollama provider only drives **Chat**. Visual Studio's **inline autocompletion** (the grey
> ghost-text suggestions) runs on GitHub's own completion models and **cannot** be pointed at a local
> Ollama endpoint or this proxy. So with Copilot, the proxy lets you choose the **chat** model only.
> If you specifically want local-model autocompletion, drive it through **Continue.dev** instead (see
> [Using with Continue.dev](#using-with-continuedev)), which has a dedicated autocomplete model setting.

> [!NOTE]
> Copilot Chat uses **both** surfaces: it discovers models and capabilities over the Ollama `/api`
> routes, then sends the actual chat over `/v1/chat/completions` via its OpenAI client. The proxy
> serves both, so discovery and chat work through the single `http://localhost:11434` endpoint.

A practical split is one backend for chat and others routed by model name, all behind the single proxy
endpoint (see [Example configurations](#example-configurations)). Local-model **autocompletion** in
this split is a Continue.dev feature, not a Copilot one.

## Using with Continue.dev

> [!NOTE]
> Unlike Copilot, **Continue.dev does not need this proxy** to talk to OpenAI-compatible APIs — it
> has a native `openai` provider where you can set `apiBase` and `apiKey` directly. The proxy is
> therefore **optional** here; use it when you want the conveniences below.

Going through OllamaProxy with Continue's `ollama` provider buys you:

- **Auto-discovery** — Continue pulls the model list from `/api/tags` and capabilities from
  `/api/show`, so you don't hand-maintain each model in your Continue config.
- **One endpoint for many backends** — local + cloud sit behind a single Ollama URL, with routing by
  model name, instead of several separate `openai` entries.
- **Consistency with Copilot** — both tools share the same proxy, the same model names, and the same
  advertised capabilities.

In Continue's `config.json`, add a model with `"provider": "ollama"` and the proxy's URL:

```json
{
  "models": [
    {
      "title": "Cloud (via OllamaProxy)",
      "provider": "ollama",
      "model": "gpt-4o",
      "apiBase": "http://localhost:11434"
    }
  ]
}
```

The `model` value is the **client-facing name** as it appears in `/api/tags` — the proxy resolves it
to the right backend and upstream model.

> [!TIP]
> Unlike Copilot, Continue.dev **can** drive local-model autocompletion through the proxy. Point its
> `tabAutocompleteModel` at a fast local model exposed by the proxy (again with `"provider": "ollama"`
> and `"apiBase": "http://localhost:11434"`), while keeping a heavier model for chat.

For comparison, the proxy-free route (talking to a backend directly) would instead use Continue's
native `openai` provider with an explicit `apiBase` and `apiKey` — useful when you only have a single
backend and don't need discovery or multi-backend routing.

---

## Example configurations

These three configs trace the same progression as the [operating modes](#operating-modes) — from a
single auto-discovered backend, through a mixed setup, to a fully pinned one. Pick the one that
matches where your deployment is today; moving to the next is mostly a matter of adding `Models`
entries and tightening `Mode`.

### Plug-and-Play (one cloud backend)

```json
{
  "OllamaProxy": {
    "Mode": "PlugAndPlay",
    "Backends": {
      "openai": {
        "BaseUrl": "https://api.openai.com/v1",
        "ProviderType": "openai",
        "AutoExpose": true
      }
    }
  }
}
```

### Hybrid (local + cloud side by side)

A local LM Studio backend auto-exposes its models (handy as Continue.dev autocompletion or a
lightweight chat model), while a cloud backend contributes a pinned, tool-capable chat model:

```json
{
  "OllamaProxy": {
    "Mode": "Hybrid",
    "Backends": {
      "local": {
        "BaseUrl": "http://localhost:1234/v1",
        "ProviderType": "openai",
        "ApiKey": "lm-studio-placeholder",
        "AutoExpose": true
      },
      "cloud": {
        "BaseUrl": "https://api.openai.com/v1",
        "ProviderType": "openai",
        "AutoExpose": false
      }
    },
    "Models": [
      {
        "Name": "gpt-4o",
        "Backend": "cloud",
        "UpstreamModel": "gpt-4o",
        "SupportsTools": true,
        "SupportsVision": true
      }
    ]
  }
}
```

```bash
export OllamaProxy__Backends__cloud__ApiKey="sk-..."
```

### Explicit (fully pinned production)

```json
{
  "OllamaProxy": {
    "Mode": "Explicit",
    "Backends": {
      "groq": {
        "BaseUrl": "https://api.groq.com/openai/v1",
        "ProviderType": "openai"
      }
    },
    "Models": [
      {
        "Name": "llama-3.3-70b",
        "Backend": "groq",
        "UpstreamModel": "llama-3.3-70b-versatile",
        "SupportsTools": true
      }
    ]
  }
}
```

```bash
export OllamaProxy__Backends__groq__ApiKey="gsk_..."
```

---

## Deployment (Docker)

The repository ships a multi-stage [`Dockerfile`](Dockerfile) (SDK build stage → slim ASP.NET
runtime) and a [`docker-compose.yml`](docker-compose.yml) for a one-command start. **Secrets are
never baked into the image** — supply API keys at run time via environment variables.

### Build and run with `docker`

```bash
docker build -t ollamaproxy:latest .

docker run --rm -p 11434:11434 \
  -e OllamaProxy__Mode=PlugAndPlay \
  -e OllamaProxy__Backends__default__BaseUrl=https://api.openai.com/v1 \
  -e OllamaProxy__Backends__default__ProviderType=openai \
  -e OllamaProxy__Backends__default__AutoExpose=true \
  -e OllamaProxy__Backends__default__ApiKey=sk-... \
  ollamaproxy:latest
```

The container listens on the Ollama port `11434` (via `ASPNETCORE_HTTP_PORTS`) and runs as the
non-root `$APP_UID` user from the base image, so existing Ollama clients connect without
reconfiguration.

### Run with Docker Compose

Copy [`.env.example`](.env.example) to `.env`, fill in your key, then start:

```bash
cp .env.example .env      # then edit .env and set OPENAI_API_KEY
docker compose up --build
```

Compose loads `.env` automatically and injects the key as
`OllamaProxy__Backends__default__ApiKey`. The `.env` file is git-ignored, so your secret never
lands in version control.

> [!TIP]
> To pin a production configuration, mount your own `appsettings.json` read-only into the
> container — for example
> `-v ./appsettings.Production.json:/app/appsettings.json:ro` — and keep only secrets in
> environment variables.

A `/health` liveness probe is wired into the Compose `healthcheck`, so orchestrators can tell when
the proxy is ready.

---

## License

MIT License
