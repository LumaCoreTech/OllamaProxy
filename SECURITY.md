# Security Policy

Thanks for helping keep **OllamaProxy** and its users safe.

## Supported versions

OllamaProxy is maintained on a forward-only basis: **only the latest released version is supported.**
Security fixes are shipped in the next release rather than backported to older ones — if you are
affected, the remedy is to upgrade to the latest release.

While on the `0.x` line the HTTP surface, configuration keys, and CLI may still change between
releases (see [`CHANGELOG.md`](CHANGELOG.md)).

## Reporting a vulnerability

**Please do not open a public issue for security vulnerabilities.**

Instead, report privately through GitHub's built-in
[**private vulnerability reporting**](https://github.com/LumaCoreTech/OllamaProxy/security/advisories/new):

1. Go to the repository's **Security** tab → **Report a vulnerability**.
2. Describe the issue, the affected version or commit, and a minimal reproduction if possible.
3. Include the impact you foresee (for example: key disclosure, request smuggling, denial of service).

If private reporting is unavailable to you, open a regular issue that contains **only** a request to be
contacted about a security matter — **without any details** — and a maintainer will follow up privately.

### What to expect

- **Acknowledgement:** we aim to confirm receipt within a few days.
- **Assessment:** we will investigate, determine severity, and keep you updated on progress.
- **Fix & disclosure:** once a fix is ready we will release it, credit you (if you wish), and publish a
  security advisory. We favour coordinated disclosure and ask that you give us a reasonable window before
  any public write-up.

## Security-relevant surfaces

OllamaProxy sits on the request path between clients and one or more OpenAI-compatible backends, so a few
areas deserve particular attention when reporting or reviewing:

- **Backend credentials.** The proxy holds API keys for its configured backends. The secure place to
  store a key depends on the deployment: for containers and shell-launched processes, a **process-scoped**
  environment variable; for a Windows Service, the **ACL-restricted** `appsettings.json` the installer
  writes (readable only by `SYSTEM`, Administrators, and the service account) — a machine-wide environment
  variable is *worse* there because every process on the box can read it. See
  [Secrets and environment variables](docs/configuration.md#secrets-and-environment-variables) for the full
  guidance. Findings that leak keys (logs, error responses, traces) are in scope regardless of storage
  method.
- **Diagnostic request tracing.** When enabled, tracing persists request/response content as JSON files on
  disk. A redaction pass strips attachment payloads, but reports of sensitive data surviving redaction, or
  traces being written where they should not be, are in scope.
- **Admin surface.** The administration endpoints and UI change operator-facing configuration; access-control
  or input-validation gaps there are in scope.
- **Translation & forwarding.** Request/response translation between the Ollama and OpenAI dialects handles
  untrusted input on both sides; parsing, smuggling, or resource-exhaustion issues are in scope.

## Out of scope

- Vulnerabilities in the upstream backends themselves (report those to the respective provider).
- Issues that require a pre-compromised host or physical access to the machine running the proxy.
- Missing hardening that is already the operator's documented responsibility (see
  [`docs/deployment.md`](docs/deployment.md)).
