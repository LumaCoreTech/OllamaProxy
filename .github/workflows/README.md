# GitHub Workflows

CI/CD workflows for OllamaProxy.

---

## Overview

| Workflow | Trigger | Purpose |
| --- | --- | --- |
| [`build.yml`](build.yml) | Push / PR to `main` | Build and test on Linux **and** Windows; publishes a test report, uploads coverage, generates badge data, and (on `main`) publishes the badges to the `badges` branch in a dependent `publish-badges` job. |
| [`release.yml`](release.yml) | Version tags (`v*`) | Build, test, publish a multi-arch container image to GHCR, and create a GitHub Release. |

Dependency updates are automated via [`../dependabot.yml`](../dependabot.yml) for NuGet packages,
GitHub Actions, and the Docker base image.

---

## `build.yml` — CI

- **Runners:** `ubuntu-latest` and `windows-latest` (matrix, `fail-fast: false`) to guarantee
  cross-platform compatibility.
- **Steps:** restore → build (`Release`) → `dotnet test` with TRX logging and `XPlat Code Coverage`.
- **Reporting:** [`dorny/test-reporter`](https://github.com/dorny/test-reporter) surfaces results as
  a check; the Cobertura coverage file is uploaded as an artifact (`coverage-<os>`).

The test step uses the VSTest pipeline (`--collect:"XPlat Code Coverage"`), matching the project's
`Microsoft.NET.Test.Sdk` + `coverlet.collector` setup.

---

## `release.yml` — Release

- **Trigger:** pushing a tag like `v1.0.0`.
- **Image:** built from the repository [`Dockerfile`](../../Dockerfile) for `linux/amd64` and
  `linux/arm64`, pushed to **GHCR** (`ghcr.io/<owner>/<repo>`). Tags are derived from the version via
  [`docker/metadata-action`](https://github.com/docker/metadata-action): the full version,
  `major.minor`, and `latest`.
- **Auth:** uses the automatically-provided `GITHUB_TOKEN` — no extra secrets required.
- **Release notes:** generated automatically by
  [`softprops/action-gh-release`](https://github.com/softprops/action-gh-release).

To cut a release:

```bash
git tag v1.0.0
git push origin v1.0.0
```

---

## Badges

The README badges use the [shields.io endpoint API](https://shields.io/endpoint) backed by JSON files
on an orphan **`badges`** branch:

1. Each matrix leg of `build.yml` (Ubuntu, Windows) derives a **build** badge from its own `job.status`,
   parses its TRX counters into a **test** badge, and runs `dotnet-reportgenerator-globaltool` over the
   Cobertura coverage into a **coverage** badge, uploading all three as a per-OS `badge-data-<os>` artifact.
2. The dependent **`publish-badges`** job in the *same* workflow run (`needs: build`, `if: always()`,
   restricted to `main`) downloads those artifacts and commits the JSON to the `badges` branch
   (creating it as an empty orphan on first run).
3. shields.io reads the raw JSON from
   `https://raw.githubusercontent.com/<owner>/<repo>/badges/ollamaproxy-<os>-<kind>-badge.json`.

> [!IMPORTANT]
> Badge publishing lives **inside** `build.yml` as a dependent job rather than in a separate
> `workflow_run`-triggered workflow. A `workflow_run` trigger only fires from the default branch's
> copy of the file, requires an exact workflow-name match, and never runs until the file already
> exists on `main` — all silent failure modes. Keeping it in-run downloads the artifacts from the
> same run (no cross-workflow trigger, no run-id juggling), so it reliably runs every time.

| Badge | File on `badges` branch |
| --- | --- |
| Ubuntu Build | `ollamaproxy-ubuntu-build-badge.json` |
| Ubuntu Tests | `ollamaproxy-ubuntu-test-badge.json` |
| Ubuntu Coverage | `ollamaproxy-ubuntu-coverage-badge.json` |
| Windows Build | `ollamaproxy-windows-build-badge.json` |
| Windows Tests | `ollamaproxy-windows-test-badge.json` |
| Windows Coverage | `ollamaproxy-windows-coverage-badge.json` |

### Failure handling

The badge-generation steps are deliberately **resilient to a broken build**, so the README always
reflects reality instead of freezing on the last green run:

- All three badge steps run with `if: always()` **and** `continue-on-error: true`, and each one
  *always* writes its JSON file.
- The build badge is derived from `job.status`, so it reads **`failing`** (red) whenever any earlier
  step in the leg — restore, build, test, or the Windows installer smoke build — failed.
- If restore/build fails before any test runs (no TRX), the test badge is written as a red
  **`build failed`**.
- The test badge's red/green verdict counts only genuine failures (`total − passed − skipped`), so
  **skipped** tests — such as gated-off live tests — never turn it red; the skip count is still shown
  in the message (e.g. `799 passed, 12 skipped`) for transparency.
- If no coverage is collected, the coverage badge is written as a red **`unavailable`** (and
  `reportgenerator` is skipped entirely rather than erroring on a missing report).
- `Upload Badge Data` uses `if-no-files-found: warn`, so a missing artifact is loud in the logs.

Because `publish-badges` runs with `if: always()` (regardless of the build legs' outcome), these red
badges are published just like green ones — a failing pipeline visibly turns the README badges red.

> [!NOTE]
> The badges show as "invalid" until the first CI run on `main` populates the `badges` branch — that
> is expected on a fresh repository. No extra secret is needed: the `publish-badges` job pushes with
> the built-in `GITHUB_TOKEN` (job-level `contents: write`).

