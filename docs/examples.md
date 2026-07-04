# Example configurations

> Part of the [OllamaProxy documentation](../README.md#documentation).

These three configs trace the same progression as the [operating modes](configuration.md#operating-modes) — from a
single auto-discovered backend, through a mix of per-backend modes, to a fully pinned one. Pick the one
that matches where your deployment is today; moving along the line is mostly a matter of adding `Models`
entries to a backend and tightening its `Mode`.

## Plug-and-Play (one cloud backend)

OpenRouter advertises rich capability metadata, so a single `PlugAndPlay` backend publishes a complete,
accurate catalog with no registry at all:

```json
{
  "OllamaProxy": {
    "Backends": {
      "openrouter": {
        "BaseUrl": "https://openrouter.ai/api/v1",
        "ProviderType": "openrouter",
        "ApiKey": "sk-or-...",
        "Mode": "PlugAndPlay"
      }
    }
  }
}
```

Every backend needs an `ApiKey` of at least eight characters; the `sk-or-...` shown is a placeholder for
your real key. Prefer keeping it out of the file by setting the matching environment variable instead; it
wins over the `ApiKey` value above:

```bash
export OllamaProxy__Backends__openrouter__ApiKey="sk-or-..."
```

## Mixed modes (local + cloud side by side)

Because each backend owns its mode, one deployment can mix them. Here a local LM Studio backend is set
to `PlugAndPlay` to auto-expose its models (handy as Continue.dev autocompletion or a lightweight chat
model), while a cloud backend is `Explicit` and contributes exactly one pinned, tool-capable chat model.
The local backend's `Mode` is set explicitly because the `openai` provider type defaults to `Explicit`.

Both backends use the generic `openai` adapter, which reads no context window from the listing (OpenAI's
`/v1/models` does not advertise one), so each needs a context length supplied: a backend-wide
[`ContextLength`](configuration.md#context-window) default for the auto-discovered local models, and a per-model `ContextLength` on the cloud pin.
Without it the local models would be skipped and the pin would fail startup — see [Context window](configuration.md#context-window).

> [!NOTE]
> `Hybrid` mode is tempting here — auto-expose everything from the cloud backend — but the generic
> `openai` adapter reports no capability metadata, so every discovered model would have to be actively
> probed. On a backend with many models that can mean many sequential round trips at startup. `Hybrid`
> is the right choice when the backend returns rich metadata (OpenRouter, Venice), so probing is
> skipped entirely. For a metadata-free backend like plain OpenAI, pinning the models you actually
> want in `Explicit` is both faster and more predictable.

```json
{
  "OllamaProxy": {
    "Backends": {
      "local": {
        "BaseUrl": "http://localhost:1234/v1",
        "ProviderType": "openai",
        "ApiKey": "lm-studio-placeholder",
        "Mode": "PlugAndPlay",
        "ContextLength": 8192
      },
      "cloud": {
        "BaseUrl": "https://api.openai.com/v1",
        "ProviderType": "openai",
        "ApiKey": "sk-...",
        "Mode": "Explicit",
        "Models": [
          {
            "Name": "gpt-4o",
            "UpstreamModel": "gpt-4o",
            "SupportsTools": true,
            "SupportsVision": true,
            "ContextLength": 128000
          }
        ]
      }
    }
  }
}
```

Here `local` carries the harmless placeholder `lm-studio-placeholder` because LM Studio ignores auth
entirely — the key still only has to clear the eight-character minimum. For the real `cloud` key, prefer the
environment variable; it overrides the file:

```bash
export OllamaProxy__Backends__cloud__ApiKey="sk-..."
```

## Explicit (fully pinned production)

Groq is also served by the generic `openai` adapter, so — exactly as above — each pinned model needs an
explicit `ContextLength`; Groq's listing carries no window for the proxy to inherit.

```json
{
  "OllamaProxy": {
    "Backends": {
      "groq": {
        "BaseUrl": "https://api.groq.com/openai/v1",
        "ProviderType": "openai",
        "ApiKey": "gsk_...",
        "Mode": "Explicit",
        "Models": [
          {
            "Name": "llama-3.3-70b",
            "UpstreamModel": "llama-3.3-70b-versatile",
            "SupportsTools": true,
            "ContextLength": 131072
          }
        ]
      }
    }
  }
}
```

Prefer the environment-variable form for the real key; it overrides the file:

```bash
export OllamaProxy__Backends__groq__ApiKey="gsk_..."
```
