# Changelog

## 0.1.0 - 2026-11-11

Initial version.

- Scheduled Azure Pipeline and .NET 10 CLI that upgrades configured repositories to the latest .NET LTS and opens one pull request per repository on `dependency-agent/net10-lts`.
- Deterministic updates: target framework, `global.json`, NuGet packages (latest stable, framework-aligned families, pins, major caps, ignore list) and Dockerfile `mcr.microsoft.com/dotnet/*` images.
- Verification: baseline and upgraded restore/build/test, "no new warnings" gate.
- Fixing: declarative rewrite rules and a configurable AI provider (GitHub Copilot CLI or OpenAI), with a sanitizing, audit-logging proxy to the Microsoft Learn MCP server.
- Guards: change budget, allowed edit patterns, protected paths, test-tampering detection, foreign-commit protection.
- Draft PRs when not green; PRs are never auto-merged. .NET Framework projects are reported, not migrated.
- JSON Schemas for all configuration files, including a repository's own `.dependency-agent.json`.
