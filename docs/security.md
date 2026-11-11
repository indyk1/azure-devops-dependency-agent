# Security

The agent runs unattended, holds credentials, edits repositories and sends code to an AI provider. This page states what leaves the agent, how credentials are scoped and what is not mitigated.

## Data flow

| Destination | What is sent | When | Controlled by |
|---|---|---|---|
| NuGet feeds (nuget.org, private Artifacts feeds) | Package id and version queries; feed credentials for private feeds | Restore and package updates | The target repo's `NuGet.config` |
| Microsoft Container Registry (`mcr.microsoft.com`) | Tag metadata queries | Docker image updates | `docker.enabled` |
| AI provider (GitHub Copilot, default model Claude Sonnet 5.5, or OpenAI) | **Source code and build output of repositories where AI is enabled**: files the model reads, failing diagnostics, the task prompt | Only when a repository needs fixing and `ai.enabled` is true and the provider is not `none` | `ai.enabled` (central and per repo), `ai.provider`, per-repository overrides |
| Microsoft Learn MCP server | **Sanitized queries only**: public API names and diagnostic codes, capped at `ai.learnMcp.maxQueryLength`, filtered against `denyTerms`. No code, paths, identifiers from the repository or free text | When the AI looks up documentation | `ai.learnMcp.enabled`, `denyTerms`. Every outbound query is written to an audit log in `out/` |
| Roslynk MCP server (local) | **Nothing leaves the machine.** It is a local Roslyn process spoken to over stdio; its `stdio` bridge talks to a daemon that listens on loopback only (`localhost:6502`) and is started on demand. It exports OpenTelemetry only if an OTLP endpoint is configured, and the agent removes all `OTEL_*` variables | When the AI asks for diagnostics, navigation or code fixes | `ai.roslynk.enabled` (a repository can only turn it off), `ai.roslynk.allowWriteTools` |
| Azure DevOps (Azure Repos) | Clone and fetch, the agent branch, PR create/update, report comment | Every run (not in dry run) | Build Service permissions, `dryRun` |

Nothing else is sent. There is no telemetry (`DOTNET_CLI_TELEMETRY_OPTOUT=1` is set for child processes).

For sensitive repositories set `"ai": { "enabled": false }` in their `overrides` (or let the team do it in `.dependency-agent.json`). The deterministic updater and rewrite rules then still run, with no code leaving the agent.

## Secret scoping

Each child process gets only the credential category its job needs. Anything whose name is, or looks like, a credential (`*TOKEN`, `*_PAT`, `*_KEY`, `*SECRET`, `*PASSWORD`, `*CONNECTIONSTRING`, `*CREDENTIALS` and a set of known names) is removed from the environment unless the scope allows it.

| Process | Scope | Receives |
|---|---|---|
| `git clone/fetch/push` | Azure DevOps | The job token, passed through per-command configuration (`http.extraheader`-style) for that command only. It is never written to `.git/config`, a credential helper or disk, and is masked in captured output. |
| `dotnet restore`, package metadata queries | NuGet feeds | `VSS_NUGET_*`, `ARTIFACTS_CREDENTIALPROVIDER_*` and similar feed credentials only |
| `dotnet build`, `dotnet test`, `dotnet format` | None | No secrets at all, because they execute repository code (analyzers, MSBuild targets, tests) |
| AI provider process (Copilot CLI) / in-process OpenAI client | AI provider | Only its own token (`COPILOT_GITHUB_TOKEN` or `OPENAI_API_KEY`). It never sees `SYSTEM_ACCESSTOKEN` or feed credentials |
| Roslynk MCP server | None | No secrets and no `OTEL_*` variables. Like a build, it evaluates and compiles the repository's projects (MSBuild targets, analyzers, source generators), so it must run without credentials |

Rules that cut across the table:

- The Azure DevOps token is never in the AI process environment, so a prompt-injected model cannot use it.
- `$(NAME)` (the text Azure Pipelines passes for an undefined variable) is treated as "not set".
- Tokens passed as arguments are masked as `***` in captured output and logs.

## What the AI can and cannot do

