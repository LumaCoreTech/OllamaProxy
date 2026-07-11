# Administration UI

> Part of the [OllamaProxy documentation](../README.md#documentation).

You do not have to hand-edit `appsettings.json` to operate the proxy. OllamaProxy ships a built-in
**web administration UI** — a Blazor Server app — that lets you inspect every backend, see which models
each one offers, pin the ones you want, and push the result live. It is the recommended way to shape the
catalog; the [Configuration](configuration.md) reference documents the same file the UI writes, for when you prefer to edit
it directly or need a setting the UI does not yet expose.

Crucially, the UI is hosted on the **outer chassis**, not the inner proxy it manages (see [How it works](how-it-works.md)).
That is what lets it apply a configuration change by **recycling the inner proxy host live** — rebuilding it
onto the new configuration and swapping atomically — without the UI ever losing its own connection.
A rejected configuration never goes live, and the file on disk is rolled back to match (see [Applying changes](#applying-changes)).

## Enabling and reaching it

The admin surface is configured in the chassis's own file,
[`hostsettings.json`](../src/OllamaProxy/hostsettings.json) — **not** `appsettings.json` — under the `Admin` section.
It is **enabled by default** and bound to **localhost**, so a fresh install is manageable immediately and
never exposed off-box without an explicit choice:

| Key | Type | Default | Description |
| --- | --- | --- | --- |
| `Admin.Enabled` | bool | `true` | Whether the UI is served at all. Set to `false` to disable it entirely — no admin page and no realtime connection, leaving only the `/health` and `/ready` probes on this port. |
| `Admin.ListenUrl` | string | `http://localhost:11435` | The address the chassis listens on, deliberately separate from the proxy port (`:11434`) so the two hosts never collide. Use `http://0.0.0.0:11435` to listen on all interfaces (e.g. inside a container). |
| `Host.Mode` | enum | `Auto` | How a failed inner-proxy **start** is handled: `Daemon` stays resident (for a Windows Service or systemd), `Foreground` fails fast (for a console/container), `Auto` picks per environment. A failed live **recycle** is always non-fatal — the previous inner host keeps serving. |

Any key can be overridden by an environment variable using `__` (double underscore) as the separator —
for example `Admin__ListenUrl=http://0.0.0.0:11435` or `Admin__Enabled=false` — which always wins over the
file.

> [!IMPORTANT]
> The host part of `Admin.ListenUrl` must name a **specific interface**: `localhost`, an IP literal (for
> example `127.0.0.1` or `0.0.0.0`), or an explicit wildcard (`[::]`, `*`, `+`). Kestrel does **not** resolve
> a DNS host name in a bind URL — it silently binds *every* interface instead. To avoid that quiet
> over-exposure, a DNS host name such as `http://my-server:11435` is rejected at startup.

With the defaults, open the UI at:

```text
http://localhost:11435
```

> [!NOTE]
> The UI binds to `localhost` by default, so it is reachable only from the same machine. To administer a
> remote or containerized deployment, set `Admin.ListenUrl` to a non-loopback address **and** put it behind your
> own authenticating reverse proxy or network controls — the admin surface has no built-in authentication
> and edits the live proxy configuration.

## The Backends page

The **Backends** page is the heart of the UI. It lists every configured backend as a collapsible card; each
card header shows the backend's name and a badge for its effective [mode](configuration.md#operating-modes) (*Plug-and-play* / *Hybrid* / *Explicit*).
Expanding a card opens an editor and fetches that backend's models on demand, showing:

- **Connection settings** — name, base URL, provider, API key, and mode, plus an **Advanced** section for
  the rarely-changed knobs (see [Applying changes](#applying-changes)).
- **A reconciliation summary** — how many models are *available*, *unavailable*, *discovered*, and how
  many pinned models have **drifted** (their pinned capabilities or context window no longer match what
  the backend now reports).
- **A model table** — every model the backend offers or that you have pinned, with its upstream id,
  state, detected capabilities, and context window.

If a backend cannot be reached, the error is reported inline in that backend's model area instead of the
table, so an unreachable backend never blanks the rest of the page.

Two actions refine what you see:

- **Probe capabilities** — capability detection is *skipped* on load so the page is fast; click this on a
  reachable backend to actively probe it (completion, tools, vision, embeddings) and fill in the
  capability columns on demand. Metadata-rich backends like OpenRouter and Venice populate the
  capability column immediately without active probing.
- **Pinning** — on `Hybrid` and `Explicit` backends, each model row carries a checkbox. Pinning a model
  writes it into that backend's `Models` registry, capturing its current upstream id, capabilities, and
  context window so the surface stays fixed even if the backend's listing changes later. Pinning is hidden
  for `Plug-and-play` backends, whose registry is ignored by design.

## Applying changes

Every edit — pins, connection settings, and the advanced knobs — is **staged** in the editor, not applied
immediately. The **Apply** button commits the whole configuration in one transactional step:

1. The new configuration is **written** to `appsettings.json`.
2. The inner proxy host is **recycled** onto it, with a dry-run validation first.
3. If validation **passes**, the new host swaps in and the change is live.
4. If validation **fails**, the change is **rejected and rolled back**: the file on disk is restored and
   the previous inner host keeps serving — so a bad edit can never take the proxy down or survive to break
   the next restart.

The result banner reports exactly which of these happened.

How each backend's `ApiKey` is persisted is a **deployment setting**, not a per-apply choice. It is
configured once through the `Admin:ApiKeyPersistencePolicy` key in [`hostsettings.json`](configuration.md)
and shown **read-only** next to the **Apply** button so you can see which policy is in effect:

- **Save in configuration file** *(default — `WriteToFile`)* — keys are written into `appsettings.json`
  verbatim, for a self-contained file you can copy between machines.
- **Environment variables only** *(`EnvironmentOnly`)* — every backend's key is written **blank**, forcing it
  to be supplied at runtime through `OllamaProxy__Backends__<name>__ApiKey`. A backend missing that variable
  then fails validation on the next recycle, surfacing the gap immediately. See
  [Secrets and environment variables](configuration.md#secrets-and-environment-variables).

> [!NOTE]
> The UI covers backends end to end — adding and removing them, their connection settings, the model
> registry, and each backend's **Advanced** knobs: a default `ContextLength`, a `ModelPrefix`, a default
> `ReasoningEffort`, and capability `Probing`. The one section it does not yet expose is request **tracing**
> (`RequestTracing`); edit [`appsettings.json`](configuration.md) directly for that.
