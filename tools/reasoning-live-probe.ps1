# Copyright (c) 2026 LumaCoreTech
# SPDX-License-Identifier: MIT
# Project: https://github.com/LumaCoreTech/OllamaProxy

<#
.SYNOPSIS
    Live-backend diagnostic harness for the un-measured assumptions in the OllamaProxy provider layer. Talks RAW
    to the real backend, deliberately bypassing the proxy code, so each assumption is measured against independent
    backend behavior instead of testing itself. Provider-aware: every test sends byte-for-byte what the ACTIVE
    provider's adapter (OpenRouter, Venice, OpenAI, or vLLM) would write, selected with -Provider.

.DESCRIPTION
    Comments across the provider layer describe assumptions about real backend behavior, several explicitly
    flagged "not yet measured against a live backend". Mock tests cannot retire those comments honestly (they
    would be circular). This script exercises each assumption against a real OpenAI-compatible backend and prints
    a PASS / FAIL / INCONCLUSIVE verdict plus raw evidence, so each comment can be rewritten truthfully.

    The harness resolves a provider DIALECT up front (see Get-Dialect) and every test asks that dialect for its
    wire form, so the measurement is faithful to the backend actually under test. Each provider's reasoning wire
    form, off-switch, sampling-extension and vendor-parameter behavior, catalog shape, and key requirements differ;
    the dialect encodes all of them. Tests that are not meaningful for a dialect are skipped with a clear note
    (e.g. F needs sampling forwarding, which the plain OpenAI dialect does not do; G is Venice-only).

    The tests fall into three families:

      Reasoning assumptions (A-D):
        A. The backend accepts the dialect's CEILING reasoning effort (the highest non-pinned token the adapter
           emits: "xhigh" for OpenRouter/OpenAI/vLLM, "max" for Venice). A "high" control gates the verdict; a
           "max" negative control runs when the ceiling sits below "max".
           -> <Provider>Provider.MaxDialectReasoningEffort / ApplyReasoning().
        B. A backend that emits reasoning_details sends the COMPLETE array on a single (or cumulative) streamed
           delta, not fragmented across deltas.
           -> OpenAiCompatibleProvider.ObserveReasoningDetails() ("last non-null blob is the complete one").
        C. The reasoning-details round-trip works end to end: re-attaching the captured opaque blob on the
           follow-up turn is accepted and yields a coherent continuation.
           -> ReasoningDetailsCacheOptions ("not yet measured against a live backend").
        D. The correlation key survives a client that deserializes and re-serializes the assistant history
           (offline; no backend or key needed).
           -> ReasoningDetailsCorrelation ("not measured against a live backend").

      Wire-shape assumptions (E-G):
        E. The dialect's reasoning OFF switch is accepted (reasoning_effort="none", venice disable_thinking, or
           vLLM enable_thinking=false), gated by a no-reasoning control.
        F. The de-facto top_k / min_p sampling extensions are accepted (only for dialects that forward them).
           -> <Provider>Provider.ApplySamplingExtensions() -> WriteTopKAndMinP().
        G. The forced vendor parameters are accepted (Venice include_venice_system_prompt=false), gated by a
           no-vendor control.
           -> VeniceProvider.ApplyVendorParameters().

      Capability-prober assumptions (H-L) -- each mirrors OpenAiCapabilityProber byte-for-byte:
        H. The completion probe payload is accepted (silent prompt, streamed, no token cap).
        I. The tool probe payload is accepted (silent prompt + trivial "ping" function). NB a 2xx confirms the
           payload was ACCEPTED, not that tools were exercised -- the prober's documented silent-ignore caveat.
        J. The SHIPPED placeholder image passes a real vision backend's content validation (the payload itself is
           under test; a 4xx on a vision model means the image regressed).
        K. The embedding probe payload is accepted on the /embeddings endpoint (non-streaming).
        L. A streamed chat request returns headers at stream-open, before generation completes -- the timing
           assumption AddStreaming() and the prober's headers-only read rest on.

    The script NEVER routes through the proxy. It speaks the OpenAI wire protocol directly. Test D runs entirely
    offline -- it is a pure local JSON round-trip measurement and needs no backend or key.

.PARAMETER Tests
    Which specific tests to run: any of A-L. Ignored when -All is given. When neither -Tests nor -All is
    specified, all tests applicable to the selected provider run. The selection is narrowed to the active
    dialect's ApplicableTests, so an inapplicable test (e.g. G on a non-Venice backend) is skipped with a note.
    Test D runs offline (no API key required).

.PARAMETER All
    Run every test applicable to the selected provider. Equivalent to omitting both -All and -Tests.

.PARAMETER ListModels
    Discovery mode: query GET /models and project the catalog through the active dialect. When the catalog
    carries capability metadata (OpenRouter, Venice) it flags tool/reasoning candidates and, where the catalog
    declares a reasoning-effort enum, whether any model advertises "max"; for minimal catalogs (OpenAI, vLLM) it
    lists model ids so a slug can be picked for the live tests. Runs no tests; ignores -Tests / -All.

.PARAMETER Provider
    The backend provider whose dialect drives every test: openrouter (default), venice, openai, or vllm. Selects
    the reasoning wire form, off-switch, sampling/vendor behavior, base URL, key source, and applicable-test set.

.PARAMETER BaseUrl
    The OpenAI-compatible base URL of the backend under test. Empty by default: the active dialect supplies its
    canonical URL. REQUIRED for self-hosted vLLM, which has no canonical URL.

.PARAMETER Model
    The model id to probe. Empty by default: resolved from the active dialect's DefaultModel, or REQUIRED when the
    dialect has none (OpenAI, vLLM). For tests B/C you need a reasoning- and tool-capable model that actually emits
    reasoning_details; for J an actual vision model; for K an embedding model. Override to match your access.

.PARAMETER MaxOutputTokens
    Upper bound on generated tokens per probe call, to keep cost negligible. Default: 512.

.PARAMETER ShowBodies
    Print full request/response bodies (verbose). Off by default; verdict lines already carry the key evidence.

.INPUTS
    The backend API key is read from the active provider's environment variable (OPENROUTER_API_KEY,
    VENICE_API_KEY, or OPENAI_API_KEY) -- never passed as an argument, matching the repository's secret-handling
    discipline. Self-hosted vLLM is keyless (no variable, no Authorization header). Every backend-hitting test
    needs the key; test D does not.

.EXAMPLE
    $env:OPENROUTER_API_KEY = '...'; ./tools/reasoning-live-probe.ps1 -All

.EXAMPLE
    $env:VENICE_API_KEY = '...'; ./tools/reasoning-live-probe.ps1 -Provider venice    # all Venice-applicable tests

.EXAMPLE
    ./tools/reasoning-live-probe.ps1 -Provider vllm -BaseUrl http://localhost:8000/v1 -Model my-model -Tests A,E

.EXAMPLE
    ./tools/reasoning-live-probe.ps1 -Tests D    # offline key-stability measurement, no API key needed

.EXAMPLE
    $env:OPENROUTER_API_KEY = '...'; ./tools/reasoning-live-probe.ps1 -Tests H,I,J,K,L    # capability-prober tests

.EXAMPLE
    $env:VENICE_API_KEY = '...'; ./tools/reasoning-live-probe.ps1 -Provider venice -ListModels    # discover slugs

.NOTES
    Cost: the backend-hitting tests make a handful of tiny real API calls. Output is capped via -MaxOutputTokens.
    This tool adds no dependency (pure PowerShell + .NET BCL), so THIRD-PARTY-NOTICES.md is unaffected.
#>

