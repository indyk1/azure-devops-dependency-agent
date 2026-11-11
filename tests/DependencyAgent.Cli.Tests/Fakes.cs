using DependencyAgent.Core;
using DependencyAgent.Core.Configuration;
using DependencyAgent.Core.Model;

namespace DependencyAgent.Cli.Tests;

internal sealed class FakeGit : IGitWorkspace
{
    public bool ForeignCommits { get; set; }

    public Exception? CloneFailure { get; set; }

    public List<string> Commits { get; } = [];

    public int Pushes { get; private set; }

    public Task CloneAsync(RepositoryEntry repository, string destination, CancellationToken cancellationToken)
    {
        if (CloneFailure is not null)
        {
            throw CloneFailure;
        }

        Directory.CreateDirectory(Path.Combine(destination, ".git", "info"));
        return Task.CompletedTask;
    }

    public Task CheckoutAgentBranchAsync(RepoContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<string> GetHeadAsync(string workingDirectory, CancellationToken cancellationToken) => Task.FromResult($"sha{Commits.Count}");

    public Task<string?> CommitAllAsync(RepoContext context, string message, CancellationToken cancellationToken)
    {
        Commits.Add(message);
        return Task.FromResult<string?>($"sha{Commits.Count}");
    }

    public Task<bool> RemoteBranchHasForeignCommitsAsync(RepoContext context, CancellationToken cancellationToken) => Task.FromResult(ForeignCommits);

    public Task PushAsync(RepoContext context, CancellationToken cancellationToken)
    {
        Pushes++;
        return Task.CompletedTask;
    }
}

internal sealed class FakePullRequests : IPullRequestClient
{
    public List<RepoRunReport> Calls { get; } = [];

    public Task<PullRequestInfo> CreateOrUpdateAsync(RepoContext context, RepoRunReport report, CancellationToken cancellationToken)
    {
        Calls.Add(report);
        return Task.FromResult(new PullRequestInfo(42, "https://dev.azure.com/org/p/_git/r/pullrequest/42", report.Outcome != RunOutcome.Green, Calls.Count == 1));
    }
}

internal sealed class FakeUpdateStep(params ChangeRecord[] changes) : IUpdateStep
{
    public string Name => "fake-update";

    public Task<UpdateStepResult> ApplyAsync(RepoContext context, CancellationToken cancellationToken) =>
        Task.FromResult(new UpdateStepResult(changes, []));
}

/// <summary>Returns queued build results (the first call is the baseline); the last one repeats.</summary>
internal sealed class FakeVerifier : IVerifier
{
    private readonly Queue<VerificationResult> builds;
    private readonly Queue<VerificationResult> tests;

    public FakeVerifier(IEnumerable<VerificationResult> builds, IEnumerable<VerificationResult>? tests = null)
    {
        this.builds = new Queue<VerificationResult>(builds);
        this.tests = new Queue<VerificationResult>(tests ?? [Results.TestsPassed()]);
    }

    public int BuildCalls { get; private set; }

    public Task<VerificationResult> BuildAsync(RepoContext context, CancellationToken cancellationToken)
    {
        BuildCalls++;
        return Task.FromResult(builds.Count > 1 ? builds.Dequeue() : builds.Peek());
    }

    public Task<VerificationResult> TestAsync(RepoContext context, CancellationToken cancellationToken) =>
        Task.FromResult(tests.Count > 1 ? tests.Dequeue() : tests.Peek());
}

internal sealed class FakeGuard(GuardResult? result = null) : IChangeGuard
{
    public Task<GuardResult> EvaluateAsync(RepoContext context, string sinceRef, GuardScope scope, CancellationToken cancellationToken) =>
        Task.FromResult(result ?? new GuardResult(true, [], 0, 0, []));

    public Task<IReadOnlyList<string>> RevertDisallowedAsync(RepoContext context, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}

internal sealed class FakeFixer(string name, params FixResult[] results) : ICodeFixer
{
    private readonly Queue<FixResult> queue = new(results);

    public string Name => name;

    public List<FixRequest> Requests { get; } = [];

    public Task<FixResult> FixAsync(RepoContext context, FixRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(queue.Count > 0 ? queue.Dequeue() : FixResult.None("no more scripted fixes"));
    }
}

internal sealed class FakeRenderer : IReportRenderer
{
    public string RenderPullRequestTitle(RepoRunReport report, AgentSettings settings) => "title";

    public string RenderPullRequestDescription(RepoRunReport report, int maxLength = 4000) => "description";

    public string RenderFullReport(RepoRunReport report) => $"# {report.Repository}: {report.Outcome}";

    public string RenderRunSummary(IReadOnlyList<RepoRunReport> reports) => "summary";
}

internal static class Results
{
    public static VerificationResult BuildOk(params BuildDiagnostic[] warnings) =>
        new(VerificationStage.Build, true, warnings, null, "", TimeSpan.FromSeconds(1));

    public static VerificationResult BuildFailed(string code = "SYSLIB0060", string file = "src/A.cs") =>
        new(VerificationStage.Build, false, [new BuildDiagnostic(DiagnosticSeverity.Error, code, "obsolete", file, 1, 1, "src/A.csproj")], null, "", TimeSpan.FromSeconds(1));

    public static VerificationResult TestsPassed() =>
        new(VerificationStage.Test, true, [], new TestSummary(3, 3, 0, 0, []), "", TimeSpan.FromSeconds(1));

    public static VerificationResult TestsFailed() =>
        new(VerificationStage.Test, false, [], new TestSummary(3, 2, 1, 0, [new FailedTest("T.Hash", "expected X")]), "", TimeSpan.FromSeconds(1));

    public static BuildDiagnostic Warning(string code, string file = "src/A.cs") =>
        new(DiagnosticSeverity.Warning, code, "warn", file, 1, 1, null);
}
