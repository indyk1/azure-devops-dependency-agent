# Test fixtures

## SampleLegacyApp

A small, realistic .NET 8 repository used by the end-to-end test: the test copies it into a temporary git repository, runs the agent against it and asserts on the result. It is **not** part of `DependencyAgent.slnx` and must never be built as part of the main solution.

| Path | Purpose |
|---|---|
| `global.json` | SDK `8.0.100`, `rollForward: latestFeature` - the agent should bump it |
| `Directory.Build.props`, `Directory.Packages.props` | Standalone (stops MSBuild picking up the repository's own files, which enforce `net10.0` and `TreatWarningsAsErrors`). Central package management with deliberately old versions |
| `NuGet.config` | nuget.org only (`<clear/>`) |
| `SampleLegacyApp.sln` | Classic `.sln` because the .NET 8 SDK cannot read `.slnx` |
| `src/SampleLegacyApp.Core` | Class library, `TreatWarningsAsErrors=true`. Uses `Rfc2898DeriveBytes` constructors (SYSLIB0060) and `new X509Certificate2(byte[])` (SYSLIB0057), which are obsolete on .NET 9/10 and have documented, behaviour-identical replacements. `Security/LegacyTokenSigner.cs` uses an obsolete API inside a protected folder to exercise the guards |
| `src/SampleLegacyApp.Api` | Minimal `Microsoft.NET.Sdk.Web` app using `WebHost.CreateDefaultBuilder` (ASPDEPR008) and an old, vulnerable `System.Text.Json` reference |
| `tests/SampleLegacyApp.Tests` | xunit tests that pin current behaviour (PBKDF2 and HMAC known vectors, certificate round trip using an in-test self-signed certificate, serialiser output) so a behaviour change is detected |
| `Dockerfile` | `mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim` / `aspnet:8.0-bookworm-slim` |
| `.dependency-agent.json` | Example per-repository override (lower file limit, extra protected pattern, extra Learn deny term) |
| `EXPECTED-BREAKS.md` | Diagnostics observed after upgrading to net10.0 (codes, files) |

Run it on the baseline:

```bash
cd tests/fixtures/SampleLegacyApp
dotnet test          # 14 tests on net8.0 (needs the .NET 8 SDK and runtime)
```

Copy it somewhere outside this repository before running the agent against it. Never run the agent inside this directory, because git would treat it as part of the dependency-agent repository. Exclude `bin/` and `obj/` when copying.
