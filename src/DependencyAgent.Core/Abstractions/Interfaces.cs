using DependencyAgent.Core.Configuration;
using DependencyAgent.Core.Model;

namespace DependencyAgent.Core;

/// <summary>A deterministic update step (TFM, global.json, NuGet, Docker). Must be idempotent.</summary>
public interface IUpdateStep
{
    string Name { get; }

    Task<UpdateStepResult> ApplyAsync(RepoContext context, CancellationToken cancellationToken);
}

/// <summary>Restores, builds and tests the target repository.</summary>
public interface IVerifier
{
    /// <summary>
    /// <c>dotnet restore</c> (NuGet feed credentials allowed) then <c>dotnet build --no-restore</c>
    /// (all secrets scrubbed). Returns the build result; a restore failure returns Stage=Restore.
    /// </summary>
    Task<VerificationResult> BuildAsync(RepoContext context, CancellationToken cancellationToken);

    /// <summary><c>dotnet test --no-build</c> with all secrets scrubbed (tests execute repository code).</summary>
    Task<VerificationResult> TestAsync(RepoContext context, CancellationToken cancellationToken);
}

/// <summary>Enforces change budget, allowed-edit and protected-path rules using git diffs.</summary>
public interface IChangeGuard
{
    /// <summary>Evaluate changes in the working tree (committed + uncommitted) since <paramref name="sinceRef"/>.</summary>
    Task<GuardResult> EvaluateAsync(RepoContext context, string sinceRef, GuardScope scope, CancellationToken cancellationToken);

    /// <summary>
    /// Revert uncommitted changes (modified, added, deleted) to files that are not allowed or are protected.
    /// Returns repo-relative paths that were reverted.
    /// </summary>
    Task<IReadOnlyList<string>> RevertDisallowedAsync(RepoContext context, CancellationToken cancellationToken);
}

/// <summary>Attempts to fix a failing build/test. Rule-based and AI fixers implement this.</summary>
public interface ICodeFixer
{
    string Name { get; }

    Task<FixResult> FixAsync(RepoContext context, FixRequest request, CancellationToken cancellationToken);
}

/// <summary>An LLM backend (Copilot CLI, OpenAI API).</summary>
public interface IAiProvider
{
    /// <summary><c>copilot</c> or <c>openai</c>.</summary>
    string Name { get; }

    /// <summary>Returns a reason string when the provider cannot run (missing token/executable), else null.</summary>
    string? CheckAvailability(AgentSettings settings);

    Task<AiRunResult> RunAsync(AiRunRequest request, AgentSettings settings, CancellationToken cancellationToken);
}

/// <summary>Sanitized access to Microsoft Learn documentation.</summary>
public interface ILearnDocsClient
{
    Task<string> SearchDocsAsync(string query, CancellationToken cancellationToken);

    /// <summary>Fetch a learn.microsoft.com page as markdown. Non-Microsoft-docs URLs are refused.</summary>
    Task<string> FetchDocAsync(string url, CancellationToken cancellationToken);

    Task<string> SearchCodeSamplesAsync(string query, string? language, CancellationToken cancellationToken);
}

/// <summary>Git operations on target repositories. Credentials are never persisted to .git/config.</summary>
public interface IGitWorkspace
{
    /// <summary>Clone <paramref name="repository"/> at its base branch into <paramref name="destination"/>.</summary>
    Task CloneAsync(RepositoryEntry repository, string destination, CancellationToken cancellationToken);

    /// <summary>Create (or reset) the local agent branch from the base branch and configure the commit identity.</summary>
    Task CheckoutAgentBranchAsync(RepoContext context, CancellationToken cancellationToken);

    Task<string> GetHeadAsync(string workingDirectory, CancellationToken cancellationToken);

    /// <summary>Stage everything and commit. Returns the new commit sha, or null when there was nothing to commit.</summary>
    Task<string?> CommitAllAsync(RepoContext context, string message, CancellationToken cancellationToken);

    /// <summary>
    /// True when the remote agent branch exists and contains commits (not on the base branch) by anyone other than the
    /// agent identity — i.e. a human pushed to it, so the agent must not overwrite it.
    /// </summary>
    Task<bool> RemoteBranchHasForeignCommitsAsync(RepoContext context, CancellationToken cancellationToken);

    /// <summary>Force-push (with lease) the agent branch.</summary>
    Task PushAsync(RepoContext context, CancellationToken cancellationToken);
}

/// <summary>Azure DevOps pull request operations.</summary>
public interface IPullRequestClient
{
    /// <summary>Create the PR or update the existing active one from the agent branch (title, description, draft state, comment with full report).</summary>
    Task<PullRequestInfo> CreateOrUpdateAsync(RepoContext context, RepoRunReport report, CancellationToken cancellationToken);
}

/// <summary>Renders reports as markdown.</summary>
public interface IReportRenderer
{
    string RenderPullRequestTitle(RepoRunReport report, AgentSettings settings);

    /// <summary>PR description; must be &lt;= <paramref name="maxLength"/> characters (Azure DevOps limit is 4000).</summary>
    string RenderPullRequestDescription(RepoRunReport report, int maxLength = 4000);

    /// <summary>Full detail (diagnostics, test failures, AI summaries) for a PR comment and the pipeline artifact.</summary>
    string RenderFullReport(RepoRunReport report);

    /// <summary>One table across all repositories for the pipeline run summary.</summary>
    string RenderRunSummary(IReadOnlyList<RepoRunReport> reports);
}
