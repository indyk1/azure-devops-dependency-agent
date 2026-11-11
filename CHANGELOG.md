# Changelog

## 0.2.0 - 2026-11-11

- **Single project.** All target repositories are in one Azure DevOps project: the new top-level `project` of `config/repositories.json` (falls back to the pipeline's `SYSTEM_TEAMPROJECT`). Entries no longer need `project`; one that names a different project is an error. Duplicates are detected by repository name. `run --repository` accepts `Repo` or `Project/Repo`; matrix keys and entries use only the repository name (`{ "targetRepository": "<repo>" }`); the pipeline artifact is `report-<repo>`; `plan` prints the project once. "Limit job authorization scope to current project" can stay on.
- **AI defaults.** Copilot with Claude Sonnet 5.5 at high reasoning effort. New settings `ai.reasoningEffort`, `ai.copilot.model`, `ai.copilot.reasoningEffort` and `ai.openAi.reasoningEffort`; `validate-config` checks the effort values.
- **Roslynk MCP.** The AI can use [Roslynk](https://github.com/mrpmorris/Roslynk) 3.0.0 (installed and pinned by the pipeline) for compiler diagnostics, navigation and Roslyn code fixes, local only and without secrets. New `ai.roslynk` settings; a repository file can only turn it off. `validate-config` requires `stdio` in `ai.roslynk.arguments`.
- Schemas, documentation and tests updated.

## 0.1.0 - 2026-11-11

Initial version.

- Scheduled Azure Pipeline and .NET 10 CLI that upgrades configured repositories to the latest .NET LTS and opens one pull request per repository on `dependency-agent/net10-lts`.
- Deterministic updates: target framework, `global.json`, NuGet packages (latest stable, framework-aligned families, pins, major caps, ignore list) and Dockerfile `mcr.microsoft.com/dotnet/*` images.
- Verification: baseline and upgraded restore/build/test, "no new warnings" gate.
- Fixing: declarative rewrite rules and a configurable AI provider (GitHub Copilot CLI or OpenAI), with a sanitizing, audit-logging proxy to the Microsoft Learn MCP server.
- Guards: change budget, allowed edit patterns, protected paths, test-tampering detection, foreign-commit protection.
- Draft PRs when not green; PRs are never auto-merged. .NET Framework projects are reported, not migrated.
- JSON Schemas for all configuration files, including a repository's own `.dependency-agent.json`.