[CmdletBinding()]
param(
    [ValidateSet('A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J', 'K', 'L')]
    [string[]] $Tests = @(),

    [switch] $All,

    [switch] $ListModels,

    [ValidateSet('openrouter', 'venice', 'openai', 'vllm')]
    [string] $Provider = 'openrouter',

    # Empty by default: the active provider dialect supplies its canonical base URL. An explicit value always
    # wins (required for self-hosted vLLM, which has no canonical URL).
    [string] $BaseUrl = '',

    # Empty by default: resolved from the active dialect's DefaultModel, or required when the dialect has none.
    [string] $Model = '',

    [int] $MaxOutputTokens = 512,

    [switch] $ShowBodies
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 may default to an older TLS; force TLS 1.2 so HTTPS to the backend works there too.
# Harmless on PowerShell 7+, which already negotiates modern TLS.
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12

# System.Net.Http is part of the shared framework on PS 7+ (Add-Type would fail), but must be loaded on 5.1.
# SilentlyContinue swallows the "already loaded" error on 7+.
Add-Type -AssemblyName System.Net.Http -ErrorAction SilentlyContinue

# Per-test verdicts, filled by Write-Verdict and consumed by the final summary.
$script:Findings = @{}

# Cross-test capture from Test B, replayed by Test C (the opaque blob + the tool call that anchors its key).
$script:Capture = $null

# The capability-probe prompt, byte-identical to OpenAiCapabilityProber.SilentProbePrompt: it asks the model to
# stay silent so a probe generates as little as possible without a token-cap parameter (max_tokens is rejected by
# OpenAI reasoning models; max_completion_tokens is not yet universal). The capability tests (H-L) reuse it so
# they send exactly what the production prober sends.
$script:SilentProbePrompt = 'Respond with nothing.'

# The vision-probe image, byte-identical to OpenAiCapabilityProber.PlaceholderImageDataUri: a busy 96x96 JPEG
# (diagonal gradient + several distinct coloured shapes). The decisive constraint is image CONTENT, not size --
# some backends run a feature check and reject a flat/monochrome placeholder, which would surface as a
# body-rejecting 4xx the prober reads as "vision unsupported". Keeping this string identical means Test J
# measures the SAME image the prober ships, so a PASS here is evidence the shipped probe image is good.
$script:PlaceholderImageDataUri =
    'data:image/jpeg;base64,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'

#region Provider dialects

<#
.SYNOPSIS
    Returns the wire-dialect descriptor for a provider, encoding byte-for-byte what that provider's
    OpenAiCompatibleProvider subclass writes on a chat-completions request. Every test asks the ACTIVE dialect
    for its wire form instead of hardcoding one provider, so the harness measures each backend with exactly the
    request its production adapter would send.
.DESCRIPTION
    Each descriptor mirrors one production adapter so a measurement is meaningful for that backend:

      * Name              -- the provider type, used as the reasoning-details correlation scope (Test D) and in
                             all diagnostics. Matches ProviderDescriptor.ProviderType byte-for-byte.
      * BaseUrl           -- the provider's canonical OpenAI-compatible base URL (overridable via -BaseUrl);
                             empty for a self-hosted backend (vLLM) where -BaseUrl is required.
      * ApiKeyEnvVar      -- the environment variable the key is read from; empty for a keyless self-hosted
                             backend (vLLM), in which case no Authorization header is sent.
      * WriteReasoning    -- { param($Body, $Token) } writes a POSITIVE effort token in the provider's dialect:
                             flat reasoning_effort (OpenAI/Venice), nested reasoning.effort (OpenRouter), or
                             flat + chat_template_kwargs.enable_thinking (vLLM).
      * WriteReasoningOff -- { param($Body) } writes the provider's reasoning OFF switch: reasoning_effort =
                             "none" (OpenAI/OpenRouter), venice_parameters.disable_thinking = true (Venice), or
                             reasoning_effort = "none" + enable_thinking = false (vLLM).
      * CeilingToken      -- the highest reasoning token the adapter emits for a NON-pinned effort
                             (MaxDialectReasoningEffort.ToWireValue()): "xhigh" everywhere except Venice, whose
                             adapter still claims "max" (unverified -- Test A measures it live).
      * SamplingForwarded -- whether the adapter forwards top_k/min_p (ApplySamplingExtensions is overridden):
                             true for OpenRouter/Venice/vLLM, false for the base OpenAI dialect.
      * WriteVendorParams -- { param($Body) } writes the adapter's FORCED vendor parameters, or $null when the
                             adapter has none. Only Venice (include_venice_system_prompt = false).
      * ApplicableTests   -- the test ids meaningful for this dialect; tests outside the set are skipped with a
                             clear note (e.g. F needs sampling forwarding, G is Venice-only).
      * Discovery         -- a catalog descriptor { RequiresKey, HasCapabilityMetadata, Project } consumed by
                             -ListModels. RequiresKey gates the Authorization header on GET /models (OpenRouter's
                             catalog is public, vLLM is keyless, Venice/OpenAI need the key); HasCapabilityMetadata
                             says whether entries carry capability flags at all (false for the minimal OpenAI/vLLM
                             schema); Project maps a raw entry onto the neutral { Id, Tools, Reasoning, Efforts }
                             shape, where $null means "the catalog does not say".
.PARAMETER Provider
    The provider type whose dialect is returned: openrouter, venice, openai, or vllm.
.OUTPUTS
    A [pscustomobject] dialect descriptor with the fields documented above.
#>
function Get-Dialect {
    param([Parameter(Mandatory)][ValidateSet('openrouter', 'venice', 'openai', 'vllm')][string] $Provider)

    switch ($Provider) {
        'openrouter' {
            return [pscustomobject]@{
                Name              = 'openrouter'
                BaseUrl           = 'https://openrouter.ai/api/v1'
                ApiKeyEnvVar      = 'OPENROUTER_API_KEY'
                # A reasoning- AND tool-capable default; override with -Model (use -ListModels to find candidates).
                # Measured 2026 as covering A,E,F,H,I,J,L live: it is the rare slug advertising BOTH the "xhigh"
                # ceiling and the "none" off-switch, and is tool- + vision-capable. It emits no reasoning_details,
                # so B/C still need a Claude/Gemini "thinking" model. OpenRouter's catalog is volatile -- if this
                # slug 404s ("No endpoints found"), pick a live one with -ListModels.
                DefaultModel      = 'openai/gpt-5.2'
                # Nested unified object: payload["reasoning"]["effort"] = token (OpenRouterProvider.ApplyReasoning()).
                WriteReasoning    = {
                    param($Body, $Token)
                    if (-not $Body.ContainsKey('reasoning')) { $Body['reasoning'] = @{} }
                    $Body['reasoning']['effort'] = $Token
                }
                # Off is the same nested field carrying the "none" token.
                WriteReasoningOff = {
                    param($Body)
                    if (-not $Body.ContainsKey('reasoning')) { $Body['reasoning'] = @{} }
                    $Body['reasoning']['effort'] = 'none'
                }
                # Inherited XHigh ceiling, measured 2026: OpenRouter's gateway rejects "max" with HTTP 400.
                CeilingToken      = 'xhigh'
                SamplingForwarded = $true
                WriteVendorParams = $null
                ApplicableTests   = @('A', 'B', 'C', 'D', 'E', 'F', 'H', 'I', 'J', 'K', 'L')
                # Public catalog (no key); rich metadata: supported_parameters + reasoning.supported_efforts.
                Discovery         = [pscustomobject]@{
                    RequiresKey           = $false
                    HasCapabilityMetadata = $true
                    Project               = {
                        param($Entry)
                        $params = Get-Prop $Entry 'supported_parameters'
                        [pscustomobject]@{
                            Id        = [string] (Get-Prop $Entry 'id')
                            Tools     = [bool]($params -and ($params -contains 'tools'))
                            Reasoning = [bool]($params -and ($params -contains 'reasoning'))
                            Efforts   = Get-Prop (Get-Prop $Entry 'reasoning') 'supported_efforts'
                        }
                    }
                }
            }
        }
        'venice' {
            return [pscustomobject]@{
                Name              = 'venice'
                BaseUrl           = 'https://api.venice.ai/api/v1'
                ApiKeyEnvVar      = 'VENICE_API_KEY'
                # A reasoning-capable Venice default; override with -Model to match your account's catalog.
                DefaultModel      = 'qwen3-235b'
                # Flat OpenAI field for positive efforts (VeniceProvider.ApplyReasoning(), non-None branch).
                WriteReasoning    = {
                    param($Body, $Token)
                    $Body['reasoning_effort'] = $Token
                }
                # Off is the Venice vendor switch, NOT reasoning_effort = "none".
                WriteReasoningOff = {
                    param($Body)
                    if (-not $Body.ContainsKey('venice_parameters')) { $Body['venice_parameters'] = @{} }
                    $Body['venice_parameters']['disable_thinking'] = $true
                }
                # Unverified ceiling -- Test A measures whether Venice actually accepts "max".
                CeilingToken      = 'max'
                SamplingForwarded = $true
                # Forced on every chat request, overwriting any client value (VeniceProvider.ApplyVendorParameters()).
                WriteVendorParams = {
                    param($Body)
                    if (-not $Body.ContainsKey('venice_parameters')) { $Body['venice_parameters'] = @{} }
                    $Body['venice_parameters']['include_venice_system_prompt'] = $false
                }
                ApplicableTests   = @('A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J', 'K', 'L')
                # Needs the key; capability flags live in model_spec.capabilities, but the catalog declares no
                # reasoning-effort enum, so Reasoning/Efforts stay unknown ($null) and the "max" view is unavailable.
                Discovery         = [pscustomobject]@{
                    RequiresKey           = $true
                    HasCapabilityMetadata = $true
                    Project               = {
                        param($Entry)
                        $caps = Get-Prop (Get-Prop $Entry 'model_spec') 'capabilities'
                        $tools = if ($caps) { [bool] (Get-Prop $caps 'supportsFunctionCalling') } else { $null }
                        [pscustomobject]@{
                            Id        = [string] (Get-Prop $Entry 'id')
                            Tools     = $tools
                            Reasoning = $null
                            Efforts   = $null
                        }
                    }
                }
            }
        }
        'vllm' {
            return [pscustomobject]@{
                Name              = 'vllm'
                BaseUrl           = ''   # Self-hosted: no canonical URL, so -BaseUrl is required.
                ApiKeyEnvVar      = ''   # Typically keyless: no Authorization header is sent.
                DefaultModel      = ''   # Self-hosted catalog is deployment-specific, so -Model is required.
                # Dual emission: flat token for modern vLLM + the explicit kwarg for older builds/templates
                # (VllmProvider.ApplyReasoning()).
                WriteReasoning    = {
                    param($Body, $Token)
                    $Body['reasoning_effort'] = $Token
                    if (-not $Body.ContainsKey('chat_template_kwargs')) { $Body['chat_template_kwargs'] = @{} }
                    $Body['chat_template_kwargs']['enable_thinking'] = $true
                }
                WriteReasoningOff = {
                    param($Body)
                    $Body['reasoning_effort'] = 'none'
                    if (-not $Body.ContainsKey('chat_template_kwargs')) { $Body['chat_template_kwargs'] = @{} }
                    $Body['chat_template_kwargs']['enable_thinking'] = $false
                }
                CeilingToken      = 'xhigh'
                SamplingForwarded = $true
                WriteVendorParams = $null
                ApplicableTests   = @('A', 'B', 'C', 'D', 'E', 'F', 'H', 'I', 'J', 'K', 'L')
                # Keyless self-hosted; the OpenAI-minimal catalog (id/created/owned_by) carries no capability flags.
                Discovery         = [pscustomobject]@{
                    RequiresKey           = $false
                    HasCapabilityMetadata = $false
                    Project               = {
                        param($Entry)
                        [pscustomobject]@{
                            Id        = [string] (Get-Prop $Entry 'id')
                            Tools     = $null
                            Reasoning = $null
                            Efforts   = $null
                        }
                    }
                }
            }
        }
        'openai' {
            return [pscustomobject]@{
                Name              = 'openai'
                BaseUrl           = 'https://api.openai.com/v1'
                ApiKeyEnvVar      = 'OPENAI_API_KEY'
                DefaultModel      = ''   # OpenAI model ids change often, so -Model is required to avoid a stale default.
                # Base dialect: the flat OpenAI reasoning_effort field (OpenAiCompatibleProvider.ApplyReasoning()).
                WriteReasoning    = {
                    param($Body, $Token)
                    $Body['reasoning_effort'] = $Token
                }
                WriteReasoningOff = {
                    param($Body)
                    $Body['reasoning_effort'] = 'none'
                }
                CeilingToken      = 'xhigh'
                # Base ApplySamplingExtensions is a no-op, so top_k/min_p are NOT forwarded for plain OpenAI.
                SamplingForwarded = $false
                WriteVendorParams = $null
                ApplicableTests   = @('A', 'B', 'C', 'D', 'E', 'H', 'I', 'J', 'K', 'L')
                # Needs the key; the official catalog is minimal (id/created/owned_by) with no capability flags.
                Discovery         = [pscustomobject]@{
                    RequiresKey           = $true
                    HasCapabilityMetadata = $false
                    Project               = {
                        param($Entry)
                        [pscustomobject]@{
                            Id        = [string] (Get-Prop $Entry 'id')
                            Tools     = $null
                            Reasoning = $null
                            Efforts   = $null
                        }
                    }
                }
            }
        }
        # ValidateSet on $Provider makes any other value unreachable; throw rather than return a silent $null.
        default { throw "Unknown provider '$Provider'." }
    }
}

#endregion Provider dialects

#region Shared helpers

<#
.SYNOPSIS
    Prints a section banner so the tests are visually separable in the transcript.
#>
function Write-Section {
    param([string] $Title)

    Write-Host ''
    Write-Host ('=' * 100) -ForegroundColor DarkCyan
    Write-Host $Title -ForegroundColor Cyan
    Write-Host ('=' * 100) -ForegroundColor DarkCyan
}

<#
.SYNOPSIS
    Records and prints a colored verdict line for a test, and stores it for the final summary.
#>
function Write-Verdict {
    param(
        [Parameter(Mandatory)][string] $Test,
        [Parameter(Mandatory)][ValidateSet('PASS', 'FAIL', 'INCONCLUSIVE')][string] $Verdict,
        [Parameter(Mandatory)][string] $Detail
    )

    $color = switch ($Verdict) {
        'PASS' { 'Green' }
        'FAIL' { 'Red' }
        default { 'Yellow' }
    }

    Write-Host ('  [{0}] {1,-13}' -f $Test, $Verdict) -ForegroundColor $color -NoNewline
    Write-Host (' {0}' -f $Detail)

    $script:Findings[$Test] = [pscustomobject]@{ Verdict = $Verdict; Detail = $Detail }
}

<#
.SYNOPSIS
    Reads the active provider's API key from its configured environment variable, or returns $null for a
    keyless self-hosted provider whose dialect declares no env var.
.DESCRIPTION
    The variable name comes from the active dialect (OPENROUTER_API_KEY, VENICE_API_KEY, OPENAI_API_KEY); a
    dialect with an empty ApiKeyEnvVar (vLLM) is keyless, so this returns $null and the transport helpers send
    no Authorization header. A key-requiring provider whose variable is unset throws a clear, provider-specific
    error so the operator knows exactly which secret to set. The key is never accepted as an argument, matching
    the repository's secret-handling discipline.
#>
function Get-ApiKey {
    param([string] $EnvVarName = $script:Dialect.ApiKeyEnvVar)

    # A keyless provider (self-hosted vLLM) declares no env var; the caller then sends no Authorization header.
    if ([string]::IsNullOrWhiteSpace($EnvVarName)) { return $null }

    $key = [System.Environment]::GetEnvironmentVariable($EnvVarName)
    if ([string]::IsNullOrWhiteSpace($key)) {
        throw "$EnvVarName is not set. Set it in the environment before running live tests for provider '$($script:Dialect.Name)'."
    }

    return $key
}

<#
.SYNOPSIS
    Sends one non-streaming POST to the backend and returns its status and raw body, WITHOUT throwing on a
    non-2xx response (so a 400 capability rejection is observable rather than an exception).
.DESCRIPTION
    Uses System.Net.Http.HttpClient directly rather than Invoke-WebRequest -SkipHttpErrorCheck, because the
    latter switch only exists on PowerShell 7+. The raw .NET client behaves identically on 5.1 and 7+.
#>
function Invoke-Backend {
    param(
        [string] $ApiKey,
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][hashtable] $Body
    )

    $json = $Body | ConvertTo-Json -Depth 100 -Compress
    if ($ShowBodies) { Write-Host "    -> POST $Path`n$json" -ForegroundColor DarkGray }

    $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromMinutes(2)
    try {
        $uri = ($BaseUrl.TrimEnd('/')) + $Path
        $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, $uri)
        # A keyless provider (self-hosted vLLM) sends no Authorization header.
        if (-not [string]::IsNullOrWhiteSpace($ApiKey)) {
            $request.Headers.Authorization =
                [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $ApiKey)
        }
        $request.Content =
            [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, 'application/json')

        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        $content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if ($ShowBodies) { Write-Host "    <- $([int]$response.StatusCode)`n$content" -ForegroundColor DarkGray }

        return [pscustomobject]@{
            Status = [int] $response.StatusCode
            Body   = $content
        }
    }
    finally {
        $client.Dispose()
    }
}

<#
.SYNOPSIS
    Sends one streaming (SSE) POST and returns the status plus the ordered list of raw "data:" payloads
    (the "[DONE]" sentinel and blank lines removed), WITHOUT throwing on a non-2xx response.
#>
function Invoke-BackendStream {
    param(
        [string] $ApiKey,
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][hashtable] $Body
    )

    $json = $Body | ConvertTo-Json -Depth 100 -Compress
    if ($ShowBodies) { Write-Host "    -> POST $Path (stream)`n$json" -ForegroundColor DarkGray }

    $payloads = [System.Collections.Generic.List[string]]::new()
    $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromMinutes(2)
    try {
        $uri = ($BaseUrl.TrimEnd('/')) + $Path
        $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, $uri)
        # A keyless provider (self-hosted vLLM) sends no Authorization header.
        if (-not [string]::IsNullOrWhiteSpace($ApiKey)) {
            $request.Headers.Authorization =
                [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $ApiKey)
        }
        $request.Headers.Accept.Add(
            [System.Net.Http.Headers.MediaTypeWithQualityHeaderValue]::new('text/event-stream'))
        $request.Content =
            [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, 'application/json')

        # ResponseHeadersRead lets us read the SSE stream incrementally instead of buffering the whole body.
        $response = $client.SendAsync(
            $request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        $status = [int] $response.StatusCode

        if ($status -lt 200 -or $status -ge 300) {
            # Error responses are not SSE; surface the body as a single payload for the caller to inspect.
            $errorBody = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            $payloads.Add($errorBody)
            return [pscustomobject]@{ Status = $status; Payloads = $payloads }
        }

        $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $reader = [System.IO.StreamReader]::new($stream)
        try {
            while (-not $reader.EndOfStream) {
                $line = $reader.ReadLine()
                if ([string]::IsNullOrWhiteSpace($line)) { continue }
                if (-not $line.StartsWith('data:')) { continue }

                $data = $line.Substring(5).Trim()
                if ($data -eq '[DONE]') { break }

                $payloads.Add($data)
            }
        }
        finally {
            $reader.Dispose()
        }

        return [pscustomobject]@{ Status = $status; Payloads = $payloads }
    }
    finally {
        $client.Dispose()
    }
}

