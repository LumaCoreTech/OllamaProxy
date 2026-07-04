# Third-Party Notices

OllamaProxy is licensed under the MIT License (see [LICENSE](LICENSE)).

OllamaProxy incorporates third-party libraries listed below. We are grateful to the authors
and contributors of these projects for making their work available to the open-source
community. Inclusion in this file is an attribution requirement of the respective upstream
license; it does **not** imply that the listed projects endorse OllamaProxy.

The file is organized into two parts:

1. **[Source-Adapted Code](#source-adapted-code)** — third-party source code that has been
   copied into the OllamaProxy repository and modified. These entries carry the full original
   license text, as required by the respective licenses.
2. **[Binary Dependencies](#binary-dependencies)** — third-party software consumed as
   compiled artifacts (NuGet packages). License texts for these are distributed by the
   upstream projects themselves; this section provides attribution and license identifiers.

---

## Source-Adapted Code

OllamaProxy does not currently vendor or adapt any third-party source code. All third-party
software is consumed as binary NuGet packages (see [Binary Dependencies](#binary-dependencies)).
Should adapted source be introduced in the future, it will be documented here together with the
full original license text.

---

## Binary Dependencies

For consumed NuGet packages, see also the package metadata in the respective project files
([`src/OllamaProxy/OllamaProxy.csproj`](src/OllamaProxy/OllamaProxy.csproj),
[`tests/OllamaProxy.Tests/OllamaProxy.Tests.csproj`](tests/OllamaProxy.Tests/OllamaProxy.Tests.csproj))
and the respective package licenses on [nuget.org](https://www.nuget.org/).

### NuGet Packages

#### coverlet.collector

- **Author:** Toni Solarin-Sodara and the coverlet-coverage Contributors
- **License:** MIT
- **URL:** <https://github.com/coverlet-coverage/coverlet>
- **Usage:** Test-only. Collects code coverage during `dotnet test`.

#### coverlet.msbuild

- **Author:** Toni Solarin-Sodara and the coverlet-coverage Contributors
- **License:** MIT
- **URL:** <https://github.com/coverlet-coverage/coverlet>
- **Usage:** Test-only. Provides MSBuild-integrated code coverage during `dotnet test`.

#### Microsoft.Extensions.Http.Resilience

- **Author:** Microsoft
- **License:** MIT
- **URL:** <https://github.com/dotnet/extensions>
- **Usage:** Runtime. Provides the standard resilience pipeline (retries, circuit breaker,
  timeouts) for the backend `HttpClient` instances.

#### Polly

- **Author:** App vNext
- **License:** BSD-3-Clause
- **URL:** <https://github.com/App-vNext/Polly>
- **Usage:** Runtime, transitive via `Microsoft.Extensions.Http.Resilience`. Underlying
  resilience strategy implementation.

#### xUnit.net (xunit, xunit.runner.visualstudio)

- **Author:** .NET Foundation and Contributors
- **License:** Apache-2.0
- **URL:** <https://github.com/xunit/xunit>
- **Usage:** Test-only. Unit-testing framework and Visual Studio / VSTest runner.

### .NET Platform

OllamaProxy is built on the [.NET platform](https://github.com/dotnet/runtime) by Microsoft,
licensed under the MIT License. The following Microsoft components are used throughout the
project and are listed here for completeness:

- Microsoft.NET.Sdk.Web (ASP.NET Core shared framework — Minimal API host, Kestrel, hosting,
  configuration, dependency injection, logging, `HttpClientFactory`)
- Microsoft.NET.Test.Sdk

All Microsoft packages are licensed under the **MIT License**.
See <https://github.com/dotnet/runtime/blob/main/LICENSE.TXT> for details.

---

## Installer Tooling

The following components are used only to **build the Windows MSI installer** (under
[`installer/`](installer/)). They are build-time tooling: they are **not** linked into, distributed
with, or required by the OllamaProxy application itself.

### WiX Toolset (v7)

- **Author:** WiX Toolset team and contributors (.NET Foundation)
- **License:** Microsoft Reciprocal License (MS-RPL)
- **URL:** <https://github.com/wixtoolset/wix>
- **Usage:** Build-time only. Compiles the MSI package (`WixToolset.Sdk`), the standard UI dialog
  set (`WixToolset.UI.wixext`), and the secured-folder / service-configuration helpers
  (`WixToolset.Util.wixext`). The managed custom actions are authored against the WiX DTF libraries
  (`WixToolset.Dtf.CustomAction`, `WixToolset.Dtf.WindowsInstaller`) and wrapped by MakeSfxCA.

> **Open Source Maintenance Fee (OSMF):** WiX v7 is distributed under the OSMF EULA. The fee applies
> only to users employing the toolset in revenue-generating activities with annual gross revenue at
> or above US$10,000; non-commercial use is exempt. See <https://wixtoolset.org/osmf/>.

### Microsoft.NETFramework.ReferenceAssemblies

- **Author:** Microsoft
- **License:** MIT
- **URL:** <https://github.com/microsoft/dotnet>
- **Usage:** Build-time only. Supplies the .NET Framework 4.7.2 reference assemblies the installer's
  custom-action project compiles against (the Windows Installer custom-action host runs on the .NET
  Framework).