- **No repository code is executed by the AI process.** The AI is not given a shell tool. The Copilot CLI is started with file read/edit tools only; shell and network tools are denied. The OpenAI provider is an in-process tool loop that exposes only: read/search files, write files within the allowed patterns, a docs lookup via the sanitizing proxy and a "run build" tool that the agent itself executes without secrets.
- Edits are applied to the working tree and then validated by the change guard: files outside `allowedEditPatterns` and anything in `protectedPatterns` are **reverted**; if the change budget is exceeded the fix is rejected.
- **Test tampering is detected.** Fixes that delete or weaken tests (removed test files, removed `[Fact]`/`[Test]` attributes, added skips or `Assert` removals) are flagged and reverted, so a "green" result cannot be obtained by making the tests pass vacuously.
- Documentation lookups go through the local sanitizing proxy, never directly to the Learn endpoint.
- **Roslynk** gives the AI compiler diagnostics, navigation and Roslyn code fixes/refactorings. Its write tools edit the working tree like the file tools do, so the same guards apply afterwards (allowed/protected patterns, change budget, test-tampering detection). Because Roslynk evaluates and compiles repository projects the way a build does, repository code (analyzers, generators, MSBuild targets) can execute inside it; it therefore runs with no secrets, exactly like `dotnet build`. Set `ai.roslynk.allowWriteTools` to `false` to make it read-only, or `ai.roslynk.enabled` to `false` to switch it off (a repository file can do the latter for itself).

## Other controls

- **Logging-command neutralisation.** All captured child output has `##vso[` and `##[` prefixes broken, so repository code or tool output cannot set pipeline variables, upload files or alter the run through Azure Pipelines logging commands.
- **Git hooks disabled.** Clones run with hooks disabled (`core.hooksPath` pointed at an empty location), so a repository cannot execute hook scripts on the agent through the agent's own git commands. Submodule and filter configuration from the repository is not trusted for credentials.
- **Foreign-commit protection.** If anyone other than the agent identity has pushed commits to the agent branch, the agent does not overwrite it: the repository is reported as `Skipped`. Pushes use force-with-lease so a concurrent push is never clobbered.
- **One PR per repository, never merged.** The agent only creates or updates its PR and has no completion or approval logic.
- **Budget and scope.** `maxChangedFiles`, `maxChangedLines`, `allowedEditPatterns`, `protectedPatterns` are enforced after every fixer.
- **Per-repo override cannot loosen controls.** A repository's `.dependency-agent.json` can only add protections, lower limits or turn AI off. Provider, credentials, endpoints, git identity and branch name come from central configuration only.
- **Audit trail.** The run artifact contains per-repository reports and the Learn query audit log.

## Recommended branch policies

On every target repository's base branch:

- minimum one human reviewer (the agent's PR author is the Build Service identity, so any human is independent);
- build validation with the repository's own CI;
- comment resolution required, linked work items as per your standard;
- no bypass permissions for the Build Service identity;
- code owners / required reviewers for sensitive paths (the agent already will not edit them, but a human should confirm nothing adjacent changed).

Review `Directory.Build.props`, `*.csproj`, `Directory.Packages.props`, `global.json` and Dockerfile changes in the PR as carefully as code: build files execute during CI.

## Residual risks

- **The AI provider sees code.** For every repository with AI enabled, source files and build output are sent to GitHub Copilot or OpenAI under that provider's terms. Disable AI for repositories that must not leave your boundary.
- **Build and test execute repository code on the agent**, exactly as any CI does. Secrets are scrubbed from those processes, but the code runs on a Microsoft-hosted agent with network access during the job. Updating packages can also pull in new build-time code (analyzers, MSBuild targets, source generators), which then runs in the following build. Review package changes.
- **Prompt injection.** Content in the repository (comments, strings, markdown, package READMEs) can try to steer the model. This is mitigated by the absence of a shell tool, token scoping, edit/protected-path guards, reverts and test-tampering detection, but not eliminated: the model can still make a wrong or malicious-looking edit inside the allowed files, which is why a human reviews every PR.
- **Broad pipeline identity.** The Build Service identity can write to every target repository you grant it. Keep the grant to the target list and restrict who can edit this repository and pipeline. See the trade-off discussion in [setup.md](setup.md).
- **Learn lookups leak intent, not code.** Sanitised queries reveal that some repository uses certain public APIs. Use `denyTerms` for any internal name that could collide with a public-looking identifier.
- **Variable group secrets** are readable by anyone who can edit the pipeline.