<#
.SYNOPSIS
    Produces the canonical form of a parsed JSON node, mirroring ReasoningDetailsCorrelation.WriteCanonical:
    object members in ordinal key order, array order preserved, scalars in their canonical JSON text.
.DESCRIPTION
    This is the exact transformation whose stability across a client round-trip Test D measures. Replicating it
    here (rather than calling into the proxy) keeps the measurement independent of the code under test.
#>
function Get-CanonicalForm {
    param($Node)

    if ($null -eq $Node) { return 'null' }

    # Objects from ConvertFrom-Json are PSCustomObject; hashtables are supported too for synthetic inputs.
    if ($Node -is [System.Management.Automation.PSCustomObject] -or $Node -is [hashtable]) {
        if ($Node -is [hashtable]) {
            $pairs = $Node.GetEnumerator() | ForEach-Object { [pscustomobject]@{ Key = [string]$_.Key; Value = $_.Value } }
        }
        else {
            $pairs = $Node.PSObject.Properties | ForEach-Object { [pscustomobject]@{ Key = $_.Name; Value = $_.Value } }
        }

        $keys = @($pairs | ForEach-Object { $_.Key })
        # Ordinal sort to match StringComparer.Ordinal in the proxy; .NET sorts in place by char code.
        [Array]::Sort($keys, [System.StringComparer]::Ordinal)

        $members = foreach ($key in $keys) {
            $value = ($pairs | Where-Object { $_.Key -eq $key } | Select-Object -First 1).Value
            ($key | ConvertTo-Json -Compress) + ':' + (Get-CanonicalForm -Node $value)
        }

        return '{' + ($members -join ',') + '}'
    }

    # Arrays preserve their order (arrays are ordered); strings are NOT treated as arrays.
    if ($Node -is [System.Collections.IList]) {
        $items = foreach ($item in $Node) { Get-CanonicalForm -Node $item }
        return '[' + ($items -join ',') + ']'
    }

    # Scalar (string, number, bool): its own compact JSON serialization is already canonical and preserves type.
    return ($Node | ConvertTo-Json -Compress)
}

<#
.SYNOPSIS
    Parses one SSE data payload as JSON, returning $null when it is not valid JSON (best-effort, like the proxy).
#>
function ConvertFrom-SsePayload {
    param([Parameter(Mandatory)][string] $Payload)

    try {
        return $Payload | ConvertFrom-Json
    }
    catch {
        return $null
    }
}

<#
.SYNOPSIS
    Computes the reasoning-details correlation key for a set of tool calls, mirroring
    ReasoningDetailsCorrelation.TryComputeKey byte-for-byte (format version, separators, ordinal fragment sort,
    backend-name scoping, SHA-256 hex). Used by Test D to measure key stability across a client round-trip.
.DESCRIPTION
    Each tool call's arguments may be the OpenAI wire form (a JSON STRING) or an already-parsed object; the proxy
    parses the string (ParseArgumentsOrEmpty) before canonicalizing, so a string is parsed here too, with an
    empty object as the fallback for null/blank/invalid -- matching the proxy.
#>
function Get-CorrelationKey {
    param(
        [Parameter(Mandatory)][string] $BackendName,
        [Parameter(Mandatory)] $ToolCalls
    )

    $formatVersion = 'rd-v1'
    $fieldSeparator = [char] 0x0000
    $callSeparator = [char] 0x0001

    $fragments = [System.Collections.Generic.List[string]]::new()
    foreach ($call in $ToolCalls) {
        $func = Get-Prop $call 'function'
        $name = [string] (Get-Prop $func 'name')
        $arguments = Get-Prop $func 'arguments'

        # OpenAI wire shape carries arguments as a JSON string; the proxy parses it before canonicalizing.
        $argsNode = $null
        if ($arguments -is [string]) {
            if ([string]::IsNullOrWhiteSpace($arguments)) { $argsNode = [pscustomobject]@{} }
            else {
                try { $argsNode = $arguments | ConvertFrom-Json } catch { $argsNode = [pscustomobject]@{} }
            }
        }
        else {
            $argsNode = $arguments
        }

        $canonicalArgs = Get-CanonicalForm -Node $argsNode
        $fragments.Add("$name$fieldSeparator$canonicalArgs")
    }

    # Sort fragments ordinally so the order of parallel tool calls cannot change the key.
    $sorted = @($fragments)
    [Array]::Sort($sorted, [System.StringComparer]::Ordinal)

    # Backend name as an escaped JSON string, like JsonValue.Create(name).ToJsonString().
    $scope = $BackendName | ConvertTo-Json -Compress
    $canonical = "$formatVersion$callSeparator$scope$callSeparator" + ($sorted -join $callSeparator)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($canonical))
    }
    finally {
        $sha.Dispose()
    }

    return [System.BitConverter]::ToString($hash).Replace('-', '')
}

<#
.SYNOPSIS
    Safely reads a (possibly missing) property from a PSCustomObject or a key from a hashtable without throwing
    under StrictMode.
.DESCRIPTION
    ConvertFrom-Json yields PSCustomObject (read via PSObject.Properties), while the script's synthetic test data
    uses @{} hashtable literals (an IDictionary, whose keys are NOT exposed through PSObject.Properties and must
    be read by indexing). Handling both shapes keeps callers agnostic to how the object was constructed.
#>
function Get-Prop {
    param($Object, [Parameter(Mandatory)][string] $Name)

    if ($null -eq $Object) { return $null }

    # Hashtables (and other dictionaries) expose values by key, not via PSObject.Properties.
    if ($Object -is [System.Collections.IDictionary]) {
        if ($Object.Contains($Name)) { return $Object[$Name] }
        return $null
    }

    $prop = $Object.PSObject.Properties[$Name]
    if ($null -eq $prop) { return $null }
    return $prop.Value
}

<#
.SYNOPSIS
    Collapses a response body to a single short line for inclusion in a verdict, so the evidence stays readable
    without dumping a full body (use -ShowBodies for the complete payloads).
#>
function Get-BodySnippet {
    param([string] $Body, [int] $MaxLength = 240)

    if ([string]::IsNullOrWhiteSpace($Body)) { return '<empty>' }

    $flat = ($Body -replace '\s+', ' ').Trim()
    if ($flat.Length -le $MaxLength) { return $flat }
    return $flat.Substring(0, $MaxLength) + ' ...'
}

<#
.SYNOPSIS
    Classifies a capability-probe HTTP status exactly as OpenAiCapabilityProber.ProbeOnceAsync does, so the
    capability tests (H-L) read each backend response with the SAME policy the production prober uses.
.DESCRIPTION
    The prober interprets a probe's status without inspecting the body (except to surface a rejection):

      * 2xx                         -> 'Present'    (the capability-specific field was accepted)
      * 401 / 403 / 404             -> 'Permanent'  (auth/routing; inconclusive, not retried)
      * 429 / 5xx                   -> 'Transient'  (throttle/server error; inconclusive, retried)
      * any other 4xx (400/422/...) -> 'Absent'     (the request body was rejected -> capability unsupported)

    Returning the same four buckets keeps the live verdict faithful to the prober's contract rather than
    inventing a second interpretation. The caller decides what each bucket MEANS for the assumption it measures
    (e.g. for vision, 'Absent' on a vision-capable model is evidence the shipped placeholder image regressed).
.PARAMETER Status
    The HTTP status code returned by the probe.
.OUTPUTS
    One of the strings 'Present', 'Absent', 'Permanent', or 'Transient'.
#>
function Get-ProbeClassification {
    param([Parameter(Mandatory)][int] $Status)

    if ($Status -ge 200 -and $Status -lt 300) { return 'Present' }
    if ($Status -eq 401 -or $Status -eq 403 -or $Status -eq 404) { return 'Permanent' }
    if ($Status -eq 429 -or $Status -ge 500) { return 'Transient' }
    return 'Absent'
}

<#
.SYNOPSIS
    Renders a capability-probe verdict for a "positive path" test (H/I/K): the active default model is expected to
    HAVE the capability, so a 2xx is the PASS and every other bucket is reported faithfully (Absent/Permanent/
    Transient) with the prober's interpretation, since none of them can be a clean PASS.
.DESCRIPTION
    Centralizes the H/I/K verdict shape so each test only supplies its capability label and the rejection body.
    Vision (J) and streaming-timing (L) have bespoke verdicts and do NOT use this helper -- J must call out the
    "shipped image regressed vs. model lacks vision" ambiguity, and L is a timing measurement, not a status read.
.PARAMETER Test
    The single-letter test id (e.g. 'H').
.PARAMETER Capability
    A short human label for the probed capability (e.g. 'completion').
.PARAMETER Status
    The probe's HTTP status code.
.PARAMETER Body
    The probe's response body, surfaced (snipped) when the capability is read as absent.
#>
function Write-PositiveCapabilityVerdict {
    param(
        [Parameter(Mandatory)][string] $Test,
        [Parameter(Mandatory)][string] $Capability,
        [Parameter(Mandatory)][int] $Status,
        [string] $Body
    )

    switch (Get-ProbeClassification -Status $Status) {
        'Present' {
            Write-Verdict -Test $Test -Verdict 'PASS' `
                -Detail ("Backend accepted the {0} probe payload (HTTP {1}). The prober would correctly read this model as {0}-capable." `
                    -f $Capability, $Status)
        }
        'Absent' {
            Write-Verdict -Test $Test -Verdict 'INCONCLUSIVE' `
                -Detail ("Backend rejected the {0} probe with a body-rejecting HTTP {1}; the prober would read this as `"{0} unsupported`". Run against a model that HAS {0} to verify the positive path. Upstream said: {2}" `
                    -f $Capability, $Status, (Get-BodySnippet -Body $Body))
        }
        'Permanent' {
            Write-Verdict -Test $Test -Verdict 'INCONCLUSIVE' `
                -Detail ("{0} probe returned a permanent HTTP {1} (auth/routing); the prober treats this as inconclusive. Check the key, base URL, and model id." `
                    -f $Capability, $Status)
        }
        'Transient' {
            Write-Verdict -Test $Test -Verdict 'INCONCLUSIVE' `
                -Detail ("{0} probe returned a transient HTTP {1} (throttle/server error); the prober would retry. Re-run when the backend is healthy." `
                    -f $Capability, $Status)
        }
    }
}

#endregion Shared helpers

#region Discovery

<#
.SYNOPSIS
	Discovery mode -- lists the active backend's models, projected through the dialect's catalog descriptor, and
	(when the catalog exposes it) flags the tool/reasoning candidates and whether any model advertises "max".
.DESCRIPTION
	Queries GET /models and routes every entry through $script:Dialect.Discovery.Project, which normalizes the
	provider-specific catalog shape (OpenRouter's flat supported_parameters + reasoning.supported_efforts, Venice's
	nested model_spec.capabilities, the OpenAI/vLLM minimal id-only schema) onto a neutral
	{ Id, Tools, Reasoning, Efforts } record. The Authorization header is sent only when the dialect's catalog
	requires it (OpenRouter is public; vLLM is keyless; Venice/OpenAI need the key).

	When the dialect reports no capability metadata (HasCapabilityMetadata = $false), it simply lists the model
	ids -- the catalog cannot answer "tool + reasoning capable", so the human picks a slug and lets the live tests
	measure it. When metadata is present, it keeps tool+reasoning candidates and, if the catalog also declares a
	reasoning-effort enum, reports whether any model lists "max" as catalog-level evidence for the ceiling claim.
