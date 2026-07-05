# Contributing to OllamaProxy

Thanks for your interest in improving **OllamaProxy**! This document covers how to get set up, the
conventions the project follows, and what a good pull request looks like.

By contributing you agree that your contributions are licensed under the project's
[MIT License](LICENSE).

## Table of contents

1. [Getting started](#getting-started)
2. [Building and testing](#building-and-testing)
3. [Coding conventions](#coding-conventions)
4. [Commit messages](#commit-messages)
5. [Third-party dependencies](#third-party-dependencies)
6. [Pull requests](#pull-requests)
7. [Reporting bugs and requesting features](#reporting-bugs-and-requesting-features)
8. [Security issues](#security-issues)

## Getting started

You will need:

- The **.NET 10 SDK**.
- A Git client. The repository uses a Git submodule (`build.net`), so clone recursively:

```powershell
git clone --recursive https://github.com/LumaCoreTech/OllamaProxy
```

If you already cloned without `--recursive`, initialize the submodule with
`git submodule update --init --recursive`.

To try the proxy locally, follow the **Quick start** in the [README](README.md) and the
[configuration guide](docs/configuration.md).

## Building and testing

All commands run from the repository root against the solution file `OllamaProxy.slnx`:

```powershell
dotnet restore OllamaProxy.slnx
dotnet build OllamaProxy.slnx -c Release
dotnet test OllamaProxy.slnx -c Release
```

A few things worth knowing:

- **Live backend tests self-skip.** Integration tests that need real backend credentials are gated on
  environment variables and are reported as *skipped* (never failed) when those variables are absent, so
  the default `dotnet test` run is safe without any keys.
- **The Windows installer is separate.** The WiX installer embeds a Windows-only `net472` custom action
  and is **not** part of `OllamaProxy.slnx`. Build it explicitly on Windows when your change touches it:
  `dotnet build installer/OllamaProxy.Installer.wixproj -c Release -p:Platform=x64`.
- **CI runs on Linux and Windows.** Please make sure changes behave on both — the most common cross-platform
  pitfalls are path handling and host-dependent APIs. See the badges in the README for current status.

## Coding conventions

The full, authoritative coding standards live in
[`.github/instructions/base.instructions.md`](.github/instructions/base.instructions.md) (copied from the
`build.net` submodule during the build. Highlights:

- **Target framework is .NET 10.** Match the existing code style of the surrounding code.
- **Keep changes minimal and focused** on the task at hand.
- **XML documentation** is expected on the members you add or change (including private/internal ones).
- **Async:** use `ConfigureAwait(false)` in library code; follow the test-project exceptions described in
  the standards.
- **Tests** follow the `Method_State_Expectation` naming pattern and the AAA layout, and aim for meaningful
  behavioral coverage (measured with Coverlet). New behavior should come with tests.
- **Localization** applies only to the Blazor UI; API responses and validation messages stay in English.

## Commit messages

This project follows the [**Conventional Commits**](https://www.conventionalcommits.org/) specification.

```text
<type>(<scope>): <subject>

<body>
```

- **Header** is mandatory (max 72 characters); **type** and **scope** are lowercase; **subject** starts with
  a capital letter and uses the imperative mood.
- Common **types**: `feat`, `fix`, `docs`, `refactor`, `test`, `perf`, `style`, `chore`, `revert`.
- Common **scopes**: `api`, `auth`, `core`, `data`, `ci`, `build`, `deps`, `docker`, `ui`, `docs`, and the
  others listed in the standards.
- Mark breaking changes with a `!` after the type/scope and a `BREAKING CHANGE:` footer.

Example:

```text
fix(ci): Exclude skipped tests from Tests badge verdict

The badge colored itself from a counter that treated skipped tests as
failures. Derive skipped from total - executed instead so gated-off
live tests no longer turn the badge red.
```

## Third-party dependencies

When you **add, remove, or update** a third-party dependency (NuGet package, npm package, bundled JS
library, or any other external asset), update
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) in the same change:

- **Adding:** insert a new entry with Author, License, and URL.
- **Removing:** delete the corresponding entry.
- **Updating:** adjust the entry if the license or author changed (a version bump with no license change
  needs no update).

## Pull requests

Before opening a PR:

1. **Build and test** the solution on your platform (`dotnet build` + `dotnet test`).
2. **Keep the diff focused.** One logical change per PR makes review faster; unrelated cleanups belong in
   their own PR.
3. **Update documentation** under [`docs/`](docs/) and the [`CHANGELOG.md`](CHANGELOG.md) `Unreleased`
   section when your change is user-visible.
4. **Describe the why.** A short explanation of the motivation and approach helps reviewers far more than a
   restatement of the diff.

PRs target the `main` branch and must pass the CI build/test matrix before merge.

## Reporting bugs and requesting features

Use the repository's [issue tracker](https://github.com/LumaCoreTech/OllamaProxy/issues). A good bug report
includes:

- What you expected to happen and what actually happened.
- Steps to reproduce (a minimal configuration and the client you pointed at the proxy).
- Version or commit, backend type, and relevant log output (with secrets redacted).

## Security issues

**Do not** report security vulnerabilities through public issues. Follow the process in
[`SECURITY.md`](SECURITY.md) instead.
