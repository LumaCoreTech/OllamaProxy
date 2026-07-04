# Live Provider-Conformance Suite

These tests drive the **real** provider adapters (`OpenRouterProvider`, `VeniceProvider`) through their public
`IProviderAdapter` API against **live** backends — the same code path production routes through. They prove
that the proxy's translation pipeline speaks every feature correctly with a real backend: chat, streaming,
tool-use, vision, embeddings, and the server-side `reasoning_details` round-trip.

Unlike the deterministic integration tests in the parent folder (which inject a canned `HttpMessageHandler`),
the only swapped seam here is `IBackendHttpClientProvider`: a `RecordingHttpClientProvider` hands the provider a
**real** `HttpClient` pointed at the live backend, configured through the production
`BackendHttpClientConfiguration` so the wire setup cannot drift.

## What these tests assert (and what they don't)

- **Asserted:** the call conforms — no `ProviderException` (a mistranslated request would surface as a backend
  4xx turned into one), the response maps cleanly into the Ollama contracts, tool-call arguments arrive as a
  JSON **object** (Ollama's shape, not OpenAI's string), the stream terminates with one `done` chunk, and the
  reasoning round-trip re-attaches the exact captured blob.
- **Not asserted:** the model's exact text, token counts, or which tool it chose — those are
  non-deterministic. A passing run means *"our provider code speaks every feature correctly with this
  backend"*, not *"the model said X"*.

## Gating

The suite is **opt-in** and gated entirely on environment variables, so it is safe to leave in the default test
run: with no variables set, every test reports **Skipped** (never a false Pass) and never flakes in unattended
CI. The gate is implemented natively via `LiveBackendFactAttribute`, which sets the test's `Skip` reason when a
required variable is absent.

A test runs only when **all** the variables it lists are present. The API key gates the whole backend; the
vision/embedding/reasoning model variables gate only their individual test, so a backend that does not offer a
capability simply skips that one test.

## Environment variables

### OpenRouter

| Variable | Required | Default | Purpose |
|---|---|---|---|
| `OLLAMAPROXY_LIVE_OPENROUTER_API_KEY` | **Yes** | — | Bearer key; its absence skips the whole class. |
| `OLLAMAPROXY_LIVE_OPENROUTER_BASE_URL` | No | `https://openrouter.ai/api/v1` | Backend base URL. |
| `OLLAMAPROXY_LIVE_OPENROUTER_CHAT_MODEL` | No | `openai/gpt-5.2` | Model for chat, stream, and tool tests. |
| `OLLAMAPROXY_LIVE_OPENROUTER_VISION_MODEL` | No | — | Enables the vision test when set. |
| `OLLAMAPROXY_LIVE_OPENROUTER_EMBED_MODEL` | No | — | Enables the embeddings test when set. |
| `OLLAMAPROXY_LIVE_OPENROUTER_REASONING_MODEL` | No | — | Enables the reasoning round-trip test when set. |

### Venice

| Variable | Required | Default | Purpose |
|---|---|---|---|
| `OLLAMAPROXY_LIVE_VENICE_API_KEY` | **Yes** | — | Bearer key; its absence skips the whole class. |
| `OLLAMAPROXY_LIVE_VENICE_BASE_URL` | No | `https://api.venice.ai/api/v1` | Backend base URL. |
| `OLLAMAPROXY_LIVE_VENICE_CHAT_MODEL` | No | `qwen3-235b` | Model for chat, stream, and tool tests. |
| `OLLAMAPROXY_LIVE_VENICE_VISION_MODEL` | No | — | Enables the vision test when set. |
| `OLLAMAPROXY_LIVE_VENICE_EMBED_MODEL` | No | — | Enables the embeddings test when set. |
| `OLLAMAPROXY_LIVE_VENICE_REASONING_MODEL` | No | — | Enables the reasoning round-trip test when set. |

## Running

Run only the live suite (PowerShell):

```powershell
# Set the credentials for the backend(s) you want to exercise.
$env:OLLAMAPROXY_LIVE_OPENROUTER_API_KEY = "sk-or-..."
$env:OLLAMAPROXY_LIVE_OPENROUTER_REASONING_MODEL = "anthropic/claude-opus-4.8"

# Run just the Category=Live tests.
dotnet test --filter "Category=Live"
```

Exclude the live suite from a normal run (it already self-skips without credentials, but this avoids even the
discovery overhead):

```powershell
dotnet test --filter "Category!=Live"
```

In Visual Studio Test Explorer, group by **Trait** and run the **Live** group, or use the search box with
`Trait:"Live"`.

## Cost & rate limits

Live calls cost tokens and may rate-limit. Prompts are kept tiny on purpose. Treat this suite as a manual,
opt-in conformance check — not a per-commit gate.