#>
function Invoke-ListModels {
	Write-Section ("Discovery -- models for the '{0}' dialect" -f $script:Dialect.Name)

	$discovery = $script:Dialect.Discovery

	$uri = ($BaseUrl.TrimEnd('/')) + '/models'
	Write-Host ("  GET {0}" -f $uri) -ForegroundColor Gray

	# Only attach a key when the dialect's catalog requires one; a public (OpenRouter) or keyless (vLLM) catalog
	# is queried anonymously.
	$headers = @{}
	if ($discovery.RequiresKey) {
		$key = Get-ApiKey
		if (-not [string]::IsNullOrWhiteSpace($key)) { $headers['Authorization'] = "Bearer $key" }
	}

	try {
		$response = Invoke-WebRequest -Uri $uri -Headers $headers -UseBasicParsing
	}
	catch {
		Write-Host ("  Failed to query {0}: {1}" -f $uri, $_.Exception.Message) -ForegroundColor Red
		return
	}

	$models = Get-Prop ($response.Content | ConvertFrom-Json) 'data'
	if ($null -eq $models -or $models.Count -eq 0) {
		Write-Host '  No models returned.' -ForegroundColor Yellow
		return
	}

	# Normalize every entry onto the neutral shape via the dialect projection, then sort by id for stable output.
	$projected =
		$models |
		ForEach-Object { & $discovery.Project $_ } |
		Sort-Object -Property Id

	# Minimal-catalog dialects (OpenAI, vLLM) cannot answer "tool + reasoning capable" -- they expose only ids.
	# List the ids and let the live tests decide; do not pretend to filter on metadata that is not there.
	if (-not $discovery.HasCapabilityMetadata) {
		Write-Host ("  {0} model(s) (this catalog exposes ids only, no capability metadata):" -f @($projected).Count) -ForegroundColor Gray
		foreach ($entry in $projected) {
			Write-Host ("    {0}" -f $entry.Id) -ForegroundColor DarkGray
		}

		$example = @($projected | Select-Object -First 1 | ForEach-Object { $_.Id })
		if ($example) {
			Write-Host ''
			Write-Host '  Pick a slug above and pass it via -Model to run the tests, e.g.:' -ForegroundColor DarkCyan
			Write-Host ('    ./tools/reasoning-live-probe.ps1 -All -Provider {0} -Model ''{1}''' -f $script:Dialect.Name, $example) -ForegroundColor DarkCyan
		}
		return
	}

	# Keep only models the catalog marks as BOTH tool- and reasoning-capable. A $null flag means "the catalog does
	# not say"; treat that as not-a-candidate here (Venice reports tools but never reasoning, so it lands below).
	$candidates = @($projected | Where-Object { $_.Tools -eq $true -and $_.Reasoning -eq $true })

	if ($candidates.Count -eq 0) {
		# Venice's catalog advertises function calling but no reasoning flag, so the tool+reasoning filter is empty
		# even though reasoning works on the wire. Fall back to listing tool-capable models so the hint still helps.
		$toolCapable = @($projected | Where-Object { $_.Tools -eq $true })
		if ($toolCapable.Count -gt 0) {
			Write-Host ("  No model declares BOTH tools and reasoning, but {0} declare tools (reasoning is not a catalog flag here):" `
					-f $toolCapable.Count) -ForegroundColor Yellow
			foreach ($entry in ($toolCapable | Select-Object -First 10)) {
				Write-Host ("    {0}" -f $entry.Id) -ForegroundColor DarkGray
			}
			Write-Host ''
			Write-Host '  Reasoning is exercised live; pick a tool-capable slug above and pass it via -Model.' -ForegroundColor DarkCyan
			return
		}

		Write-Host '  No tool+reasoning capable models found in the catalog.' -ForegroundColor Yellow
		return
	}

	# Split candidates by whether they declare a supported_efforts enum. Models that declare it are the
	# actionable ones for Test A (the max-effort probe); models that only advertise reasoning without an effort
	# enum are still valid for tests B/C (the reasoning_details round-trip). A leading "~" marks a special
	# OpenRouter variant slug, not a plain id -- excluded from the suggested example so the hint is copy-pasteable.
	$declaring = [System.Collections.Generic.List[object]]::new()
	$undeclared = [System.Collections.Generic.List[string]]::new()
	$maxCapable = [System.Collections.Generic.List[string]]::new()

	foreach ($model in $candidates) {
		if ($model.Efforts) {
			$declaring.Add([pscustomobject]@{ Id = $model.Id; Efforts = $model.Efforts })
			if ($model.Efforts -contains 'max') { $maxCapable.Add($model.Id) }
		}
		else {
			$undeclared.Add($model.Id)
		}
	}

	Write-Host ("  {0} tool+reasoning model(s): {1} declare an effort enum, {2} do not." `
			-f $candidates.Count, $declaring.Count, $undeclared.Count) -ForegroundColor Gray

	if ($declaring.Count -gt 0) {
		Write-Host ''
		Write-Host '  Models declaring a reasoning-effort enum (best for Test A):' -ForegroundColor Gray
		foreach ($entry in $declaring) {
			$hasMax = $entry.Efforts -contains 'max'
			$marker = if ($hasMax) { ' <- advertises "max"' } else { '' }
			$color = if ($hasMax) { 'Green' } else { 'Gray' }
			Write-Host ("    {0,-45} efforts: [{1}]{2}" -f $entry.Id, ($entry.Efforts -join ', '), $marker) -ForegroundColor $color
		}
	}

	if ($undeclared.Count -gt 0) {
		Write-Host ''
		Write-Host ("  {0} model(s) advertise reasoning without an effort enum (valid for tests B/C), e.g.:" -f $undeclared.Count) -ForegroundColor Gray
		foreach ($id in ($undeclared | Select-Object -First 5)) {
			Write-Host ("    {0}" -f $id) -ForegroundColor DarkGray
		}
	}

	Write-Host ''
	if ($maxCapable.Count -gt 0) {
		Write-Host ("  Assumption #4 (catalog view): {0} model(s) advertise reasoning effort `"max`"; e.g. {1}." `
				-f $maxCapable.Count, (($maxCapable | Select-Object -First 3) -join ', ')) -ForegroundColor Green
	}
	else {
		Write-Host '  Assumption #4 (catalog view): NO model advertises reasoning effort "max" in supported_efforts.' -ForegroundColor Yellow
		Write-Host '  That is catalog evidence the dialect ceiling of "max" exceeds what models declare; confirm with Test A.' -ForegroundColor Yellow
	}

	# Suggest a concrete, copy-pasteable example: prefer a clean (non-"~") slug that declares efforts, then any
	# clean slug, so the hint always works even if every effort-declaring model is a special variant.
	$cleanDeclaring = @($declaring | Where-Object { -not $_.Id.StartsWith('~') } | Select-Object -First 1)
	$example =
		if ($cleanDeclaring.Count -gt 0) { $cleanDeclaring[0].Id }
		else { @($candidates | Where-Object { -not $_.Id.StartsWith('~') } | Select-Object -First 1 | ForEach-Object { $_.Id }) }

	if ($example) {
		Write-Host ''
		Write-Host '  Pick a slug above and pass it via -Model to run tests A/B/C, e.g.:' -ForegroundColor DarkCyan
		Write-Host ('    ./tools/reasoning-live-probe.ps1 -All -Provider {0} -Model ''{1}''' -f $script:Dialect.Name, $example) -ForegroundColor DarkCyan
	}
}

#endregion Discovery

#region Tests

<#
.SYNOPSIS
    Test A -- measures whether the active backend accepts the dialect's CEILING reasoning effort (the highest
    token the adapter emits for a non-pinned request: "xhigh" for OpenRouter/OpenAI/vLLM, "max" for Venice).
.DESCRIPTION
    Sends the reasoning directive exactly as the active dialect's adapter writes it (nested reasoning.effort for
    OpenRouter, flat reasoning_effort for Venice/OpenAI, flat + chat_template_kwargs.enable_thinking for vLLM).
    A bare ceiling probe is ambiguous on its own: a 400 could mean "the ceiling token is unknown" OR "this
    request is malformed for an unrelated reason". So a CONTROL request with effort = "high" (a documented value
    every dialect accepts) is sent first. The verdict is only meaningful when the control is accepted:
      control 200 + ceiling 200 -> PASS          (the ceiling token is accepted; the dialect ceiling holds)
      control 200 + ceiling 400 -> FAIL          (the ceiling token is rejected; the dialect ceiling over-sends)
      control non-200           -> INCONCLUSIVE  (something unrelated is wrong; the ceiling result says nothing)

    When the ceiling sits BELOW "max" (every dialect except Venice), a NEGATIVE CONTROL then probes "max",
    expecting it to be rejected -- that rejection is the very reason the ceiling is capped at "xhigh" instead of
    "max". If "max" is accepted too, the cap is conservative rather than forced, and that is reported as a note.
#>
function Test-A-CeilingEffort {
    param([string] $ApiKey)

    $ceiling = $script:Dialect.CeilingToken
    Write-Section ("Test A -- '{0}' accepts the dialect ceiling reasoning effort `"{1}`" (assumption #4)" `
            -f $script:Dialect.Name, $ceiling)

    # Minimal chat turn; the reasoning directive -- written in the ACTIVE dialect's wire form -- is what is under
    # test, not the content.
    function New-EffortBody([string] $effort) {
        $body = @{
            model      = $Model
            max_tokens = $MaxOutputTokens
            messages   = @(@{ role = 'user'; content = 'Reply with the single word: ok.' })
        }
        # The dialect decides the wire shape: nested reasoning.effort (OpenRouter), flat reasoning_effort
        # (Venice/OpenAI), or flat + chat_template_kwargs.enable_thinking (vLLM).
        & $script:Dialect.WriteReasoning $body $effort
        return $body
    }

    Write-Host '  Control: reasoning effort = "high" (documented value)...' -ForegroundColor Gray
    $control = Invoke-Backend -ApiKey $ApiKey -Path '/chat/completions' -Body (New-EffortBody 'high')
    Write-Host ("    control status: {0}" -f $control.Status) -ForegroundColor Gray

    if ($control.Status -ne 200) {
        $snippet = Get-BodySnippet -Body $control.Body
        Write-Verdict -Test 'A' -Verdict 'INCONCLUSIVE' `
            -Detail ("Control (effort=high) returned {0}, not 200 -- the ceiling result is not interpretable. Body: {1}" `
                -f $control.Status, $snippet)
        return
    }

    Write-Host ("  Probe: reasoning effort = `"{0}`" (the dialect ceiling)..." -f $ceiling) -ForegroundColor Gray
    $probe = Invoke-Backend -ApiKey $ApiKey -Path '/chat/completions' -Body (New-EffortBody $ceiling)
    Write-Host ("    probe status:   {0}" -f $probe.Status) -ForegroundColor Gray

    if ($probe.Status -eq 200) {
        $detail = "Backend accepted reasoning effort `"$ceiling`" (HTTP 200). The dialect ceiling holds for this model."

        # Negative control: when the ceiling is BELOW "max", "max" should be rejected -- that rejection is the
        # reason the ceiling sits at "xhigh" rather than "max". Skipped when the ceiling already IS "max" (Venice),
        # since there is no token above it to probe. Run only after the ceiling passed, to avoid a wasted call.
        if ($ceiling -ne 'max') {
            Write-Host '  Negative control: reasoning effort = "max" (expected to be rejected)...' -ForegroundColor Gray
            $maxProbe = Invoke-Backend -ApiKey $ApiKey -Path '/chat/completions' -Body (New-EffortBody 'max')
            Write-Host ("    max status:     {0}" -f $maxProbe.Status) -ForegroundColor Gray

            if ($maxProbe.Status -eq 400) {
                $detail += ' Negative control confirms "max" is rejected (HTTP 400) -- the ceiling is correctly capped below "max".'
            }
            elseif ($maxProbe.Status -eq 200) {
                $detail += ' NOTE: "max" was ALSO accepted (HTTP 200) -- this model would tolerate a higher ceiling; the cap is conservative, not forced.'
            }
            else {
                $detail += (' Negative control for "max" was inconclusive (HTTP {0}).' -f $maxProbe.Status)
            }
        }

        Write-Verdict -Test 'A' -Verdict 'PASS' -Detail $detail
    }
    elseif ($probe.Status -eq 400) {
        $snippet = Get-BodySnippet -Body $probe.Body
        Write-Verdict -Test 'A' -Verdict 'FAIL' `
            -Detail ("Backend rejected the dialect ceiling `"{0}`" with HTTP 400 while accepting `"high`". The dialect ceiling over-sends. Body: {1}" `
                -f $ceiling, $snippet)
    }
    else {
        $snippet = Get-BodySnippet -Body $probe.Body
        Write-Verdict -Test 'A' -Verdict 'INCONCLUSIVE' `
            -Detail ("Unexpected status {0} for the ceiling probe (control was 200). Body: {1}" -f $probe.Status, $snippet)
    }
}

<#
.SYNOPSIS
    Test B -- measures whether reasoning_details arrives complete on a single streamed delta (vs. fragmented).
.DESCRIPTION
    Drives a forced tool-calling turn (tool_choice = required) with reasoning enabled, the scenario in which a
    reasoning backend emits the opaque reasoning_details blob. Runs it twice:

      1. Non-streaming -- confirms the backend emits reasoning_details at all and captures the blob + tool calls
         into $script:Capture so Test C can replay them. This is the clean capture path.

      2. Streaming -- counts how many deltas carry a non-null reasoning_details. The proxy
         (ObserveReasoningDetails) keeps only the LAST non-null blob, so the assumption holds iff either:
           - exactly one delta carries it, or
           - several deltas carry it but each is CUMULATIVE (element count non-decreasing, the last is the
             full array) -- the last is then still complete.
         A genuinely FRAGMENTED stream (pieces that must be concatenated) makes the proxy keep only the last
         fragment, which is the failure this test is built to catch.
