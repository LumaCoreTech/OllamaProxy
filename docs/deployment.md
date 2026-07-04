# Deployment

> Part of the [OllamaProxy documentation](../README.md#documentation).

- [Deployment (Docker)](#deployment-docker)
- [Deployment (Windows installer)](#deployment-windows-installer)

## Deployment (Docker)

The repository ships a multi-stage [`Dockerfile`](../Dockerfile) (SDK build stage → slim ASP.NET runtime) and
a [`docker-compose.yml`](../docker-compose.yml) for a one-command start.
**Secrets are never baked into the image** — supply API keys at run time via environment variables.

### Build and run with `docker`

```bash
docker build -t ollamaproxy:latest .

docker run --rm -p 11434:11434 \
  -e OllamaProxy__Backends__default__BaseUrl=https://openrouter.ai/api/v1 \
  -e OllamaProxy__Backends__default__ProviderType=openrouter \
  -e OllamaProxy__Backends__default__Mode=PlugAndPlay \
  -e OllamaProxy__Backends__default__ApiKey=sk-or-... \
  ollamaproxy:latest
```

The container listens on the Ollama port `11434` (via `ASPNETCORE_HTTP_PORTS`) and runs as the
non-root `$APP_UID` user from the base image, so existing Ollama clients connect without
reconfiguration.

### Run with Docker Compose

Copy [`.env.example`](../.env.example) to `.env`, fill in your key, then start:

```bash
cp .env.example .env      # then edit .env and set OPENROUTER_API_KEY
docker compose up --build
```

Compose loads `.env` automatically and injects the key as
`OllamaProxy__Backends__default__ApiKey`. The `.env` file is git-ignored, so your secret never
lands in version control.

The sample Compose stack publishes both surfaces:

- `http://localhost:11434` — the Ollama-compatible proxy
- `http://localhost:11435` — the administration UI

The admin UI is intentionally published as `127.0.0.1:11435`, so it stays reachable only from
the local machine, matching the default non-container security posture.

> [!TIP]
> To pin a production configuration, mount your own `appsettings.json` read-only into the
> container — for example `-v ./appsettings.Production.json:/app/appsettings.json:ro` —
> and keep only secrets in environment variables.

The Compose `healthcheck` targets the inner proxy's `/health` **liveness** probe on port `11434`, so a
failing check means the proxy process itself is down (or its configuration is broken). Note that
`/health` is a pure liveness signal — it answers `{"status":"ok"}` even when every backend is
unreachable. The outer chassis additionally exposes a `/ready` **readiness** probe on port `11435` that
reports `503` until the inner proxy host is actually serving; route orchestrators or load balancers on
`/ready` when you need readiness rather than liveness. See [Admin chassis probes](endpoints.md#admin-chassis-probes)
for both probe shapes.

## Deployment (Windows installer)

For Windows machines that should run the proxy as a background service — without installing .NET,
cloning the repository, or touching a terminal — the release page ships an **MSI installer**
(`OllamaProxy-<version>-x64.msi`). It bundles a self-contained build of the app (no prerequisites) and
registers it as an auto-start **Windows Service**.

> [!IMPORTANT]
> The released MSI is **not code-signed**, so Windows **SmartScreen** and **User Account Control** will
> warn that the publisher is unknown ("Windows protected your PC" / "Unknown publisher"). This is
> expected for an unsigned, community-built installer — it does **not** mean the file is unsafe. To
> proceed, click **More info → Run anyway** in the SmartScreen dialog and confirm the UAC prompt. If
> you want the warning gone, see [Signing the installer yourself](#signing-the-installer-yourself).

### Install (interactive)

Double-click the MSI and follow the wizard. It presents two configuration pages before the final
confirmation:

1. **Port configuration** — set the URLs the proxy and the admin panel listen on. Both default to
   `localhost` (`http://localhost:11434` for the Ollama-compatible proxy, `http://localhost:11435` for
   the admin UI). Change the port or bind address here if another process already occupies one of them.
   Click **Check ports** to confirm both are free before proceeding.

2. **Backend configuration** — pick your **provider** (`OpenAI-compatible`, `Venice`, `OpenRouter`, or
   `vLLM`). Selecting one prefills its canonical **base URL**, which stays editable for custom
   endpoints. Enter your **API key**, then click **Test connection** to verify both live: the installer
   issues a real request and tells you, before anything is committed, whether the key is accepted and
   whether the URL is missing its `/v1` segment.

When setup finishes, the **OllamaProxy** service is running and the admin UI opens automatically in
your default browser, so you can confirm the catalog and probe capabilities straight away.

### Install (silent / unattended)

The MSI supports a fully unattended install for fleet rollout. Pass the backend and endpoint settings
as public properties — `OLLAMAPROXY_PROVIDERTYPE` defaults to `openai` and both URL properties default
to `localhost`, so set only what differs:

```powershell
msiexec /i OllamaProxy-1.0.0-x64.msi /qn `
  OLLAMAPROXY_PROVIDERTYPE="openai" `
  OLLAMAPROXY_BASEURL="https://api.openai.com/v1" `
  OLLAMAPROXY_APIKEY="sk-..." `
  OLLAMAPROXY_LISTENURL="http://localhost:11434" `
  OLLAMAPROXY_ADMINURL="http://localhost:11435"
```

### Where things live

| Path | Purpose |
| --- | --- |
| `%ProgramFiles%\OllamaProxy\` | The service binaries and `appsettings.reference.json` (an annotated reference of every setting). Read-only at runtime. |
| `%ProgramData%\OllamaProxy\appsettings.json` | Backend configuration: the `default` backend (provider, base URL, API key) and the Ollama-compatible listener URL. Written by the installer wizard. |
| `%ProgramData%\OllamaProxy\hostsettings.json` | Outer chassis configuration: the admin UI URL and host mode. Written by the installer wizard alongside `appsettings.json`. |
| `%ProgramData%\OllamaProxy\` | Both config files live here, secured so only Administrators, `SYSTEM`, and the service account can read them — the API key is never world-readable. Also the writable data area for request traces. |

The service runs under the isolated virtual account **`NT SERVICE\OllamaProxy`**, so its token — and
therefore the API key it reads — is not shared with any other service on the machine.

> [!CAUTION]
> **Do not** put the API key in a *machine-wide* environment variable. Machine (system) environment
> variables live in the registry and are readable by **every** user and process on the box, so the
> secret would leak far beyond the service. The installer deliberately writes the key into the
> ACL-restricted `%ProgramData%\OllamaProxy\appsettings.json` (readable only by `SYSTEM`,
> Administrators, and the service account) — that is the most secure location, so **leave it there**.
> If you really must use an environment variable, make it **service-scoped**, not machine-wide: add a
> multi-string `Environment` value (e.g. `OllamaProxy__Backends__default__ApiKey=sk-...`) under
> `HKLM\SYSTEM\CurrentControlSet\Services\OllamaProxy`, which Windows injects only into the service's
> own process — never the machine environment that other processes can read. Manage the service with
> `sc.exe` or `services.msc` under the name **OllamaProxy**.

An upgrade installs over the previous version and **preserves your existing
`%ProgramData%\OllamaProxy\appsettings.json` and `hostsettings.json`**, so reconfiguration is never
lost — only the binaries are refreshed. Uninstalling removes the program files and the service but
intentionally keeps the data folder. A later **fresh install** (after an uninstall) therefore treats
the wizard as authoritative: it writes the values you enter, and if configuration files from a previous
installation are still in the data folder each is first moved aside to a timestamped backup (for
example `appsettings.20260607-001530.bak`) in the same secured folder, so nothing is overwritten
silently.

### What the installer configures (and what it doesn't)

The installer seeds a **minimal, single-backend configuration** split across two files:

- **`appsettings.json`** — one `default` backend (provider type, base URL, API key) in `PlugAndPlay`
  mode, the Ollama-compatible listener URL, and a standard logging block.
- **`hostsettings.json`** — the admin UI URL and the host mode (`Auto`), which govern the outer chassis
  and are kept separate so the admin connection stays alive while the inner proxy is recycled.

That is everything a one-backend setup needs, but it is only a fraction of what `appsettings.json`
supports. The installer does **not** author:

- **Multiple backends**, or any per-backend [`Hybrid` / `Explicit`](configuration.md#operating-modes) modes.
- A **model registry** (`Models`) — pinned names, `UpstreamModel`, or capability flags.
- Per-backend knobs: `ModelPrefix`, `ContextLength`, `ReasoningEffort`, or `Probing`.
- Request **tracing**.

To use any of those, edit `%ProgramData%\OllamaProxy\appsettings.json` directly (the annotated
`%ProgramFiles%\OllamaProxy\appsettings.reference.json` documents every setting), then restart the
service: `Restart-Service OllamaProxy`. The full configuration reference is the
[Configuration](configuration.md) document.

### Build the installer locally

Building the MSI from source is a **two-step pipeline** — publish the self-contained app first, then
package it with WiX — and both the order and the publish *profile* matter. To avoid memorizing that,
use the helper script: it scrubs stale build output from the project folder, publishes with the
`win-x64` profile, builds the MSI, and verifies the result.

```powershell
# Run from the repository root:
.\installer\build-installer.ps1                       # default version (from the WiX project)
.\installer\build-installer.ps1 -Version 1.2.3        # stamp ProductVersion 1.2.3
.\installer\build-installer.ps1 -Version 1.2.3 -Clean # wipe prior installer artifacts first
```

The finished MSI lands at
`artifacts\installer\OllamaProxy.Installer\bin\x64\Release\OllamaProxy.msi`. The script is Windows-only
(it produces a `win-x64` MSI) and needs only the .NET SDK — the WiX v7 build tooling is restored
automatically as NuGet packages. CI runs the same two steps in
[`build.yml`](../.github/workflows/build.yml) and [`release.yml`](../.github/workflows/release.yml).

### Signing the installer yourself

The MSI on the releases page is **unsigned**, which is why SmartScreen warns about an unknown
publisher (see the note at the top of this section). There is unfortunately **no free path to a
trusted code-signing certificate**: the public CAs that chain to the Windows-trusted roots
(DigiCert, Sectigo, GlobalSign, …) all charge for Authenticode certificates — typically on the order
of a few hundred US dollars per year, and the cheaper OV certificates still require organization
validation. A free or self-signed certificate **will not** clear SmartScreen on other people's
machines, because their Windows does not trust your root; it only helps on machines where you have
installed your own certificate into the Trusted Root / Trusted Publishers stores.

If you have your own certificate (a paid Authenticode cert, or a self-signed one for internal
machines you control), you can sign the MSI after building it:

```powershell
signtool sign /fd SHA256 /a /tr http://timestamp.digicert.com /td SHA256 `
  "OllamaProxy-<version>-x64.msi"
```

