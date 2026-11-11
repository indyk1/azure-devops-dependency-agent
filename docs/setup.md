# Setup

Everything below is done once, by someone with Project Collection Administrator (or equivalent) rights for the organisation settings and Project Administrator for each target project.

## 1. Import the repository

Import this repository into Azure Repos (Repos > Files > Import repository), in any project. It is the "central" repository: it holds the pipeline, the CLI and the configuration. Target repositories are never modified except through the agent branch and PR.

## 2. Create the pipeline

1. Pipelines > New pipeline > Azure Repos Git > this repository > **Existing Azure Pipelines YAML file** > `/azure-pipelines.yml`.
2. Save (do not run yet).

The pipeline runs on Microsoft-hosted `ubuntu-latest` agents and authenticates to Azure Repos with the job access token (`System.AccessToken`), i.e. the pipeline's **Build Service identity**.

## 3. Variable group and secrets

Create a variable group named `dependency-agent-secrets` (Pipelines > Library):

| Variable | Secret | Value |
|---|---|---|
| `COPILOT_GITHUB_TOKEN` | yes | Fine-grained GitHub personal access token of a **Copilot-licensed user**, with the **Copilot Requests** account permission. Used by the GitHub Copilot CLI. |
| `OPENAI_API_KEY` | yes | OpenAI API key. Used when `ai.provider` is `openai`. |

Set only what you use. Link the group to the pipeline (Pipeline > Edit > Variables > Variable groups, or authorise it when prompted at first run). Consider restricting who can edit the group and the pipeline: anyone who can edit the pipeline can read the secrets.

Variable names are configurable (`ai.copilot.tokenVariable`, `ai.openAi.apiKeyVariable`), see [configuration.md](configuration.md). An undefined variable reaches the process as the literal text `$(NAME)`; the agent treats that as "not set".

## 4. Permissions on every target repository

The Build Service identity must be able to read, push a branch and raise a PR in each target repository. On each target repo (Project settings > Repositories > the repo > Security) set to **Allow** for the identity:

- Read
- Contribute
- Create branch
- Contribute to pull requests

Which identity? By default a pipeline runs as the **project-scoped** build identity named `<Project> Build Service (<Organisation>)`, where `<Project>` is the project that contains the pipeline (this repository's project). If "Limit job authorization scope to current project" is turned off (see below) it runs as the **collection-scoped** identity `Project Collection Build Service (<Organisation>)`. Grant the permissions to the one you actually run as; search for both names in the security dialog and check which one applies by looking at the identity shown in a failing 403 message. For cross-project targets, add the identity to each target project's repo security (the identity is a user in the organisation, so it can be added in any project).

Branch policy on the **agent branch** (`dependency-agent/*`) must not block the agent's force-push. Do not apply "Require a minimum number of reviewers" to `dependency-agent/*`; apply it to the base branches instead (see section 7).

## 5. Organisation and project settings

The agent clones the repositories listed in `config/repositories.json`, which are not declared as `resources` in the YAML. Two security features block that by default.

| Setting (Organisation settings > Pipelines > Settings; also per project) | Required value | Why |
|---|---|---|
| **Limit job authorization scope to current project for non-release pipelines** | **Off**, if any target repo is in a different project from the pipeline | The job token otherwise cannot read other projects. |
| **Limit job authorization scope to referenced Azure DevOps repositories** | **Off** | With it on, the job token can only access repositories the YAML declares, so cloning anything else fails with 403/404. |
| **Protect access to repositories in YAML pipelines** | **Off** | Related repository-access gate; with it on, undeclared repositories are inaccessible. |

Trade-off: turning these off lets every pipeline in the project (organisation, for the collection-level switch) use its Build Service identity on all repositories that identity is permitted to access. Mitigate by:

- granting the Build Service identity permissions **only** on the target repositories (it is a deny-by-default model; the settings above do not grant anything by themselves);
- restricting who can edit and run this pipeline and who can change this repository (branch policy with required reviewers on `main` here);
- running the pipeline in a dedicated project with only the minimum other pipelines.

**Alternative: a service-account PAT.** If you do not want to relax these settings, create a dedicated service account with only the four permissions above on the target repositories, create a PAT with scope Code (Read & write) and Pull Request Threads (Read & write), store it as a secret variable in the group (for example `ADO_PAT`) and configure the pipeline to pass it instead of `System.AccessToken`. The agent accepts the credential through the variables listed in `SecretEnvironment.AzureDevOpsVariables` (`SYSTEM_ACCESSTOKEN`, `AZURE_DEVOPS_EXT_PAT`, `ADO_PAT`, `AZURE_DEVOPS_TOKEN`). The downside is a long-lived secret that must be rotated; PRs will be created by that account rather than the build identity.

## 6. Private Azure Artifacts feeds

If target repositories restore from private Azure Artifacts feeds:

1. The pipeline runs the `NuGetAuthenticate@1` task before the agent, which sets the credential provider variables (for example `VSS_NUGET_EXTERNAL_FEED_ENDPOINTS`). Only the `dotnet restore` process and package-metadata queries receive them; build and test never do.
2. The Build Service identity needs **Feed Reader** (or higher) on each feed (Artifacts > feed > Settings > Permissions). Project-scoped feeds in another project also need the identity added there.
3. Each target repository must declare the feed in its own `NuGet.config`. The agent does not add sources.

Feeds on other hosts (external registries) need their own credentials; add them to the variable group and `NuGetAuthenticate`/`NUGET_*` configuration, and allow the variable names through `SecretEnvironment.NuGetVariables`.

## 7. Branch policies on target repositories

Recommended on each target repository's base branch (typically `main`):

- Require a minimum of one reviewer, and do **not** allow requestors to approve their own changes. The PR's author is the Build Service identity, so any human counts as independent.
- Build validation (the repository's own CI pipeline) required.
- Check for linked work items / comment resolution as you do for any PR.
- Do not give the Build Service identity "Bypass policies when completing pull requests" or "Bypass policies when pushing".

## 8. Schedule

The pipeline contains a cron schedule in **UTC**, default `0 2 * * *` (02:00 every day), on the `main` branch of this repository. Change the cron expression in `azure-pipelines.yml` to suit; pipelines set with `always: true` run even when this repository has no changes. If you also use UI-defined schedules, remove them, because UI schedules override YAML ones.

## 9. First dry run

1. In `config/repositories.json` set `"enabled": true` and `"ring": "pilot"` on one or two low-risk repositories. Keep everything else `"enabled": false` or in another ring.
2. Run the pipeline manually with parameters `dryRun: true` and `repositories: ring:pilot` (parameter names as defined in `azure-pipelines.yml`).
3. A dry run clones, updates, builds, fixes and verifies locally but pushes nothing and touches no PR. Download the `out` artifact: per-repository JSON and markdown reports and the Learn audit logs (`learn-audit*.jsonl`).
4. Review the report for the build output, warnings, AI summaries and the audit log. Adjust `config/` and repeat.
5. Run with `dryRun: false` and `repositories: ring:pilot`; review the PRs; then widen the ring or use `*`.

See [troubleshooting.md](troubleshooting.md) if a repository fails, and [security.md](security.md) for the data-flow review you may need before enabling AI for sensitive repositories.
