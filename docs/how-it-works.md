# How it works

> Part of the [OllamaProxy documentation](../README.md#documentation).

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

Two ideas carry the rest of this documentation:

- **One catalog.** At startup the proxy builds a single list of the models it offers, assembled from
  the models you **pin** (the registry) and the models it **discovers** by asking each backend what it
  serves. Everything in [Configuration](configuration.md) is ultimately about shaping that catalog.
- **Two surfaces, one port.** The same catalog is advertised through *both* a native Ollama API
  (`/api/*`) and an OpenAI-compatible API (`/v1/*`) on the same port. A client uses whichever it
  prefers — and some, like GitHub Copilot, use both at once (discovery over `/api`, chat over `/v1`).

Keep those two ideas in mind and every configuration knob in [Configuration](configuration.md) has an
obvious place to live.

One structural idea rounds out the picture: OllamaProxy runs as **two hosts**. A stable **outer chassis**
stays put — it anchors the Windows Service (or your foreground shell), answers the `/health` and `/ready`
probes, and hosts the built-in **[Administration UI](administration-ui.md)** — while a recyclable **inner proxy host** does the actual
translating and serving on `:11434`. Because the chassis never recycles, it can rebuild the inner proxy onto
a new configuration *live*, without dropping its own connection or losing contact with the service manager.
The two hosts read two separate files: the chassis reads [`hostsettings.json`](../src/OllamaProxy/hostsettings.json), the inner proxy reads
[`appsettings.json`](../src/OllamaProxy/appsettings.json).
