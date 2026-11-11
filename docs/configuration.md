# Configuration

Configuration lives in `config/` (central) and optionally in each target repository (`.dependency-agent.json`). All files are JSON with comments and trailing commas allowed, and all have JSON Schemas in `schemas/` (add `"$schema": "../schemas/defaults.schema.json"` for editor completion).

| File | Schema | Purpose |
|---|---|---|
| `config/defaults.json` | `schemas/defaults.schema.json` | Defaults for every repository |
| `config/repositories.json` | `schemas/repositories.schema.json` | Inventory and per-repository overrides |
| `config/rewrite-rules.json` | `schemas/rewrite-rules.schema.json` | Declarative rewrite rules |
| `<repo>/.dependency-agent.json` | `schemas/repo-override.schema.json` | Optional per-repository tightening |

Property names are camelCase and matched case-insensitively. Omitted properties take the built-in default shown below.

## Settings reference (`defaults.json`)

### Top level

| Setting | Default | Meaning |
|---|---|---|
| `targetFramework` | `net10.0` | Target framework moniker for modern .NET projects. |
| `branchName` | `dependency-agent/net10-lts` | Branch the agent owns in each target repository. |
| `gitIdentity.name` / `.email` | `Dependency Agent` / `dependency-agent@noreply.local` | Author and committer of agent commits. Also how the agent recognises its own commits. |

### `packages`

| Setting | Default | Meaning |
|---|---|---|
| `enabled` | `true` | Update NuGet packages at all. |
| `trackTargetMajorFor` | `Microsoft.AspNetCore.*`, `Microsoft.EntityFrameworkCore*`, `Microsoft.Extensions.*`, `System.*`, `Microsoft.Data.Sqlite*` | Package-id globs whose major version must track the target framework major (10.x for `net10.0`), when such a version exists. Everything else gets the latest stable. |
| `allowPrerelease` | `false` | Allow prerelease versions. |
| `pins` | `{}` | `package id -> exact version`. Pinned packages are set to that version and never moved. |
| `maxMajor` | `{}` | `package id glob -> highest allowed major`. |
| `ignore` | `[]` | Package-id globs never touched. |

### `docker`

| Setting | Default | Meaning |
|---|---|---|
| `enabled` | `true` | Update `mcr.microsoft.com/dotnet/*` image tags to the target version. |
| `filePatterns` | `**/Dockerfile`, `**/Dockerfile.*`, `**/*.Dockerfile`, `**/*.dockerfile` | Globs identifying Dockerfiles. |

### `verification`

| Setting | Default | Meaning |
|---|---|---|
| `solution` | `null` | Solution or project to build, repo-relative. `null` = auto-detect: a single `.slnx`/`.sln` at the root (`.slnx` wins when both share a name), else a single one within three levels, else a single project, else each project individually. |
| `runTests` | `true` | Run `dotnet test` after a successful build. |
| `failOnNewWarnings` | `true` | The run is not green (draft PR) if the upgraded build has warnings absent from the baseline build. |
| `buildArguments` | `[]` | Extra `dotnet build` arguments. |
| `testArguments` | `[]` | Extra `dotnet test` arguments, for example `["--filter", "Category!=Integration"]`. |
| `buildTimeoutMinutes` / `testTimeoutMinutes` | `20` / `30` | Timeouts; the process tree is killed on expiry. |

### `guards`

| Setting | Default | Meaning |
|---|---|---|
| `maxChangedFiles` | `40` | Maximum files changed by code fixes (rule-based and AI). |
| `maxChangedLines` | `1500` | Maximum added plus removed lines from code fixes. |
| `budgetExcludePatterns` | project files, `Directory.Packages.props`, `Directory.Build.props`, `global.json`, `packages.lock.json`, Dockerfiles | Manifest files updated by the deterministic updater; they do not count against the budget. |
| `allowedEditPatterns` | `**/*.cs`, `**/*.csproj`, `**/*.props`, `**/*.targets`, `**/*.razor`, `**/*.cshtml` | What code fixers may edit. Anything else is reverted. |
| `protectedPatterns` | `.git/**`, `**/Authentication/**`, `**/Authorization/**`, `**/Cryptography/**`, `**/Security/**`, `**/*.pfx`, `**/*.snk`, `**/appsettings*.json`, `**/azure-pipelines*.yml`, `**/.dependency-agent.json` | What code fixers may never edit. Takes precedence over the allowed list. |