#>
function Test-B-ReasoningDetailsShape {
    param([string] $ApiKey)

    Write-Section 'Test B -- reasoning_details arrives on a single delta (assumption #1)'

    $userMessage = @{ role = 'user'; content = 'What is the current weather in Berlin? Use the get_weather tool.' }
    $tools = @(
        @{
            type     = 'function'
            function = @{
                name        = 'get_weather'
                description = 'Get the current weather for a given city.'
                parameters  = @{
                    type       = 'object'
                    properties = @{ city = @{ type = 'string'; description = 'The city to look up.' } }
                    required   = @('city')
                }
            }
        }
    )

    function New-ToolCallBody([bool] $stream) {
        $body = @{
            model       = $Model
            max_tokens  = $MaxOutputTokens
            messages    = @($userMessage)
            tools       = $tools
            tool_choice = 'required'
        }
        # Enable reasoning in the ACTIVE dialect's wire form, exactly as the proxy adapter writes it.
        & $script:Dialect.WriteReasoning $body 'high'
        if ($stream) {
            $body['stream'] = $true
            $body['stream_options'] = @{ include_usage = $true }
        }
        return $body
    }

    # --- 1. Non-streaming: confirm emission and capture for Test C ---
    Write-Host '  Non-streaming tool-call turn...' -ForegroundColor Gray
    $resp = Invoke-Backend -ApiKey $ApiKey -Path '/chat/completions' -Body (New-ToolCallBody $false)
    Write-Host ("    status: {0}" -f $resp.Status) -ForegroundColor Gray

    if ($resp.Status -ne 200) {
        Write-Verdict -Test 'B' -Verdict 'INCONCLUSIVE' `
            -Detail ("Non-streaming tool-call turn returned {0}. Body: {1}" -f $resp.Status, (Get-BodySnippet -Body $resp.Body))
        return
    }

    $parsed = $resp.Body | ConvertFrom-Json
    $choices = Get-Prop $parsed 'choices'
    $message = if ($choices -and $choices.Count -gt 0) { Get-Prop $choices[0] 'message' } else { $null }
    $nonStreamRd = Get-Prop $message 'reasoning_details'
    $toolCalls = Get-Prop $message 'tool_calls'

    if ($null -eq $toolCalls -or $toolCalls.Count -eq 0) {
        Write-Verdict -Test 'B' -Verdict 'INCONCLUSIVE' `
            -Detail 'Backend produced no tool_calls, so there is no reasoning-details round-trip anchor to measure. Try a tool-capable model.'
        return
    }

    if ($null -ne $nonStreamRd) {
        # Stash the capture so Test C can replay it without re-calling the backend.
        $script:Capture = [pscustomobject]@{
            UserMessage      = $userMessage
            Tools            = $tools
            ToolCalls        = $toolCalls
            ReasoningDetails = $nonStreamRd
        }
        Write-Host '    reasoning_details present on the non-streaming message -> captured for Test C.' -ForegroundColor DarkGreen
    }
    else {
        Write-Host '    No reasoning_details on the non-streaming message (this model may not emit it).' -ForegroundColor DarkYellow
    }

    # --- 2. Streaming: count and shape the reasoning_details deltas ---
    Write-Host '  Streaming tool-call turn...' -ForegroundColor Gray
    $stream = Invoke-BackendStream -ApiKey $ApiKey -Path '/chat/completions' -Body (New-ToolCallBody $true)
    Write-Host ("    status: {0}, frames: {1}" -f $stream.Status, $stream.Payloads.Count) -ForegroundColor Gray

    if ($stream.Status -ne 200) {
        Write-Verdict -Test 'B' -Verdict 'INCONCLUSIVE' `
            -Detail ("Streaming tool-call turn returned {0}. Body: {1}" -f $stream.Status, (Get-BodySnippet -Body ($stream.Payloads -join ' ')))
        return
    }

    # Element count per delta that carries reasoning_details: array -> its length; non-array -> -1 (unknown shape).
    $elementCounts = [System.Collections.Generic.List[int]]::new()
    foreach ($payload in $stream.Payloads) {
        $chunk = ConvertFrom-SsePayload -Payload $payload
        if ($null -eq $chunk) { continue }

        $chunkChoices = Get-Prop $chunk 'choices'
        if ($null -eq $chunkChoices -or $chunkChoices.Count -eq 0) { continue }

        $delta = Get-Prop $chunkChoices[0] 'delta'
        $rd = Get-Prop $delta 'reasoning_details'
        if ($null -eq $rd) { continue }

        if ($rd -is [System.Collections.IList]) { $elementCounts.Add($rd.Count) }
        else { $elementCounts.Add(-1) }
    }

    $deltaCount = $elementCounts.Count
    Write-Host ("    deltas carrying reasoning_details: {0} (element counts: {1})" `
            -f $deltaCount, ($(if ($deltaCount -gt 0) { $elementCounts -join ', ' } else { 'none' }))) -ForegroundColor Gray

    if ($deltaCount -eq 0) {
        $note = if ($null -ne $nonStreamRd) {
            'Non-streaming emitted reasoning_details but the stream did not -- assumption #1 only concerns the stream, so it is not exercised here.'
        }
        else {
            'This model emits no reasoning_details in either mode; assumption #1 is not exercisable with it. Try a Claude/Gemini "thinking" model.'
        }
        Write-Verdict -Test 'B' -Verdict 'INCONCLUSIVE' -Detail $note
        return
    }

    if ($deltaCount -eq 1) {
        Write-Verdict -Test 'B' -Verdict 'PASS' `
            -Detail 'Exactly one delta carried reasoning_details -- the "last non-null blob is the complete one" assumption holds for this model.'
        return
    }

    # More than one delta carried it: cumulative (each replaces with the full-so-far) is still safe because the
    # last delta holds the complete array; genuinely fragmented pieces are not.
    $allArrays = ($elementCounts | Where-Object { $_ -lt 0 }).Count -eq 0
    $nonDecreasing = $true
    for ($i = 1; $i -lt $elementCounts.Count; $i++) {
        if ($elementCounts[$i] -lt $elementCounts[$i - 1]) { $nonDecreasing = $false; break }
    }

    if ($allArrays -and $nonDecreasing) {
        Write-Verdict -Test 'B' -Verdict 'PASS' `
            -Detail ("{0} deltas carried reasoning_details but the element count is non-decreasing (cumulative); the last delta holds the complete {1}-element array, so keeping the last is correct." `
                -f $deltaCount, $elementCounts[$elementCounts.Count - 1])
    }
    else {
        Write-Verdict -Test 'B' -Verdict 'FAIL' `
            -Detail ("{0} deltas carried reasoning_details and they are NOT cumulative (counts: {1}). The blob is fragmented across deltas; ObserveReasoningDetails keeps only the last fragment. Reassembly is needed." `
                -f $deltaCount, ($elementCounts -join ', '))
    }
}

<#
.SYNOPSIS
    Test C -- measures whether re-attaching the captured reasoning_details blob round-trips end to end.
.DESCRIPTION
    Reuses the blob + tool calls captured by Test B and builds the follow-up turn the proxy would send: the
    assistant message carries its tool_calls AND the re-attached reasoning_details blob (exactly as
    ReattachReasoningDetails stamps it), followed by a synthetic tool result. To isolate "the blob is accepted"
    from "the follow-up shape works at all", it sends two follow-ups:

      control: assistant message WITHOUT reasoning_details + tool result
      probe:   assistant message WITH    reasoning_details + tool result

    Verdicts:
      control 200 + probe 200 -> PASS          (the blob is accepted and the turn continues; round-trip works)
      control 200 + probe !=200 -> FAIL        (the re-attached blob is what breaks the follow-up)
      control !=200           -> INCONCLUSIVE  (the follow-up shape itself is wrong; the blob says nothing)

    Whether the continuation is *coherent* (not just accepted) is partly qualitative; the assistant's reply text
    is printed so the human can judge it. Requires Test B to have captured a blob first.
#>
function Test-C-RoundTrip {
    param([string] $ApiKey)

    Write-Section 'Test C -- reasoning_details round-trip is accepted and coherent (assumption #3)'

    if ($null -eq $script:Capture) {
        Write-Verdict -Test 'C' -Verdict 'INCONCLUSIVE' `
            -Detail 'No capture from Test B (run B and C together, against a model that emits reasoning_details on a tool call).'
        return
    }
    if ($null -eq $script:Capture.ReasoningDetails) {
        Write-Verdict -Test 'C' -Verdict 'INCONCLUSIVE' `
            -Detail 'Test B captured tool calls but no reasoning_details, so there is no blob to round-trip. Try a "thinking" model.'
        return
    }

    $capture = $script:Capture
    $firstToolCall = $capture.ToolCalls[0]
    $toolCallId = Get-Prop $firstToolCall 'id'

    # A synthetic tool result for the captured tool call, echoing its id so the backend can correlate it.
    $toolResult = @{
        role         = 'tool'
        tool_call_id = $toolCallId
        content      = '{"temperature_c":18,"condition":"cloudy","city":"Berlin"}'
    }

    # Rebuild the assistant turn. The control omits reasoning_details; the probe re-attaches it verbatim, which
    # is exactly what ReattachReasoningDetails does on the positionally-aligned payload message.
    function New-AssistantMessage([bool] $withBlob) {
        $assistant = @{
            role       = 'assistant'
            content    = ''
            tool_calls = $capture.ToolCalls
        }
        if ($withBlob) { $assistant['reasoning_details'] = $capture.ReasoningDetails }
        return $assistant
    }

    function New-FollowUpBody([bool] $withBlob) {
        $body = @{
            model      = $Model
            max_tokens = $MaxOutputTokens
            messages   = @($capture.UserMessage, (New-AssistantMessage $withBlob), $toolResult)
            tools      = $capture.Tools
        }
        # Re-enable reasoning on the follow-up in the active dialect's wire form, matching the original turn.
        & $script:Dialect.WriteReasoning $body 'high'
        return $body
    }

    Write-Host '  Control follow-up (no reasoning_details)...' -ForegroundColor Gray
    $control = Invoke-Backend -ApiKey $ApiKey -Path '/chat/completions' -Body (New-FollowUpBody $false)
    Write-Host ("    control status: {0}" -f $control.Status) -ForegroundColor Gray

    if ($control.Status -ne 200) {
        Write-Verdict -Test 'C' -Verdict 'INCONCLUSIVE' `
            -Detail ("Control follow-up (no blob) returned {0}; the follow-up shape itself is wrong, so the blob result is not interpretable. Body: {1}" `
                -f $control.Status, (Get-BodySnippet -Body $control.Body))
        return
    }

    Write-Host '  Probe follow-up (with re-attached reasoning_details)...' -ForegroundColor Gray
    $probe = Invoke-Backend -ApiKey $ApiKey -Path '/chat/completions' -Body (New-FollowUpBody $true)
    Write-Host ("    probe status:   {0}" -f $probe.Status) -ForegroundColor Gray

    if ($probe.Status -eq 200) {
        # Print the continuation so the human can judge coherence (the accept/reject is machine-decidable).
        $parsed = $probe.Body | ConvertFrom-Json
        $choices = Get-Prop $parsed 'choices'
        $message = if ($choices -and $choices.Count -gt 0) { Get-Prop $choices[0] 'message' } else { $null }
        $reply = Get-Prop $message 'content'
        if ($reply) { Write-Host ("    continuation: {0}" -f (Get-BodySnippet -Body ([string]$reply))) -ForegroundColor DarkGreen }

        Write-Verdict -Test 'C' -Verdict 'PASS' `
            -Detail 'Backend accepted the re-attached reasoning_details on the follow-up (HTTP 200) and produced a continuation. Round-trip works (judge coherence from the printed reply).'
    }
    else {
        Write-Verdict -Test 'C' -Verdict 'FAIL' `
            -Detail ("Control (no blob) was 200 but the probe (with re-attached blob) returned {0}. The re-attached reasoning_details is what breaks the follow-up. Body: {1}" `
                -f $probe.Status, (Get-BodySnippet -Body $probe.Body))
    }
}

<#
.SYNOPSIS
    Test D -- measures whether the correlation key survives a client JSON round-trip (offline, no backend).
.DESCRIPTION
    Replicates ReasoningDetailsCorrelation locally and proves both halves of the contract on a sample turn:

      INVARIANCE (must keep the same key) -- the perturbations a normal client introduces:
        * object key reordering + insignificant whitespace in the arguments
        * arguments delivered as a parsed object instead of the wire JSON string (deserialize/re-serialize)
        * the order of parallel tool calls swapped

      DISCRIMINATION (must change the key) -- genuinely different turns, as negative controls so a degenerate
      "always collides" hash cannot masquerade as surviving:
        * an array argument's element order changed (arrays are ordered)
        * a scalar argument value changed

    PASS only when every invariance case matches the baseline AND every discrimination case differs. This is the
    measurable part of the assumption; the part it cannot prove (a client that SEMANTICALLY rewrites arguments)
    is called out in the summary as still open.
#>
function Test-D-KeyStability {
    Write-Section 'Test D -- correlation key survives a client JSON round-trip (assumption #2)'

    # Scope the key to the active provider, mirroring how ReasoningDetailsCorrelation namespaces keys per backend
    # type. The structural invariance/discrimination logic is backend-independent, but using the real dialect name
    # keeps the measurement honest about which scope it actually exercised.
    $backend = $script:Dialect.Name

    # Wire-shape tool calls: arguments as a JSON string, one with a nested array to enable the reorder control.
    $baseline = @(
        @{ id = 'call_1'; type = 'function'; function = @{ name = 'get_weather'; arguments = '{"city":"Berlin","units":"metric"}' } }
        @{ id = 'call_2'; type = 'function'; function = @{ name = 'get_forecast'; arguments = '{"days":[1,2,3],"city":"Munich"}' } }
    )
    $baseKey = Get-CorrelationKey -BackendName $backend -ToolCalls $baseline
    Write-Host ("  baseline key: {0}" -f $baseKey.Substring(0, 16) + '...') -ForegroundColor Gray

    # --- Invariance cases (must equal the baseline key) ---

    # I1: keys reordered and whitespace added inside the arguments JSON strings.
    $i1 = @(
        @{ function = @{ name = 'get_weather'; arguments = '{ "units" : "metric", "city" : "Berlin" }' } }
        @{ function = @{ name = 'get_forecast'; arguments = '{ "city":"Munich", "days":[1,2,3] }' } }
    )

    # I2: arguments delivered as already-parsed objects (the client deserialized then re-serialized the history).
    $i2 = @(
        @{ function = @{ name = 'get_weather'; arguments = [pscustomobject]@{ units = 'metric'; city = 'Berlin' } } }
        @{ function = @{ name = 'get_forecast'; arguments = [pscustomobject]@{ city = 'Munich'; days = @(1, 2, 3) } } }
    )

    # I3: the two parallel tool calls swapped in order.
    $i3 = @($baseline[1], $baseline[0])

    $invariance = @(
        [pscustomobject]@{ Name = 'key reorder + whitespace'; Calls = $i1 }
        [pscustomobject]@{ Name = 'arguments as parsed object'; Calls = $i2 }
        [pscustomobject]@{ Name = 'parallel call order swapped'; Calls = $i3 }
    )

    # --- Discrimination cases (must differ from the baseline key) ---

    # N1: the nested array's element order changed (ordered arrays => different turn).
    $n1 = @(
        @{ function = @{ name = 'get_weather'; arguments = '{"city":"Berlin","units":"metric"}' } }
        @{ function = @{ name = 'get_forecast'; arguments = '{"days":[3,2,1],"city":"Munich"}' } }
    )

    # N2: a scalar argument value changed (Berlin -> Hamburg).
    $n2 = @(
        @{ function = @{ name = 'get_weather'; arguments = '{"city":"Hamburg","units":"metric"}' } }
        @{ function = @{ name = 'get_forecast'; arguments = '{"days":[1,2,3],"city":"Munich"}' } }
    )

    $discrimination = @(
        [pscustomobject]@{ Name = 'array element order changed'; Calls = $n1 }
        [pscustomobject]@{ Name = 'scalar value changed'; Calls = $n2 }
    )

    $failures = [System.Collections.Generic.List[string]]::new()

    foreach ($case in $invariance) {
        $key = Get-CorrelationKey -BackendName $backend -ToolCalls $case.Calls
        $ok = $key -eq $baseKey
        $mark = if ($ok) { 'same' } else { 'DIFFERS' }
        $color = if ($ok) { 'DarkGreen' } else { 'Red' }
        Write-Host ("    invariance  [{0,-7}] {1}" -f $mark, $case.Name) -ForegroundColor $color
        if (-not $ok) { $failures.Add("invariance '$($case.Name)' changed the key") }
    }

    foreach ($case in $discrimination) {
        $key = Get-CorrelationKey -BackendName $backend -ToolCalls $case.Calls
        $differs = $key -ne $baseKey
        $mark = if ($differs) { 'differs' } else { 'COLLIDES' }
        $color = if ($differs) { 'DarkGreen' } else { 'Red' }
        Write-Host ("    discrimination [{0,-8}] {1}" -f $mark, $case.Name) -ForegroundColor $color
        if (-not $differs) { $failures.Add("discrimination '$($case.Name)' collided with the baseline key") }
    }

    if ($failures.Count -eq 0) {
        Write-Verdict -Test 'D' -Verdict 'PASS' `
            -Detail 'Key is invariant under client reformatting/reordering AND distinct for genuinely different turns. The canonicalization holds against a structural round-trip (semantic argument rewrites remain out of scope).'
    }
    else {
        Write-Verdict -Test 'D' -Verdict 'FAIL' -Detail ($failures -join '; ')
    }
}

<#
.SYNOPSIS
    Test E -- measures whether the active dialect's reasoning OFF switch is accepted by the backend (HTTP 200).
.DESCRIPTION
    Each provider turns reasoning OFF differently, and this test sends exactly that wire form:
      * reasoning.effort = "none"                                   (OpenRouter)
      * reasoning_effort = "none"                                   (OpenAI)
      * venice_parameters.disable_thinking = true                  (Venice)
      * reasoning_effort = "none" + chat_template_kwargs.enable_thinking = false  (vLLM)
    A plain CONTROL request (no reasoning directive at all) is sent first to separate "the off switch is
    rejected" from "this request is malformed for an unrelated reason":
      control 200 + off 200 -> PASS          (the off switch is accepted; the dialect's None path is valid)
      control 200 + off 400 -> FAIL          (the off switch is rejected; the None branch over-sends)
      control non-200       -> INCONCLUSIVE  (something unrelated is wrong; the off result says nothing)
#>
function Test-E-ReasoningOff {
    param([string] $ApiKey)

    Write-Section ("Test E -- '{0}' accepts the dialect reasoning OFF switch" -f $script:Dialect.Name)

    # Minimal chat turn; the off switch -- written in the ACTIVE dialect's wire form -- is what is under test.
    function New-OffBody([bool] $withOff) {
        $body = @{
            model      = $Model
            max_tokens = $MaxOutputTokens
            messages   = @(@{ role = 'user'; content = 'Reply with the single word: ok.' })
        }
        # The dialect decides the off-switch shape: reasoning.effort="none" (OpenRouter), reasoning_effort="none"
        # (OpenAI), venice_parameters.disable_thinking (Venice), or reasoning_effort="none"+enable_thinking=false (vLLM).
        if ($withOff) { & $script:Dialect.WriteReasoningOff $body }
        return $body
    }

    Write-Host '  Control: no reasoning directive...' -ForegroundColor Gray
    $control = Invoke-Backend -ApiKey $ApiKey -Path '/chat/completions' -Body (New-OffBody $false)
    Write-Host ("    control status: {0}" -f $control.Status) -ForegroundColor Gray

    if ($control.Status -ne 200) {
        $snippet = Get-BodySnippet -Body $control.Body
        Write-Verdict -Test 'E' -Verdict 'INCONCLUSIVE' `
            -Detail ("Control (no reasoning directive) returned {0}, not 200 -- the off-switch result is not interpretable. Body: {1}" `
                -f $control.Status, $snippet)
        return
    }

    Write-Host '  Probe: reasoning OFF switch...' -ForegroundColor Gray
    $probe = Invoke-Backend -ApiKey $ApiKey -Path '/chat/completions' -Body (New-OffBody $true)
    Write-Host ("    probe status:   {0}" -f $probe.Status) -ForegroundColor Gray

    if ($probe.Status -eq 200) {
        Write-Verdict -Test 'E' -Verdict 'PASS' `
            -Detail 'Backend accepted the dialect reasoning OFF switch (HTTP 200). The None-effort wire form is valid for this model.'
    }
    elseif ($probe.Status -eq 400) {
        $snippet = Get-BodySnippet -Body $probe.Body
        Write-Verdict -Test 'E' -Verdict 'FAIL' `
            -Detail ("Backend rejected the dialect reasoning OFF switch with HTTP 400 while accepting a plain request. The None branch over-sends. Body: {0}" `
                -f $snippet)
    }
    else {
        $snippet = Get-BodySnippet -Body $probe.Body
        Write-Verdict -Test 'E' -Verdict 'INCONCLUSIVE' `
            -Detail ("Unexpected status {0} for the off-switch probe (control was 200). Body: {1}" -f $probe.Status, $snippet)
    }
}

<#
.SYNOPSIS
    Test F -- measures whether the backend accepts the de-facto top_k / min_p sampling extensions (HTTP 200).
.DESCRIPTION
    OpenRouter, Venice and vLLM override ApplySamplingExtensions to forward top_k/min_p as flat top-level fields
    (WriteTopKAndMinP); the strict OpenAI dialect does NOT, which is why F is not in OpenAI's ApplicableTests.
    This test sends both fields exactly as the proxy stamps them (top_k as an integer, min_p as a float) and uses
    a plain CONTROL (no sampling extensions) to separate "the extensions are rejected" from "this request is
    malformed for an unrelated reason":
      control 200 + probe 200 -> PASS          (the extensions are accepted; forwarding them is safe)
      control 200 + probe 400 -> FAIL          (the backend rejects them; forwarding over-sends)
      control non-200         -> INCONCLUSIVE  (something unrelated is wrong; the extension result says nothing)
#>
function Test-F-SamplingExtensions {
    param([string] $ApiKey)

    Write-Section ("Test F -- '{0}' accepts the top_k / min_p sampling extensions" -f $script:Dialect.Name)

    # Minimal chat turn; the sampling extensions are what is under test. top_k is an integer and min_p a float,
    # the exact shapes WriteTopKAndMinP stamps from the inbound Ollama options.
    function New-SamplingBody([bool] $withExtensions) {
        $body = @{
            model      = $Model
            max_tokens = $MaxOutputTokens
            messages   = @(@{ role = 'user'; content = 'Reply with the single word: ok.' })
        }
        if ($withExtensions) {
            $body['top_k'] = 40
            $body['min_p'] = 0.05
        }
        return $body
    }

    Write-Host '  Control: no sampling extensions...' -ForegroundColor Gray
    $control = Invoke-Backend -ApiKey $ApiKey -Path '/chat/completions' -Body (New-SamplingBody $false)
    Write-Host ("    control status: {0}" -f $control.Status) -ForegroundColor Gray

    if ($control.Status -ne 200) {
        $snippet = Get-BodySnippet -Body $control.Body
        Write-Verdict -Test 'F' -Verdict 'INCONCLUSIVE' `
            -Detail ("Control (no extensions) returned {0}, not 200 -- the sampling result is not interpretable. Body: {1}" `
                -f $control.Status, $snippet)
        return
    }

    Write-Host '  Probe: top_k = 40, min_p = 0.05...' -ForegroundColor Gray
    $probe = Invoke-Backend -ApiKey $ApiKey -Path '/chat/completions' -Body (New-SamplingBody $true)
    Write-Host ("    probe status:   {0}" -f $probe.Status) -ForegroundColor Gray

    if ($probe.Status -eq 200) {
        Write-Verdict -Test 'F' -Verdict 'PASS' `
            -Detail 'Backend accepted the top_k / min_p sampling extensions (HTTP 200). Forwarding them is safe for this model.'
    }
    elseif ($probe.Status -eq 400) {
        $snippet = Get-BodySnippet -Body $probe.Body
        Write-Verdict -Test 'F' -Verdict 'FAIL' `
            -Detail ("Backend rejected top_k / min_p with HTTP 400 while accepting a plain request. Forwarding them over-sends. Body: {0}" `
                -f $snippet)
    }
    else {
        $snippet = Get-BodySnippet -Body $probe.Body
        Write-Verdict -Test 'F' -Verdict 'INCONCLUSIVE' `
            -Detail ("Unexpected status {0} for the sampling probe (control was 200). Body: {1}" -f $probe.Status, $snippet)
    }
}

<#
.SYNOPSIS
    Test G -- measures whether the dialect's forced vendor parameters are accepted by the backend (HTTP 200).
.DESCRIPTION
    Only Venice forces a vendor parameter on every chat request: venice_parameters.include_venice_system_prompt =
    false (VeniceProvider.ApplyVendorParameters), which is why G is in Venice's ApplicableTests alone. This test
    sends that block exactly as the proxy forces it, with a plain CONTROL (no venice_parameters) first to separate
    "the vendor block is rejected" from "this request is malformed for an unrelated reason":
      control 200 + probe 200 -> PASS          (the forced vendor block is accepted; forcing it is safe)
      control 200 + probe 400 -> FAIL          (the backend rejects it; forcing it breaks every request)
      control non-200         -> INCONCLUSIVE  (something unrelated is wrong; the vendor result says nothing)

    The dialect with no forced vendor parameters has no WriteVendorParams writer; G is filtered out of its
    ApplicableTests, so the defensive guard below is belt-and-suspenders rather than an expected path.
#>
function Test-G-VendorParameters {
    param([string] $ApiKey)

    Write-Section ("Test G -- '{0}' accepts the forced vendor parameters" -f $script:Dialect.Name)

    # Belt-and-suspenders: ApplicableTests already restricts G to dialects that force vendor parameters, but a
    # direct -Tests G on a dialect without them would otherwise dereference a null writer.
    if ($null -eq $script:Dialect.WriteVendorParams) {
        Write-Verdict -Test 'G' -Verdict 'INCONCLUSIVE' `
            -Detail ("The '{0}' dialect forces no vendor parameters, so there is nothing to measure here." -f $script:Dialect.Name)
        return
    }

    # Minimal chat turn; the forced vendor block is what is under test, not the content.
    function New-VendorBody([bool] $withVendor) {
        $body = @{
            model      = $Model
            max_tokens = $MaxOutputTokens
            messages   = @(@{ role = 'user'; content = 'Reply with the single word: ok.' })
        }
        # The dialect writes its forced vendor parameters (Venice: venice_parameters.include_venice_system_prompt = false).
        if ($withVendor) { & $script:Dialect.WriteVendorParams $body }
        return $body
    }

    Write-Host '  Control: no vendor parameters...' -ForegroundColor Gray
    $control = Invoke-Backend -ApiKey $ApiKey -Path '/chat/completions' -Body (New-VendorBody $false)
    Write-Host ("    control status: {0}" -f $control.Status) -ForegroundColor Gray

    if ($control.Status -ne 200) {
        $snippet = Get-BodySnippet -Body $control.Body
        Write-Verdict -Test 'G' -Verdict 'INCONCLUSIVE' `
            -Detail ("Control (no vendor parameters) returned {0}, not 200 -- the vendor result is not interpretable. Body: {1}" `
                -f $control.Status, $snippet)
        return
    }

    Write-Host '  Probe: forced vendor parameters...' -ForegroundColor Gray
    $probe = Invoke-Backend -ApiKey $ApiKey -Path '/chat/completions' -Body (New-VendorBody $true)
    Write-Host ("    probe status:   {0}" -f $probe.Status) -ForegroundColor Gray

    if ($probe.Status -eq 200) {
        Write-Verdict -Test 'G' -Verdict 'PASS' `
            -Detail 'Backend accepted the forced vendor parameters (HTTP 200). Forcing them on every request is safe for this model.'
    }
    elseif ($probe.Status -eq 400) {
        $snippet = Get-BodySnippet -Body $probe.Body
        Write-Verdict -Test 'G' -Verdict 'FAIL' `
            -Detail ("Backend rejected the forced vendor parameters with HTTP 400 while accepting a plain request. Forcing them breaks every request. Body: {0}" `
                -f $snippet)
    }
    else {
        $snippet = Get-BodySnippet -Body $probe.Body
        Write-Verdict -Test 'G' -Verdict 'INCONCLUSIVE' `
            -Detail ("Unexpected status {0} for the vendor probe (control was 200). Body: {1}" -f $probe.Status, $snippet)
    }
}

<#
.SYNOPSIS
    Test H -- measures whether the model accepts the capability prober's COMPLETION probe payload (HTTP 2xx).
.DESCRIPTION
    Sends OpenAiCapabilityProber.BuildCompletionProbePayload() byte-for-byte: a single silent user turn, streamed,
    with no token-cap parameter. This is the production prober's plain chat-completion probe; a 2xx is the prober
    reading the model as completion-capable. The verdict uses Get-ProbeClassification so the live read matches the
    prober's status policy exactly. The default model is expected to be a normal chat model, so 2xx is the PASS;
    a body-rejecting 4xx is reported as the prober's "completion unsupported" reading (rare for a chat model, so
    it is surfaced as inconclusive with the rejection body rather than a clean negative).
#>
function Test-H-CompletionProbe {
    param([string] $ApiKey)

    Write-Section 'Test H -- model accepts the completion probe payload (capability prober)'

    # Byte-identical to BuildCompletionProbePayload(): silent user turn, streamed, no token cap.
    $body = @{
        model    = $Model
        messages = @(@{ role = 'user'; content = $script:SilentProbePrompt })
        stream   = $true
    }

    Write-Host '  Completion probe (silent prompt, streamed)...' -ForegroundColor Gray
    $probe = Invoke-Backend -ApiKey $ApiKey -Path '/chat/completions' -Body $body
    Write-Host ("    status: {0}" -f $probe.Status) -ForegroundColor Gray

    Write-PositiveCapabilityVerdict -Test 'H' -Capability 'completion' -Status $probe.Status -Body $probe.Body
}

<#
.SYNOPSIS
    Test I -- measures whether the model accepts the capability prober's TOOL probe payload (HTTP 2xx).
.DESCRIPTION
    Sends OpenAiCapabilityProber.BuildToolProbePayload() byte-for-byte: a silent user turn plus one trivial
    "ping" function whose presence exercises the backend's tools handling, streamed, with no token cap. A 2xx is
    the prober reading the model as tool-capable. NOTE the prober's documented false-positive caveat: a backend
    that does not understand tools may SILENTLY IGNORE the tools array and still answer 2xx -- a status read
    cannot refute that, so a PASS here confirms the payload was ACCEPTED, not that the tool was exercised. The
    verdict mirrors the prober's status policy via Get-ProbeClassification.
#>
function Test-I-ToolProbe {
    param([string] $ApiKey)

    Write-Section 'Test I -- model accepts the tool probe payload (capability prober)'

    # Byte-identical to BuildToolProbePayload(): silent user turn + a trivial "ping" function, streamed.
    $body = @{
        model    = $Model
        messages = @(@{ role = 'user'; content = $script:SilentProbePrompt })
        tools    = @(
            @{
                type     = 'function'
                function = @{
                    name        = 'ping'
                    description = 'A trivial probe function.'
                    parameters  = @{
                        type       = 'object'
                        properties = @{}
                    }
                }
            }
        )
        stream   = $true
    }

    Write-Host '  Tool probe (silent prompt + ping function, streamed)...' -ForegroundColor Gray
    $probe = Invoke-Backend -ApiKey $ApiKey -Path '/chat/completions' -Body $body
    Write-Host ("    status: {0}" -f $probe.Status) -ForegroundColor Gray

    Write-PositiveCapabilityVerdict -Test 'I' -Capability 'tool' -Status $probe.Status -Body $probe.Body
}

<#
.SYNOPSIS
    Test J -- measures whether the SHIPPED placeholder image passes the prober's VISION probe (HTTP 2xx).
.DESCRIPTION
    Sends OpenAiCapabilityProber.BuildVisionProbePayload() byte-for-byte: a silent text part plus the exact
    PlaceholderImageDataUri the prober ships, streamed, with no token cap. This is the one capability test whose
    PAYLOAD itself is under test, not just the model: the prober's own comment records that the decisive gate is
    image CONTENT (a flat/monochrome placeholder gets rejected by some backends' feature checks), so a 2xx here is
    direct evidence the shipped image still passes validation on a real vision backend.

    Because of that, J does NOT use the generic positive-verdict helper -- a body-rejecting 4xx is genuinely
    ambiguous and both readings must be spelled out:
      * If the model HAS vision, an Absent (4xx) result is evidence the placeholder image REGRESSED -- exactly the
        false negative the prober warns about ("a plain probe image made vision-capable models probe as vision-less").
      * If the model LACKS vision, the same 4xx is the correct negative.
    Run J against a known vision-capable model to make the result decisive.
#>
function Test-J-VisionProbe {
    param([string] $ApiKey)

    Write-Section 'Test J -- shipped placeholder image passes the vision probe (capability prober)'

    # Byte-identical to BuildVisionProbePayload(): text(silent) + image_url(PlaceholderImageDataUri), streamed.
    $body = @{
        model    = $Model
        messages = @(
            @{
                role    = 'user'
                content = @(
                    @{ type = 'text'; text = $script:SilentProbePrompt }
                    @{ type = 'image_url'; image_url = @{ url = $script:PlaceholderImageDataUri } }
                )
            }
        )
        stream   = $true
    }

    Write-Host '  Vision probe (silent text + shipped placeholder image, streamed)...' -ForegroundColor Gray
    $probe = Invoke-Backend -ApiKey $ApiKey -Path '/chat/completions' -Body $body
    Write-Host ("    status: {0}" -f $probe.Status) -ForegroundColor Gray

    switch (Get-ProbeClassification -Status $probe.Status) {
        'Present' {
            Write-Verdict -Test 'J' -Verdict 'PASS' `
                -Detail ("Backend accepted the shipped placeholder image (HTTP {0}). Direct evidence the probe image still passes a real vision backend's content validation -- the prober reads this model as vision-capable." `
                    -f $probe.Status)
        }
        'Absent' {
            Write-Verdict -Test 'J' -Verdict 'INCONCLUSIVE' `
                -Detail ("Backend rejected the vision probe with a body-rejecting HTTP {0}. AMBIGUOUS: if this model HAS vision, the SHIPPED placeholder image regressed (the false negative the prober warns about); if it LACKS vision, this is the correct negative. Re-run against a known vision model to decide. Upstream said: {1}" `
                    -f $probe.Status, (Get-BodySnippet -Body $probe.Body))
        }
        'Permanent' {
            Write-Verdict -Test 'J' -Verdict 'INCONCLUSIVE' `
                -Detail ("Vision probe returned a permanent HTTP {0} (auth/routing); the prober treats this as inconclusive. Check the key, base URL, and model id." `
                    -f $probe.Status)
        }
        'Transient' {
            Write-Verdict -Test 'J' -Verdict 'INCONCLUSIVE' `
                -Detail ("Vision probe returned a transient HTTP {0} (throttle/server error); the prober would retry. Re-run when the backend is healthy." `
                    -f $probe.Status)
        }
    }
}

