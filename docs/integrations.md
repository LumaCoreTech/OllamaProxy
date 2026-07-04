# Using OllamaProxy with editors

> Part of the [OllamaProxy documentation](../README.md#documentation).

- [Using with GitHub Copilot (Visual Studio)](#using-with-github-copilot-visual-studio)
- [Using with Continue.dev](#using-with-continuedev)

## Using with GitHub Copilot (Visual Studio)

GitHub Copilot Chat can use a local Ollama endpoint as a model provider. Point it at the proxy:

1. Start OllamaProxy and confirm `GET /api/tags` lists the model(s) you want.
2. In Visual Studio, open Copilot Chat's model picker and **add an Ollama provider** with the base
   URL `http://localhost:11434`.
3. Pick a model exposed by the proxy. For **tool/agent** features to appear, the model must be
   advertised as tool-capable via `/api/show` — see [Capability detection](configuration.md#capability-detection).

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
endpoint (see [Example configurations](examples.md)). Local-model **autocompletion** in
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

Continue's primary configuration format is `config.yaml`. Add a model entry with `provider: ollama`
and the proxy's URL:

```yaml
name: My Config
version: 0.0.1
schema: v1

models:
  - name: Cloud model (via OllamaProxy)
    provider: ollama
    model: gpt-4o
    apiBase: http://localhost:11434
```

The `model` value is the **client-facing name** as it appears in `/api/tags` — the proxy resolves it
to the right backend and upstream model.

> [!TIP]
> Continue.dev **can** drive local-model autocompletion through the proxy. Assign the `autocomplete`
> role to a fast local model exposed by the proxy:
>
> ```yaml
> models:
>   - name: Cloud model (via OllamaProxy)
>     provider: ollama
>     model: gpt-4o
>     apiBase: http://localhost:11434
>     roles:
>       - chat
>
>   - name: Local autocomplete (via OllamaProxy)
>     provider: ollama
>     model: qwen2.5-coder:1.5b
>     apiBase: http://localhost:11434
>     roles:
>       - autocomplete
> ```
>
> Without explicit `roles`, Continue assigns a model to chat by default, so only add the list
> when you want to be explicit or override the defaults.

> [!NOTE]
> The legacy `config.json` format is still supported but deprecated by Continue.dev. If you are on an
> older installation, the equivalent JSON key for the display name is `title` (not `name`), and
> autocomplete models are configured separately under `tabAutocompleteModel` rather than via `roles`.

For comparison, the proxy-free route (talking to a backend directly) would instead use Continue's
native `openai` provider with an explicit `apiBase` and `apiKey` — useful when you only have a single
backend and don't need discovery or multi-backend routing.