### `ruleFixes`

| Setting | Default | Meaning |
|---|---|---|
| `enabled` | `true` | Enable rule-based fixes. |
| `formatDiagnostics` | `[]` | Diagnostic ids passed to `dotnet format analyzers --diagnostics` when they appear in the build output. |
| `rewriteRulesFile` | `rewrite-rules.json` | Rewrite rules file, relative to the config directory. |

### `ai`

| Setting | Default | Meaning |
|---|---|---|
| `enabled` | `true` | Allow AI fixing. |
| `provider` | `copilot` | `copilot`, `openai` or `none`. Overridable per run through the pipeline parameter. |
| `model` | `null` | Model for any provider; `null` = the provider's own default (`copilot.model`, `openAi.defaultModel`). |
| `reasoningEffort` | `null` | Reasoning effort for any provider: `none`, `minimal`, `low`, `medium`, `high`, `xhigh`, `max`; `null` = the provider's own default (`copilot.reasoningEffort`, `openAi.reasoningEffort`). |
| `maxIterations` | `3` | Maximum build-then-fix iterations. |
| `timeoutMinutes` | `20` | Wall-clock limit for one provider invocation. |
| `copilot.executable` | `copilot` | GitHub Copilot CLI executable. |
| `copilot.tokenVariable` | `COPILOT_GITHUB_TOKEN` | Environment variable with the token. |
| `copilot.model` | `claude-sonnet-5.5` | Copilot model id used when `ai.model` is `null`. Confirm the id in the Copilot CLI model picker (`/model`) for the token's account and change it here if it differs. |
| `copilot.reasoningEffort` | `high` | Passed to the Copilot CLI (`--reasoning-effort`) when `ai.reasoningEffort` is `null`. `null` = Copilot default. |
| `openAi.endpoint` | `null` | Base URL for OpenAI-compatible endpoints; `null` = api.openai.com. |
| `openAi.apiKeyVariable` | `OPENAI_API_KEY` | Environment variable with the API key. |
| `openAi.defaultModel` | `gpt-5.1` | Model when `ai.model` is `null`. |
| `openAi.reasoningEffort` | `null` | Reasoning effort for OpenAI reasoning models when `ai.reasoningEffort` is `null`; `null` = model default. |
| `openAi.maxToolTurns` | `40` | Tool-calling turns per iteration. |
| `learnMcp.enabled` | `true` | Allow Microsoft Learn lookups through the sanitizing proxy. |
| `learnMcp.endpoint` | `https://learn.microsoft.com/api/mcp` | Learn MCP endpoint. |
| `learnMcp.maxQueryLength` | `200` | Maximum length of a sanitized query. |
| `learnMcp.denyTerms` | `[]` | Case-insensitive terms that must never leave the agent (internal product or company names). |
| `roslynk.enabled` | `true` | Offer the [Roslynk](https://github.com/mrpmorris/Roslynk) MCP server (MIT) to the AI: compiler diagnostics (`get_diagnostics`), navigation and references, Roslyn code fixes and refactorings. A repository file can only turn it off. |
| `roslynk.command` | `roslynk` | Executable of the installed .NET tool (the pipeline installs `Roslynk` 3.0.0 globally and puts `~/.dotnet/tools` on `PATH`). |
| `roslynk.arguments` | `["stdio"]` | Arguments; must contain `stdio` (without it the tool starts as an HTTP daemon instead of an MCP stdio server). `validate-config` checks this. |
| `roslynk.serverName` | `roslynk` | MCP server name exposed to the AI. |
| `roslynk.allowWriteTools` | `true` | Allow Roslynk's write tools (code fixes, refactorings, rename). Edits made through them are still subject to the guards (allowed/protected patterns, budget, test-tampering detection). |

**Model and effort precedence.** The effective model is `ai.model` if set, otherwise the provider's setting (`ai.copilot.model` for Copilot, `ai.openAi.defaultModel` for OpenAI). The effective reasoning effort is `ai.reasoningEffort` if set, otherwise `ai.copilot.reasoningEffort` / `ai.openAi.reasoningEffort`. Because `ai.model` and `ai.reasoningEffort` apply to whichever provider is active, leave them `null` when you switch providers per run, and set provider-specific values in `copilot.*` / `openAi.*` instead. To change the Copilot model, set `ai.copilot.model` (confirm the id with `/model` in the Copilot CLI for the token's account); a central `overrides` block can do this per repository.