<#
.SYNOPSIS
    Test K -- measures whether the model accepts the capability prober's EMBEDDING probe payload (HTTP 2xx).
.DESCRIPTION
    Sends OpenAiCapabilityProber.BuildEmbeddingProbePayload() byte-for-byte: { model, input = "ping" } POSTed to
    the NON-streaming /embeddings endpoint (embeddings perform no generation). A 2xx is the prober reading the
    model as embedding-capable. A chat-only model typically rejects /embeddings with a body-rejecting 4xx, which
    the prober reads as "embedding unsupported" -- so run K against an embedding model to verify the positive
    path. The verdict mirrors the prober's status policy via Get-ProbeClassification.
#>
function Test-K-EmbeddingProbe {
    param([string] $ApiKey)

    Write-Section 'Test K -- model accepts the embedding probe payload (capability prober)'

    # Byte-identical to BuildEmbeddingProbePayload(): a single short input to the embeddings endpoint (no stream).
    $body = @{
        model = $Model
        input = 'ping'
    }

    Write-Host '  Embedding probe (input = "ping", non-streaming)...' -ForegroundColor Gray
    $probe = Invoke-Backend -ApiKey $ApiKey -Path '/embeddings' -Body $body
    Write-Host ("    status: {0}" -f $probe.Status) -ForegroundColor Gray

    Write-PositiveCapabilityVerdict -Test 'K' -Capability 'embedding' -Status $probe.Status -Body $probe.Body
}

