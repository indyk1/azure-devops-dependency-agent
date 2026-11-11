# Troubleshooting

Start with the run summary and the repository's report (`out/<project>__<repo>.md`). The failure reasons and build output tail are there.

## Permissions and access

| Symptom | Cause and fix |
|---|---|
| `403` on clone or push, `TF401019`, "The Git repository ... does not exist or you do not have permissions" | The Build Service identity lacks permission on that repository. Grant Read, Contribute, Create branch, Contribute to pull requests to `<Project> Build Service (<Org>)` or `Project Collection Build Service (<Org>)` (whichever the pipeline runs as). See [setup.md](setup.md) section 4. |
| `404` or "repository not found" only for repositories in **another project** | "Limit job authorization scope to current project for non-release pipelines" is on. Turn it off (organisation and project) or use a service-account PAT. |
| 403/404 for repositories in the **same project** that are not declared in the YAML | "Limit job authorization scope to referenced Azure DevOps repositories" or "Protect access to repositories in YAML pipelines" is on. Turn them off or use a PAT. |
| PR creation fails with 403 but the push worked | Missing "Contribute to pull requests". |
| Push rejected (`stale info` / force-with-lease) | Someone pushed to the agent branch after the run fetched it. Re-run; if humans own the branch the repository is skipped (see below). |
| Policy error on push to the agent branch | A branch policy covers `dependency-agent/*`. Exclude that branch pattern from required-reviewer and build policies. |
| Repository `Skipped` with "foreign commits" | A human committed to `dependency-agent/net10-lts`. Merge or close the PR and delete the branch to let the agent start again, or rename `branchName` centrally. |

## AI providers

| Symptom | Cause and fix |
|---|---|
| "Copilot token missing" / provider unavailable; run is Draft with "AI unavailable" | `COPILOT_GITHUB_TOKEN` is not defined, not linked through the variable group, not marked for the pipeline or shows as the literal `$(COPILOT_GITHUB_TOKEN)`. Link `dependency-agent-secrets` to the pipeline and authorise it. |
| Copilot CLI starts but is unauthorised | The token must be a fine-grained PAT of a Copilot-licensed user with the **Copilot Requests** permission, and must not have expired. Classic tokens and plain `GITHUB_TOKEN` are not accepted. |
| Copilot executable not found | The CLI install step failed or `ai.copilot.executable` is wrong. Check the "install" step log. |
| OpenAI `429` / `insufficient_quota` | Quota or rate limit reached. Check billing and limits for the key; reduce `maxIterations` or the number of repositories per run; switch provider for the run with the pipeline parameter. |
| OpenAI `401` | `OPENAI_API_KEY` wrong or not exposed (see the `$(...)` note above). |
| AI changes were reverted | The edits touched files outside `allowedEditPatterns` or inside `protectedPatterns`. The report lists reverted files. Widen `allowedEditPatterns` only if appropriate; protected patterns are meant to stay protected. |
| No docs lookups in the audit log | `ai.learnMcp.enabled` is false, the endpoint is unreachable from the agent, or the proxy refused every query (see the audit log). |

## Restore and build

| Symptom | Cause and fix |
|---|---|
| `NU1301`, `401` or `Unable to load the service index` for a private feed | `NuGetAuthenticate` did not run before the agent, or the Build Service lacks **Feed Reader** on the feed, or the repo's `NuGet.config` does not list the feed. |
| `NU1102`/`NU1101` after the update | The latest package version is not on any configured feed (internal mirror lag). Use `packages.pins` or `maxMajor` for the package, or `ignore` it. |
| `A compatible .NET SDK was not found` / global.json SDK not found | The target repository pins an SDK version the agent has not installed, or the updated `global.json` requires the .NET 10 SDK. Make sure the pipeline installs the target SDK (and the old SDK for the baseline build). |
| Baseline build fails before any change | The repository does not build on its own (missing private feed, workloads, native toolchain). The agent records this and cannot attribute failures to the upgrade; fix the repo or the pipeline prerequisites first. |
| "Cannot determine solution to build" | Several solutions or none at the root. Set `verification.solution` in the repository's `overrides`. |
| Workloads required (MAUI, WASM, WebAssembly) | Not installed on hosted agents. Add an install step, or disable the repository (`enabled: false`). |
| `dotnet test` needs databases, emulators or secrets | Tests run with no secrets by design. Exclude them: `verification.testArguments: ["--filter", "Category!=Integration"]`. |

## Timeouts

| Symptom | Fix |
|---|---|
| `Build timed out` / `Test timed out`, process killed | Raise `verification.buildTimeoutMinutes` / `testTimeoutMinutes` for that repository, or filter slow tests. |
| AI provider timed out | Raise `ai.timeoutMinutes` or lower `maxIterations`; very large repositories may not be good candidates for AI. |
| Whole job hits the pipeline timeout | Too many repositories in one run. Use rings (`repositories: ring:a`) in separate scheduled runs, or raise the job `timeoutInMinutes` (maximum 60 minutes for Microsoft-hosted agents on a free tier; 360 minutes otherwise). |

## Why is my PR a draft?

A PR is created as a draft whenever the run was not fully green. The report's **Failure reasons** says which:

- the **build** failed after fixes (remaining compiler errors or analyzer errors);
- **tests** failed (including tests that failed in the baseline);
- a **guard** was violated (too many files or lines changed, edits to protected or disallowed files were reverted and the build still needs them);
- **new warnings** appeared compared with the baseline (`failOnNewWarnings`);
- **AI was needed but unavailable or disabled** for that repository;
- the **baseline build was already red**;
- .NET Framework or unsupported projects were found and left untouched (informational, not necessarily a failure).

Fix the cause (or fix the code manually on the agent branch; the agent will skip the repository afterwards because of the human commit) and mark the PR ready when satisfied. The next scheduled run keeps the PR up to date only while no human has committed to the branch.

## Where to look

- Pipeline run > the CLI step log (masked, with `##vso[` neutralised).
- Artifact `out`: `<project>__<repo>.md`/`.json`, run summary, Learn audit logs.
- The PR comment holds the same full report.