Roslynk starts a loopback-only daemon on `localhost:6502` on demand and evaluates/compiles the solution like a build, so it runs without secrets. The agent removes `OTEL_*` variables so no telemetry is exported.

### `pullRequest`

| Setting | Default | Meaning |
|---|---|---|
| `titlePrefix` | `[dependency-agent]` | Prefix of the PR title. |
| `reviewers` | `[]` | Optional reviewers (unique names or group descriptors). |
| `workItemIds` | `[]` | Work items linked to the PR. |
| `labels` | `["dependency-agent"]` | PR labels. |

## `repositories.json`

All target repositories live in **one Azure DevOps project**: the top-level `project`. When it is omitted, the pipeline's own project (`SYSTEM_TEAMPROJECT`) is used; if neither exists loading fails.

```json
{
  "project": "Engineering",
  "repositories": [
    {
      "repository": "ExampleService",
      "baseBranch": "main",
      "enabled": true,
      "ring": "pilot",
      "owners": ["team-example"],
      "overrides": { "ai": { "provider": "openai" } }
    }
  ]
}
```

| Field | Required | Default | Meaning |
|---|---|---|---|
| `project` | no | top-level `project` | Deprecated. If present it must equal the top-level project (case-insensitive); naming a different project is an error. |
| `repository` | yes | | Repository name. Must be unique (case-insensitive) or loading fails. |
| `baseBranch` | no | `main` | Branch the PR targets. |
| `enabled` | no | `true` | Kill switch. Disabled entries are never processed, whatever the filter says. |
| `ring` | no | none | Rollout label usable in the run filter. |
| `owners` | no | `[]` | Informational. |
| `overrides` | no | none | Partial settings, highest precedence. |

### Filter syntax

The pipeline's `repositories` parameter selects entries. `*` or empty selects all enabled entries. Otherwise a list separated by commas, semicolons or newlines, each token being OR-ed:

| Token | Matches |
|---|---|
| `Repo` | The repository name (case-insensitive). |
| `Project/Repo` | Same, with the project spelled out (it must be the inventory's project). |
| `Data/*`, `*/Api`, `Pay*` | Globs (`*`, `?`) on the repository name (or on `Project/Repo` when the token has a `/`). |
| `ring:pilot`, `ring:b*` | Entries whose `ring` matches. |

Example: `ring:pilot, Etl`. The CLI's `run --repository` takes `Repo` or `Project/Repo`.

## Per-repository file `.dependency-agent.json`

A team can put this file at the root of its repository. It can only make the agent **stricter**; the agent never edits it (it is a protected path).

| Key | Behaviour |
|---|---|
| `guards.protectedPatterns` | Unioned with the central list. Cannot remove entries. |
| `guards.maxChangedFiles`, `guards.maxChangedLines` | The lower of central and repo wins. |
| `ai.enabled` | Can only turn AI **off**. `true` cannot enable what is disabled centrally. |
| `ai.learnMcp.enabled` | Can only turn lookups off. |
| `ai.learnMcp.denyTerms` | Unioned with the central terms. |
| `ai.roslynk.enabled` | Can only turn Roslynk off. Other `ai.roslynk.*` keys (`command`, `arguments`, `serverName`, `allowWriteTools`) are ignored. |
| `packages.*`, `docker.*`, `verification.*`, `ruleFixes.*`, `pullRequest.*`, `ai.model`, `ai.reasoningEffort`, `ai.maxIterations`, `ai.timeoutMinutes`, `guards.allowedEditPatterns`, `guards.budgetExcludePatterns` | Override the defaults. |
| `ai.provider`, `ai.openAi`, `ai.copilot` (including model and reasoning effort), `ai.learnMcp.endpoint`, `gitIdentity`, `branchName` | **Ignored.** Credentials, endpoints and identity are controlled centrally only. |

### Precedence

`defaults.json` -> repo `.dependency-agent.json` -> central `overrides` in `repositories.json` (highest). Objects merge recursively; arrays and scalars replace. The safety rules above are then applied so a repository file can never loosen a central control.

Safety settings are computed explicitly rather than by plain merging, so a central `overrides` block cannot mask a repository's stricter value: `guards.protectedPatterns` and `ai.learnMcp.denyTerms` are the union of `defaults.json`, central `overrides` and the repo file (a central override adds to the defaults, it never replaces them); `guards.maxChangedFiles`/`maxChangedLines` take the lower of the central value and the repo file; `ai.enabled`/`ai.learnMcp.enabled`/`ai.roslynk.enabled` are the central value AND the repo file. All key matching, including the "ignored keys" list, is case-insensitive.

## Package policy examples

```json
{
  "packages": {
    "pins": { "Newtonsoft.Json": "13.0.3" },
    "maxMajor": { "AutoMapper": 14, "FluentAssertions": 7 },
    "ignore": ["Legacy.Internal.*"],
    "trackTargetMajorFor": ["Microsoft.Extensions.*", "System.*"],
    "allowPrerelease": false
  }
}
```

- A pin sets the exact version and the package is then never moved.
- `maxMajor` keeps a package below a breaking major (useful for licence changes or API removals) while still taking the latest within that major.
- `ignore` leaves a package entirely alone.
- `trackTargetMajorFor` aligns framework-bound families with the TFM. Packages in that list are moved to the target major (for example `Microsoft.Extensions.Hosting` 8.0.0 to 10.x) only if that major exists.

## Rewrite rules

`config/rewrite-rules.json` holds declarative regex rewrites that run before any AI call. They are deterministic, cheap and reviewable, so use them for well-known, behaviour-identical API replacements.

```json
{
  "rules": [
    {
      "id": "pbkdf2-static",
      "enabled": true,
      "description": "Rfc2898DeriveBytes constructor + GetBytes -> static Pbkdf2 (SYSLIB0060)",
      "diagnostics": ["SYSLIB0060"],
      "filePattern": "**/*.cs",
      "find": "(?s)using var (\\w+) = new Rfc2898DeriveBytes\\((.+?), (.+?), (.+?), (HashAlgorithmName\\.\\w+)\\);\\s*return Convert\\.ToHexString\\(\\1\\.GetBytes\\((.+?)\\)\\);",
      "replace": "return Convert.ToHexString(Rfc2898DeriveBytes.Pbkdf2($2, $3, $4, $5, $6));",
      "docs": "https://learn.microsoft.com/dotnet/fundamentals/syslib-diagnostics/syslib0060"
    }
  ]
}
```

| Field | Meaning |
|---|---|
| `id` | Unique id, shown in the report. |
| `enabled` | Disabled rules never run. |
| `diagnostics` | Rule applies only when one of these ids appears in the failing build. |
| `filePattern` | Glob of files it may edit; further restricted to files the diagnostic points at, and always subject to the allowed and protected patterns. |
| `find` / `replace` | .NET regular expression and replacement (`$1` group references). |
| `docs` | Link included in the PR report. |

Every rewrite is still subject to the guards, and the build is re-run afterwards; a rule that breaks the build is reported, not hidden.