<#
.SYNOPSIS
    Test L -- measures the prober's load-bearing STREAMING-HEADERS assumption: a streamed chat request returns its
    response headers at stream-open, while a non-streamed request defers them until the whole generation is
    buffered.
.DESCRIPTION
    This is the assumption OpenAiCapabilityProber.AddStreaming() rests on, and the reason the prober's HttpClient
    runs with an INFINITE client timeout and reads headers-only: "a non-streaming request buffers the entire
    generation before sending response headers ... a streaming request returns its 200 headers the moment the
    stream opens". If that did NOT hold, the prober's headers-only read would block for the full reply and trip
    the per-attempt timeout -- the exact failure the streaming flag exists to avoid.

    To make the difference observable, L uses a prompt that generates a non-trivial amount of text (so the
    non-streamed buffering takes measurable wall-clock time) and measures TIME-TO-RESPONSE-HEADERS for both
    transports with HttpCompletionOption.ResponseHeadersRead -- exactly what the prober uses. It reads only the
    headers (never the body), then disposes:

      * non-streaming time-to-headers  ~= full generation time (headers arrive with the buffered JSON)
      * streaming     time-to-headers  ~= stream-open time      (headers arrive before the first token)

    Verdict (a wall-clock timing measurement, so network jitter applies -- re-run a borderline result):
      both 200, non-stream slow enough, stream headers << non-stream headers -> PASS
      generation too fast to distinguish the two                              -> INCONCLUSIVE
      streaming headers NOT meaningfully earlier                              -> FAIL (the prober would block here)
#>
function Test-L-StreamingHeaders {
    param([string] $ApiKey)

    Write-Section 'Test L -- streaming returns headers before generation completes (capability prober)'

    # A prompt that reliably generates enough tokens for the non-streamed buffering to take measurable time; the
    # silent probe prompt would finish too fast to distinguish stream-open from end-of-generation. Cap at >=128
    # tokens so even a low -MaxOutputTokens still produces a measurable generation window.
    $timingPrompt = 'List the numbers from 1 to 80, one per line, and nothing else.'
    $timingTokens = [Math]::Max(128, [Math]::Min($MaxOutputTokens, 256))

    # Bespoke transport: the shared Invoke-Backend / Invoke-BackendStream helpers both read to completion and
    # expose no time-to-headers, which is the single quantity this test needs. Measuring it requires a direct
    # ResponseHeadersRead send with a stopwatch, mirroring exactly how the prober reads the response.
    function Measure-TimeToHeaders([bool] $Stream) {
        $body = @{
            model      = $Model
            max_tokens = $timingTokens
            messages   = @(@{ role = 'user'; content = $timingPrompt })
        }
        if ($Stream) {
            $body['stream'] = $true
            $body['stream_options'] = @{ include_usage = $true }
        }
        $json = $body | ConvertTo-Json -Depth 100 -Compress

        $client = [System.Net.Http.HttpClient]::new()
        $client.Timeout = [TimeSpan]::FromMinutes(2)
        try {
            $uri = ($BaseUrl.TrimEnd('/')) + '/chat/completions'
            $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, $uri)
            # A keyless provider (self-hosted vLLM) sends no Authorization header.
            if (-not [string]::IsNullOrWhiteSpace($ApiKey)) {
                $request.Headers.Authorization =
                    [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $ApiKey)
            }
            if ($Stream) {
                $request.Headers.Accept.Add(
                    [System.Net.Http.Headers.MediaTypeWithQualityHeaderValue]::new('text/event-stream'))
            }
            $request.Content =
                [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, 'application/json')

            # Stop the clock the instant the response HEADERS are available -- not the body. For a non-streamed
            # completion the server sends headers only after buffering the whole generation, so this elapsed time
            # tracks the full reply; for a streamed completion the headers arrive at stream-open.
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            $response = $client.SendAsync(
                $request,
                [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
            $sw.Stop()
            $status = [int] $response.StatusCode
            $response.Dispose()   # Abandon the body unread; only the time-to-headers matters here.
            return [pscustomobject]@{ Status = $status; ElapsedMs = $sw.Elapsed.TotalMilliseconds }
        }
        finally {
            $client.Dispose()
        }
    }

    Write-Host '  Non-streaming time-to-headers (buffers full generation first)...' -ForegroundColor Gray
    $nonStream = Measure-TimeToHeaders $false
    Write-Host ("    status: {0}, headers after {1:0} ms" -f $nonStream.Status, $nonStream.ElapsedMs) -ForegroundColor Gray

    Write-Host '  Streaming time-to-headers (should arrive at stream-open)...' -ForegroundColor Gray
    $stream = Measure-TimeToHeaders $true
    Write-Host ("    status: {0}, headers after {1:0} ms" -f $stream.Status, $stream.ElapsedMs) -ForegroundColor Gray

    if ($nonStream.Status -ne 200 -or $stream.Status -ne 200) {
        Write-Verdict -Test 'L' -Verdict 'INCONCLUSIVE' `
            -Detail ("Need HTTP 200 on both transports to compare timing (got non-stream {0}, stream {1}). Fix the model/auth and re-run." `
                -f $nonStream.Status, $stream.Status)
        return
    }

    # If the non-streamed call returned almost immediately, generation was too short to separate stream-open from
    # end-of-generation -- the comparison cannot prove anything. Ask for a longer generation rather than guess.
    if ($nonStream.ElapsedMs -lt 750) {
        Write-Verdict -Test 'L' -Verdict 'INCONCLUSIVE' `
            -Detail ("Non-streaming headers arrived in {0:0} ms -- too fast to distinguish buffered from streamed headers. Raise -MaxOutputTokens or use a model that generates more, then re-run." `
                -f $nonStream.ElapsedMs)
        return
    }

    $gapMs = $nonStream.ElapsedMs - $stream.ElapsedMs
    if ($stream.ElapsedMs -lt ($nonStream.ElapsedMs * 0.5) -and $gapMs -ge 400) {
        Write-Verdict -Test 'L' -Verdict 'PASS' `
            -Detail ("Streaming headers arrived in {0:0} ms vs {1:0} ms non-streaming ({2:0} ms earlier). The backend sends headers at stream-open, not after generation -- the prober's headers-only read confirms support on time-to-first-byte as designed." `
                -f $stream.ElapsedMs, $nonStream.ElapsedMs, $gapMs)
    }
    else {
        Write-Verdict -Test 'L' -Verdict 'FAIL' `
            -Detail ("Streaming headers ({0:0} ms) were NOT meaningfully earlier than non-streaming ({1:0} ms). This backend appears to buffer before sending headers, so the prober's headers-only streamed read would block for the full generation and risk a timeout. (Timing is wall-clock; re-run to rule out jitter.)" `
                -f $stream.ElapsedMs, $nonStream.ElapsedMs)
    }
}

