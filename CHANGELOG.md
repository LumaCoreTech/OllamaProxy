# Changelog

All notable changes to **OllamaProxy** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

<!--
  Release ritual:
  1. Accumulate entries in the topmost version section under the standard headings
     (Added / Changed / Deprecated / Removed / Fixed / Security) as changes merge to `main`.
  2. To cut a release: replace the "Unreleased" placeholder date with today's ISO date
     (e.g. ## [0.2.0] - 2026-12-31), add a fresh "## [Unreleased]" section above it,
     update the link references at the bottom, then push the matching `vX.Y.Z` tag —
     release.yml builds/tests, publishes the GHCR image, cuts the GitHub Release, and
     attaches the Windows MSI. MinVer stamps the version straight from that tag.
     -->

---

## [Unreleased]

*Changes that are about to go into the next release...*

---

## [0.1.0] - 2026-07-05

Initial public preview. While on the `0.x` line the HTTP surface, configuration keys, and CLI
may still change between minor versions per Semantic Versioning's pre-stable semantics.

### Added

- **Dual API surface on a single port.** Both dialects are served in full, so Ollama-native and
  OpenAI-native clients (including GitHub Copilot's chat path) connect unchanged:
  - Ollama-native: `/api/chat`, `/api/generate`, `/api/tags`, `/api/show`, `/api/embeddings`,
    `/api/embed`, `/api/version`, `/api/ps`, plus a `/health` liveness probe.
  - OpenAI-compatible: `/v1/models`, `/v1/chat/completions`, `/v1/completions`, `/v1/embeddings`.
- **Bidirectional request/response translation** between the Ollama and OpenAI protocols, so a client
  speaking Ollama sees something indistinguishable from a native Ollama server.
- **Streaming translation, token by token,** between OpenAI SSE and Ollama JSON-Lines with no buffering.
  Tool-call deltas are carried across intact, which is what lights up Copilot's tool and agent features.
- **Reasoning/thinking content support,** including a configurable reasoning-details cache for models
  that emit separate reasoning channels.
- **Many backends behind one endpoint** with per-model routing, so a lightweight local model and a heavy
  cloud model can be served side by side while the client sees a single Ollama URL and one flat catalog.
- **A single model catalog** assembled from the models you pin (the registry) and the ones discovered by
  asking each backend what it serves.
- **Three per-backend operating modes** on the same catalog machinery: **Plug-and-Play** (backend URL and
  key only; everything auto-discovered), **Hybrid** (pin a few models, discover the rest), and **Explicit**
  (a fully pinned registry with no auto-discovery, for reproducible production behavior).
- **Capability detection** for tools, vision, completion, and embedding support, resolved from backend
  metadata and an optional active probe, falling back to a conservative completion-only default.
- **A provider abstraction** that keeps the translation core separate from each backend's quirks, so new
  upstreams can be added without touching the request path.
- **Diagnostic request tracing** that persists each completed request as one indented-JSON file, with the
  trace directory bounded as a ring buffer so a long-running proxy cannot fill the disk.
- **Self-contained .NET 10 service** configured via a small `appsettings.json` and environment variables,
  with secrets supplied through environment variables.
- **Multi-arch container image** (`linux/amd64`, `linux/arm64`) published to the GitHub Container Registry.
- **Windows MSI installer** with a configuration wizard covering backend, listener, and admin endpoints.

[0.1.0]: https://github.com/LumaCoreTech/OllamaProxy/releases/tag/v0.1.0
