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
for example, serve lightweight autocompletion from a local model and heavy chat from the cloud.

> [!NOTE]
> Built on **.NET 10** / ASP.NET Core Minimal API. Single self-contained service, configured via a
> small `appsettings.json` and environment variables.

---

## Contents

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

## Features

- **Ollama-compatible endpoints:** `/api/chat`, `/api/generate`, `/api/tags`, `/api/show`,
  `/api/embeddings`, `/api/embed`, `/api/version`, `/api/ps`, plus a `/health` liveness probe.
- **Real-time streaming translation** between OpenAI SSE and Ollama JSON-Lines, including tool-call
  deltas — so Copilot's tool/agent features light up.
- **Multiple backends** with routing by model name (local + cloud side by side).
- **Three operating modes** on the same machinery:
  - **Plug-and-Play** — just a backend URL + key; models and capabilities are auto-discovered.
  - **Hybrid** — explicit models plus auto-exposed extras.
  - **Explicit** — a fully pinned registry for predictable production behavior.
- **Capability detection** (tools / vision / completion) via backend metadata, optional active
  probing, and a name-based heuristic — required for Copilot to enable tool calling.
- **Effective-config export:** writes a fully resolved `appsettings.generated.json` on startup so
  you can graduate from a two-line quick start to a pinned configuration without guesswork.
- **Provider abstraction** so additional upstreams (e.g. Anthropic, Grok) can be added later
  without touching the core.

---

## Quick start

You need the **.NET 10 SDK** and an API key for an OpenAI-compatible backend.

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

All settings live under the top-level **`OllamaProxy`** section. The shape is:

| Key | Type | Default | Description |
| --- | --- | --- | --- |
| `Mode` | enum | `PlugAndPlay` | How the published model list is assembled. See [Operating modes](#operating-modes). |
| `WriteEffectiveConfig` | bool | `false` | Write `appsettings.generated.json` on startup, then self-disable. |
| `Backends` | map | _(required)_ | One or more named upstream backends. At least one is required. |
| `Models` | array | `[]` | The explicit model registry. Required in `Explicit` mode. |

### Operating modes

A **registry entry** is one element of the `Models` array (see [Model registry](#model-registry)) — an
explicit mapping of a client-facing model name to a backend and upstream model. The published catalog
is always assembled the same way; the mode only decides whether (and which) **discovered** models are
added on top of the registry:

| Mode | Published models |
| --- | --- |
| **`PlugAndPlay`** | Every model each backend reports, with detected capabilities. Registry entries (if any) still take precedence. Zero-friction first run. |
| **`Hybrid`** | Registry entries **plus** the discovered models of any backend with `AutoExpose: true`. |
| **`Explicit`** | Only registry entries. No discovery runs at all — a fully pinned, reproducible surface. |

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

### Backends

Each entry in `Backends` is keyed by a **logical name** (used by the registry and for routing):

| Key | Type | Default | Description |
| --- | --- | --- | --- |
| `BaseUrl` | string | _(required)_ | Absolute URL of the backend's OpenAI-compatible API (e.g. `https://api.openai.com/v1`). |
| `ProviderType` | string | `openai` | Selects the provider adapter. Currently `openai`. |
| `ApiKey` | string | _(required)_ | Bearer token. Min. 8 chars. Prefer an environment variable (see below). |
| `AutoExpose` | bool | `false` | Expose every discovered model. Honored in `Hybrid`, implied in `PlugAndPlay`, ignored in `Explicit`. |
| `Probing` | object | _(disabled)_ | Active capability probing. See [Capability detection](#capability-detection). |

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
   `/v1/models` listing are authoritative when present.
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

Upstream failures are surfaced as Ollama-shaped error bodies (`{"error": "..."}`) in English. A
genuine client error (4xx) from the backend is passed through; anything else is normalized to
`502 Bad Gateway` to signal an upstream problem.

---

## Using with GitHub Copilot (Visual Studio)

GitHub Copilot Chat can use a local Ollama endpoint as a model provider. Point it at the proxy:

1. Start OllamaProxy and confirm `GET /api/tags` lists the model(s) you want.
2. In Visual Studio, open Copilot Chat's model picker and **add an Ollama provider** with the base
   URL `http://localhost:11434`.
3. Pick a model exposed by the proxy. For **tool/agent** features to appear, the model must be
   advertised as tool-capable via `/api/show` — see [Capability detection](#capability-detection).

A practical split is a local backend for fast autocompletion and a cloud backend for heavy chat,
both behind the single proxy endpoint (see [Example configurations](#example-configurations)).

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

For comparison, the proxy-free route (talking to a backend directly) would instead use Continue's
native `openai` provider with an explicit `apiBase` and `apiKey` — useful when you only have a single
backend and don't need discovery or multi-backend routing.

---

## Example configurations

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

### Hybrid (local autocompletion + cloud chat)

A local LM Studio backend auto-exposes its models for autocompletion, while a cloud backend
contributes a pinned, tool-capable chat model:

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