#endregion Tests

#region Dispatch and summary

# Resolve the active provider dialect once, up front: every downstream stage (discovery, transport, each test)
# reads its wire forms, base URL, key source, and applicable-test set from here. -BaseUrl / -Model override the
# dialect's defaults; an empty result after the override means the dialect has no safe default and the operator
# must supply one (a self-hosted vLLM URL, an OpenAI model id).
$script:Dialect = Get-Dialect -Provider $Provider

$resolvedBaseUrl = if (-not [string]::IsNullOrWhiteSpace($BaseUrl)) { $BaseUrl } else { $script:Dialect.BaseUrl }
if ([string]::IsNullOrWhiteSpace($resolvedBaseUrl)) {
	throw "Provider '$Provider' has no canonical base URL; pass one explicitly via -BaseUrl."
}
$BaseUrl = $resolvedBaseUrl

# Discovery mode short-circuits: it runs no tests and needs no API key, so it returns before the model/key
# checks below. This is the path that helps the user find a working -Model slug.
if ($ListModels) {
	Invoke-ListModels
	return
}

$resolvedModel = if (-not [string]::IsNullOrWhiteSpace($Model)) { $Model } else { $script:Dialect.DefaultModel }
if ([string]::IsNullOrWhiteSpace($resolvedModel)) {
	throw "Provider '$Provider' has no default model; pass one explicitly via -Model (try -ListModels first)."
}
$Model = $resolvedModel

# -All (or no explicit -Tests) runs the full set; otherwise honor the explicit selection. Running everything
# by default keeps the bare invocation useful while still allowing a single-test run via -Tests. The selection
# is then narrowed to the tests the active dialect declares applicable, so a nonsensical combination (sampling
# forwarding on plain OpenAI, the Venice-only vendor probe elsewhere) is skipped with a clear note rather than
# producing a misleading FAIL.
$allTestIds = @('A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J', 'K', 'L')
$requested = if ($All -or $Tests.Count -eq 0) { $allTestIds } else { @($Tests | Sort-Object -Unique) }

$skipped = @($requested | Where-Object { $_ -notin $script:Dialect.ApplicableTests })
$selected = @($requested | Where-Object { $_ -in $script:Dialect.ApplicableTests })

# Every test except D hits the live backend; D is a pure local measurement. Fetch the key only when a
# backend-hitting test is selected. Get-ApiKey returns $null for a keyless provider (self-hosted vLLM), in
# which case the transport helpers send no Authorization header.
$needsBackend = @($selected | Where-Object { $_ -ne 'D' }).Count -gt 0
$apiKey = if ($needsBackend) { Get-ApiKey } else { $null }

Write-Host ''
Write-Host "OllamaProxy backend-assumption live probe" -ForegroundColor White
Write-Host "  Provider: $($script:Dialect.Name)" -ForegroundColor Gray
Write-Host "  Backend : $BaseUrl" -ForegroundColor Gray
Write-Host "  Model   : $Model" -ForegroundColor Gray
Write-Host "  Tests   : $($selected -join ', ')" -ForegroundColor Gray
if ($skipped.Count -gt 0) {
    Write-Host "  Skipped : $($skipped -join ', ') (not applicable to the '$($script:Dialect.Name)' dialect)" -ForegroundColor DarkYellow
}

# Test C depends on a capture from Test B; ensure B runs first when both are selected.
if ('A' -in $selected) { Test-A-CeilingEffort -ApiKey $apiKey }
if ('B' -in $selected) { Test-B-ReasoningDetailsShape -ApiKey $apiKey }
if ('C' -in $selected) { Test-C-RoundTrip -ApiKey $apiKey }
if ('D' -in $selected) { Test-D-KeyStability }
if ('E' -in $selected) { Test-E-ReasoningOff -ApiKey $apiKey }
if ('F' -in $selected) { Test-F-SamplingExtensions -ApiKey $apiKey }
if ('G' -in $selected) { Test-G-VendorParameters -ApiKey $apiKey }
# Capability-prober family (H-L): each mirrors an OpenAiCapabilityProber probe payload / timing assumption.
if ('H' -in $selected) { Test-H-CompletionProbe -ApiKey $apiKey }
if ('I' -in $selected) { Test-I-ToolProbe -ApiKey $apiKey }
if ('J' -in $selected) { Test-J-VisionProbe -ApiKey $apiKey }
if ('K' -in $selected) { Test-K-EmbeddingProbe -ApiKey $apiKey }
if ('L' -in $selected) { Test-L-StreamingHeaders -ApiKey $apiKey }

Write-Section 'Summary -- which comments the measurement lets you rewrite'

# Maps each test to the assumption it measures and the exact code location whose comment the verdict governs,
# so a PASS/FAIL/INCONCLUSIVE translates directly into a comment-edit decision.
$commentMap = @{
    'A' = [pscustomobject]@{
        Assumption = '#4  the dialect CEILING reasoning effort (e.g. "xhigh") is accepted; "max" above it is rejected'
        Location   = 'OpenAiCompatibleProvider.MaxDialectReasoningEffort / <Provider>Provider.ApplyReasoning()'
        OnPass     = 'Confirm the dialect-ceiling comment: the ceiling token is measured-accepted (and where the ceiling sits below "max", the "max" negative control is measured-rejected).'
        OnFail     = 'The ceiling token was rejected: lower MaxDialectReasoningEffort, or pin per model.'
    }
    'B' = [pscustomobject]@{
        Assumption = '#1  reasoning_details arrives complete on a single streamed delta'
        Location   = 'OpenAiCompatibleProvider.ObserveReasoningDetails()'
        OnPass     = 'Replace the "assumption (not yet measured)" wording with the measured single/cumulative-delta fact.'
        OnFail     = 'Keep the assumption note AND implement the reassembly the comment says "would be added here".'
    }
    'C' = [pscustomobject]@{
        Assumption = '#3  the reasoning_details round-trip is accepted end to end'
        Location   = 'ReasoningDetailsCacheOptions (the "not yet measured against a live backend" disclaimer)'
        OnPass     = 'Rewrite the disclaimer to record that the round-trip was measured against this live backend/model.'
        OnFail     = 'Keep the disclaimer; the re-attached blob is rejected -- investigate before claiming the feature works.'
    }
    'D' = [pscustomobject]@{
        Assumption = '#2  the correlation key survives a client deserialize/re-serialize round-trip'
        Location   = 'ReasoningDetailsCorrelation (the "not measured against a live backend" note)'
        OnPass     = 'Soften the note: the key is measured-stable under structural round-trips (semantic rewrites still out of scope).'
        OnFail     = 'Keep the note; the canonicalization does not survive the round-trip as designed.'
    }
    'E' = [pscustomobject]@{
        Assumption = 'Reasoning OFF switch: the dialect''s None-effort wire form is accepted by the backend'
        Location   = '<Provider>Provider.ApplyReasoning() (the None branch) / OpenAiCompatibleProvider reasoning off path'
        OnPass     = 'Record that the dialect''s reasoning-off wire form was measured as accepted on this backend/model.'
        OnFail     = 'The None branch over-sends -- revisit how the provider encodes "reasoning off" for this backend.'
    }
    'F' = [pscustomobject]@{
        Assumption = 'Sampling extensions: the backend honors the de-facto top_k / min_p fields'
        Location   = '<Provider>Provider.ApplySamplingExtensions() -> WriteTopKAndMinP()'
        OnPass     = 'Record that top_k / min_p were measured as accepted; forwarding them is safe for this backend.'
        OnFail     = 'The backend rejects top_k / min_p -- ApplySamplingExtensions should not forward them here.'
    }
    'G' = [pscustomobject]@{
        Assumption = 'Vendor parameters: the forced venice_parameters.include_venice_system_prompt = false is accepted'
        Location   = 'VeniceProvider.ApplyVendorParameters()'
        OnPass     = 'Record that the forced vendor block was measured as accepted; forcing it on every request is safe.'
        OnFail     = 'The backend rejects the forced vendor block -- revisit ApplyVendorParameters before forcing it.'
    }
    'H' = [pscustomobject]@{
        Assumption = 'Capability prober: the completion probe payload is accepted by a chat model (status-based read)'
        Location   = 'OpenAiCapabilityProber.BuildCompletionProbePayload() / ProbeOnceAsync() status policy'
        OnPass     = 'Confirms the completion probe payload + 2xx=present reading work against this backend; no comment change needed.'
        OnFail     = 'The completion probe was not accepted on a chat model -- investigate the payload or status policy before trusting completion probing here.'
    }
    'I' = [pscustomobject]@{
        Assumption = 'Capability prober: the tool probe payload is accepted (NB: a 2xx confirms acceptance, not that tools were exercised)'
        Location   = 'OpenAiCapabilityProber.BuildToolProbePayload() / the silent-ignore false-positive caveat in the class remarks'
        OnPass     = 'Confirms the tool probe payload is accepted; the documented silent-ignore false-positive caveat still stands and stays in the remarks.'
        OnFail     = 'The tool probe payload was rejected on a tool-capable model -- revisit BuildToolProbePayload before trusting tool probing here.'
    }
    'J' = [pscustomobject]@{
        Assumption = 'Capability prober: the SHIPPED placeholder image passes a real vision backend''s content validation'
        Location   = 'OpenAiCapabilityProber.PlaceholderImageDataUri (the "do not reintroduce a flat probe image" comment)'
        OnPass     = 'Confirms the shipped image still passes validation on this vision model; the "busy image required" comment remains accurate.'
        OnFail     = 'AMBIGUOUS on a 4xx -- on a KNOWN vision model this means the placeholder image regressed (re-derive a busier image); on a non-vision model it is the correct negative.'
    }
    'K' = [pscustomobject]@{
        Assumption = 'Capability prober: the embedding probe payload is accepted by an embedding model (status-based read)'
        Location   = 'OpenAiCapabilityProber.BuildEmbeddingProbePayload() / the embeddings endpoint path'
        OnPass     = 'Confirms the embedding probe payload + endpoint work against this backend; no comment change needed.'
        OnFail     = 'The embedding probe was not accepted on an embedding model -- investigate the payload or endpoint before trusting embedding probing here.'
    }
    'L' = [pscustomobject]@{
        Assumption = 'Capability prober: a streamed chat request returns headers at stream-open, before generation completes'
        Location   = 'OpenAiCapabilityProber.AddStreaming() + the infinite-client-timeout / headers-only read rationale'
        OnPass     = 'Confirms the streaming-headers assumption the prober rests on; the AddStreaming() rationale is measured-correct for this backend.'
        OnFail     = 'This backend buffers before sending headers -- the prober''s headers-only streamed read would block for the full generation; reconsider the streaming-probe approach for this backend.'
    }
}

foreach ($test in $allTestIds) {
    if (-not $script:Findings.ContainsKey($test)) { continue }

    $finding = $script:Findings[$test]
    $map = $commentMap[$test]

    Write-Verdict -Test $test -Verdict $finding.Verdict -Detail $map.Assumption
    Write-Host ("        location : {0}" -f $map.Location) -ForegroundColor Gray

    $action = switch ($finding.Verdict) {
        'PASS' { $map.OnPass }
        'FAIL' { $map.OnFail }
        default { 'INCONCLUSIVE -- not measured this run; leave the comment unchanged until you can run it against a suitable model.' }
    }
    Write-Host ("        action   : {0}" -f $action) -ForegroundColor Gray
}

Write-Host ''
Write-Host 'Note: paste this output back into the chat and the comments will be rewritten to match the measurement.' -ForegroundColor DarkCyan
Write-Host 'Assumption #2 cannot be fully proven here: a client that SEMANTICALLY rewrites tool-call arguments' -ForegroundColor DarkCyan
Write-Host 'remains out of scope for any offline test -- only a real end-to-end client run can close that last gap.' -ForegroundColor DarkCyan

#endregion Dispatch and summary
