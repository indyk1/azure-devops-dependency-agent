# Expected breaks after upgrading SampleLegacyApp to net10.0

Observed with .NET SDK 10.0.115 (`global.json` bumped to `10.0.100`, every csproj retargeted to `net10.0`, solution built with `dotnet build`). This file is for authors of end-to-end tests: it lists what the upgrade is expected to produce so that assertions can be precise. Re-check it after changing the fixture or the SDK.

## Baseline (net8.0, SDK 8.0.x)

| Code | Where | Notes |
|---|---|---|
| NU1903 (warning) | `SampleLegacyApp.Api` | `System.Text.Json 8.0.4` has a known high-severity vulnerability. Nothing else is reported; `dotnet test` passes (14 tests). |

## After the TFM bump only (old packages)

| Code | Severity | File | Cause | Documented replacement |
|---|---|---|---|---|
| SYSLIB0057 | error (Core has `TreatWarningsAsErrors`) | `src/SampleLegacyApp.Core/Certificates/CertificateReader.cs` (line 8) | `new X509Certificate2(byte[])` | `X509CertificateLoader.LoadCertificate(byte[])` |
| SYSLIB0060 | error | `src/SampleLegacyApp.Core/Hashing/PasswordHasher.cs` (line 16) | `new Rfc2898DeriveBytes(string, byte[], int, HashAlgorithmName)` | `Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeyLength)` |
| SYSLIB0060 | error | `src/SampleLegacyApp.Core/Security/LegacyTokenSigner.cs` (line 15) | same constructor, SHA1 variant | same; **inside `Security/`, a protected path, so fixers must not touch it** |
| NU1510 | warning | `src/SampleLegacyApp.Api/SampleLegacyApp.Api.csproj` | `System.Text.Json` is part of the net10.0 shared framework, the explicit `PackageReference` "will not be pruned" | remove the reference (or leave it and accept the warning) |
| NU1903 | warning | `SampleLegacyApp.Api` | vulnerable `System.Text.Json 8.0.4` (disappears when packages are updated) | update the package |

Because `SampleLegacyApp.Core` fails to compile, `SampleLegacyApp.Api` and the tests are not built at this stage (their diagnostics appear only once Core compiles).

## After TFM + package updates (Microsoft.Extensions.* and System.Text.Json 10.0.x)

Same three compile errors in Core, plus NU1510 in Api. NU1903 is gone. The package updates introduce no additional breaks.

## Once Core compiles (all three SYSLIB errors fixed)

Observed by applying the replacements above in a temp copy:

| Code | Severity | File | Cause |
|---|---|---|---|
| ASPDEPR008 | warning | `src/SampleLegacyApp.Api/Program.cs` (line 11, column 9) | `WebHost` is obsolete, use `HostBuilder` / `WebApplicationBuilder` |
| ASPDEPR008 | warning | `src/SampleLegacyApp.Api/Program.cs` (line 7, column 47) | `IWebHostBuilder.Build()` / `IWebHost` is obsolete, use `IHost` |
| NU1510 | warning | `SampleLegacyApp.Api.csproj` | see above |

`IWebHostBuilder` itself, `UseStartup<T>` and the classic `Startup` class compile without diagnostics (no ASPDEPR004 was observed). All 14 tests still pass on net10.0 with the replacements, i.e. the documented replacements are behaviour-identical for the pinned PBKDF2/HMAC vectors and certificate round-trips.

## What this means for an end-to-end test

- Deterministic updater alone leaves the repo **not building** (three SYSLIB errors): the run needs rule fixes or AI to go green.
- A rewrite rule (or AI) can fix `PasswordHasher.cs` and `CertificateReader.cs` (allowed paths). `LegacyTokenSigner.cs` lives under `Security/`: it must stay untouched, so the build stays red and the outcome is **Draft** (or **Failed** without a PR), with the guard violation recorded if a fixer attempted the edit.
- To exercise a **Green** outcome, delete `src/SampleLegacyApp.Core/Security/LegacyTokenSigner.cs` and `tests/SampleLegacyApp.Tests/LegacyTokenSignerTests.cs` from the temp copy first, or pre-fix them.
- `failOnNewWarnings` will report `ASPDEPR008` x2 and `NU1510` as new warnings unless fixed or the setting is disabled in the test's overrides.
- The repo's own `.dependency-agent.json` lowers `guards.maxChangedFiles` to 10 and adds `**/Legacy/**` to the protected patterns.
