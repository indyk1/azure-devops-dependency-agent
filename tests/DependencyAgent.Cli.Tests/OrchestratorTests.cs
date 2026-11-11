using System.Text.Json;
using System.Text.Json.Nodes;
using DependencyAgent.Core;
using DependencyAgent.Core.Configuration;
using DependencyAgent.Core.Model;
using Microsoft.Extensions.Logging.Abstractions;

namespace DependencyAgent.Cli.Tests;

public sealed class OrchestratorTests : IDisposable
{
    private static readonly ChangeRecord TfmChange = new(ChangeKind.TargetFramework, "src/A.csproj", "A.csproj", "net8.0", "net10.0");

    private readonly string workRoot = Path.Combine(Path.GetTempPath(), "dagent-orch-" + Guid.NewGuid().ToString("N"));
    private readonly RepositoryEntry entry = new() { Project = "Lab", Repository = "Sample", BaseBranch = "main" };
    private readonly FakeGit git = new();
    private readonly FakePullRequests prs = new();

    public void Dispose()
    {
        if (Directory.Exists(workRoot))
        {
            Directory.Delete(workRoot, recursive: true);
        }
    }

    private static JsonObject Defaults(Action<AgentSettings>? configure = null)
    {
        var settings = new AgentSettings();
        configure?.Invoke(settings);
        return (JsonObject)JsonSerializer.SerializeToNode(settings, ConfigLoader.JsonOptions)!;
    }

    private Task<RepoRunReport> RunAsync(
        IVerifier verifier,
        IUpdateStep? update = null,
        ICodeFixer? rules = null,
        ICodeFixer? ai = null,
        IChangeGuard? guard = null,
        bool dryRun = false,
        JsonObject? defaults = null)
    {
        var services = new OrchestratorServices(
            git,
            dryRun ? null : prs,
            _ => update is null ? [] : [update],
            verifier,
            guard ?? new FakeGuard(),
            rules ?? new FakeFixer("rules"),
            ai ?? new FakeFixer("ai"),
            new FakeRenderer(),
            (baseline, current) => current.Where(c => baseline.All(b => b.Fingerprint != c.Fingerprint)).ToList(),
            NullLoggerFactory.Instance);
        var options = new RunOptions { DryRun = dryRun, OutputDirectory = Path.Combine(workRoot, "out") };
        return new RepoUpgradeOrchestrator(services).RunAsync(entry, defaults ?? Defaults(), options, workRoot, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NoChanges_IsUpToDate_AndOpensNoPullRequest()
    {
        var report = await RunAsync(new FakeVerifier([Results.BuildOk()]));

        Assert.Equal(RunOutcome.UpToDate, report.Outcome);
        Assert.Empty(git.Commits);
        Assert.Equal(0, git.Pushes);
        Assert.Empty(prs.Calls);
    }

    [Fact]
    public async Task GreenBuildAndTests_PushesAndCreatesReadyPullRequest()
    {
        var report = await RunAsync(new FakeVerifier([Results.BuildOk(), Results.BuildOk()]), new FakeUpdateStep(TfmChange));

        Assert.Equal(RunOutcome.Green, report.Outcome);
        Assert.Empty(report.FailureReasons);
        Assert.Single(git.Commits);
        Assert.Equal(1, git.Pushes);
        Assert.Single(prs.Calls);
        Assert.False(report.PullRequest!.IsDraft);
        Assert.Equal("net10.0", report.TargetFramework);
    }

    [Fact]
    public async Task BrokenBuild_RuleFixerRunsBeforeAi_AndAiFixReachesGreen()
    {
        var rules = new FakeFixer("rules", FixResult.None("no rule matched"));
        var ai = new FakeFixer("ai", new FixResult(true, [new ChangeRecord(ChangeKind.AiFix, "src/A.cs", "copilot")], ["AI summary: replaced obsolete ctor", "AI reverted: src/Security/X.cs"]));
        var verifier = new FakeVerifier([Results.BuildOk(), Results.BuildFailed(), Results.BuildOk()]);

        var report = await RunAsync(verifier, new FakeUpdateStep(TfmChange), rules, ai);

        Assert.Equal(RunOutcome.Green, report.Outcome);
        Assert.Single(rules.Requests);
        Assert.Single(ai.Requests);
        Assert.Equal(1, ai.Requests[0].Iteration);
        Assert.Contains(ai.Requests[0].PriorChanges, c => c.Kind == ChangeKind.TargetFramework);
        Assert.Equal(1, report.Ai.Iterations);
        Assert.Equal(["src/A.cs"], report.Ai.FilesTouched);
        Assert.Equal(["src/Security/X.cs"], report.Ai.RevertedFiles);
        Assert.Contains("replaced obsolete ctor", report.Ai.Summaries);
        Assert.Equal(2, git.Commits.Count);
    }

    [Fact]
    public async Task RuleFixThatChangesCode_TriggersRebuildWithoutUsingAnAiIteration()
    {
        var rules = new FakeFixer("rules", new FixResult(true, [new ChangeRecord(ChangeKind.RuleFix, "src/A.cs", "SYSLIB0060")], []));
        var ai = new FakeFixer("ai");
        var verifier = new FakeVerifier([Results.BuildOk(), Results.BuildFailed(), Results.BuildOk()]);

        var report = await RunAsync(verifier, new FakeUpdateStep(TfmChange), rules, ai);

        Assert.Equal(RunOutcome.Green, report.Outcome);
        Assert.Empty(ai.Requests);
        Assert.Equal(0, report.Ai.Iterations);
    }

    [Fact]
    public async Task AiCannotFix_ProducesDraftWithReasons()
    {
        var verifier = new FakeVerifier([Results.BuildOk(), Results.BuildFailed()]);

        var report = await RunAsync(verifier, new FakeUpdateStep(TfmChange));

        Assert.Equal(RunOutcome.Draft, report.Outcome);
        Assert.Contains(report.FailureReasons, r => r.Contains("Build failed with 1 error", StringComparison.Ordinal));
        Assert.True(report.PullRequest!.IsDraft);
        Assert.Equal(1, git.Pushes);
    }

    [Fact]
    public async Task AiLoopStopsAtMaxIterations()
    {
        var change = new FixResult(true, [new ChangeRecord(ChangeKind.AiFix, "src/A.cs", "openai")], []);
        var ai = new FakeFixer("ai", change, change, change, change);
        var verifier = new FakeVerifier([Results.BuildOk(), Results.BuildFailed()]);

        var report = await RunAsync(verifier, new FakeUpdateStep(TfmChange), ai: ai, defaults: Defaults(s => s.Ai.MaxIterations = 2));

        Assert.Equal(2, ai.Requests.Count);
        Assert.Equal(2, report.Ai.Iterations);
        Assert.Equal(RunOutcome.Draft, report.Outcome);
    }

    [Fact]
    public async Task FailingTests_AreSentToFixersAndReported()
    {
        var ai = new FakeFixer("ai");
        var verifier = new FakeVerifier([Results.BuildOk(), Results.BuildOk()], [Results.TestsFailed()]);

        var report = await RunAsync(verifier, new FakeUpdateStep(TfmChange), ai: ai);

        Assert.Equal(VerificationStage.Test, ai.Requests.Single().FailedVerification.Stage);
        Assert.Contains("1 test(s) failed.", report.FailureReasons);
        Assert.Equal(RunOutcome.Draft, report.Outcome);
    }

    [Fact]
    public async Task NewWarnings_WithReliableBaseline_BlockGreen()
    {
        var verifier = new FakeVerifier([Results.BuildOk(Results.Warning("CS0168")), Results.BuildOk(Results.Warning("CS0168"), Results.Warning("ASPDEPR008", "src/Program.cs"))]);

        var report = await RunAsync(verifier, new FakeUpdateStep(TfmChange));

        Assert.Equal(RunOutcome.Draft, report.Outcome);
        Assert.Equal("ASPDEPR008", Assert.Single(report.NewWarnings).Code);
    }

    [Fact]
    public async Task NewWarnings_AreNotGated_WhenBaselineBuildFailed()
    {
        var verifier = new FakeVerifier([Results.BuildFailed(), Results.BuildOk(Results.Warning("ASPDEPR008"))]);

        var report = await RunAsync(verifier, new FakeUpdateStep(TfmChange));

        Assert.Equal(RunOutcome.Green, report.Outcome);
        Assert.Empty(report.NewWarnings);
        Assert.Contains(report.Notes, n => n.Contains("Baseline build", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GuardViolation_ForcesDraft()
    {
        var guard = new FakeGuard(new GuardResult(false, ["Changed 50 files (limit 40)."], 50, 900, []));

        var report = await RunAsync(new FakeVerifier([Results.BuildOk()]), new FakeUpdateStep(TfmChange), guard: guard);

        Assert.Equal(RunOutcome.Draft, report.Outcome);
        Assert.Contains("Guard: Changed 50 files (limit 40).", report.FailureReasons);
    }

    [Fact]
    public async Task ForeignCommitsOnAgentBranch_SkipWithoutTouchingAnything()
    {
        git.ForeignCommits = true;
        var verifier = new FakeVerifier([Results.BuildOk()]);

        var report = await RunAsync(verifier, new FakeUpdateStep(TfmChange));

        Assert.Equal(RunOutcome.Skipped, report.Outcome);
        Assert.Equal(0, verifier.BuildCalls);
        Assert.Equal(0, git.Pushes);
    }

    [Fact]
    public async Task DryRun_NeverPushesOrCallsAzureDevOps()
    {
        var report = await RunAsync(new FakeVerifier([Results.BuildOk()]), new FakeUpdateStep(TfmChange), dryRun: true);

        Assert.Equal(RunOutcome.Green, report.Outcome);
        Assert.Equal(0, git.Pushes);
        Assert.Empty(prs.Calls);
        Assert.Null(report.PullRequest);
        Assert.True(report.DryRun);
    }

    [Fact]
    public async Task Exceptions_BecomeFailedOutcome()
    {
        git.CloneFailure = new InvalidOperationException("403 Forbidden");

        var report = await RunAsync(new FakeVerifier([Results.BuildOk()]));

        Assert.Equal(RunOutcome.Failed, report.Outcome);
        Assert.Contains(report.FailureReasons, r => r.Contains("403 Forbidden", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RepoOverrideFile_IsAppliedAndNoted()
    {
        var verifier = new FakeVerifier([Results.BuildOk()]);
        var services = new OrchestratorServices(
            new OverrideWritingGit("""{ "ai": { "enabled": false, "provider": "openai" }, "guards": { "maxChangedFiles": 5 } }"""),
            prs, _ => [new FakeUpdateStep(TfmChange)], verifier, new FakeGuard(), new FakeFixer("rules"), new FakeFixer("ai"),
            new FakeRenderer(), (_, c) => c.ToList(), NullLoggerFactory.Instance);
        var options = new RunOptions { DryRun = true, OutputDirectory = Path.Combine(workRoot, "out") };

        var report = await new RepoUpgradeOrchestrator(services).RunAsync(entry, Defaults(), options, workRoot, TestContext.Current.CancellationToken);

        Assert.Equal("none", report.Ai.Provider);
        Assert.Contains(report.Notes, n => n.Contains(ConfigLoader.RepoOverrideFileName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReportJson_RoundTrips()
    {
        var report = await RunAsync(new FakeVerifier([Results.BuildOk(), Results.BuildFailed()]), new FakeUpdateStep(TfmChange));
        var outDir = Path.Combine(workRoot, "reports");

        await RepoUpgradeOrchestrator.WriteReportAsync(report, new FakeRenderer(), outDir, TestContext.Current.CancellationToken);
        var json = await File.ReadAllTextAsync(Path.Combine(outDir, "Lab__Sample.json"), TestContext.Current.CancellationToken);
        var back = JsonSerializer.Deserialize<RepoRunReport>(json, ReportJson.Options)!;

        Assert.Contains("\"Draft\"", json, StringComparison.Ordinal);
        Assert.Equal(report.Outcome, back.Outcome);
        Assert.Equal(report.Changes, back.Changes);
        Assert.Equal("SYSLIB0060", back.Build!.Errors.Single().Code);
        Assert.True(File.Exists(Path.Combine(outDir, "Lab__Sample.md")));
    }

    private sealed class OverrideWritingGit(string json) : IGitWorkspace
    {
        private readonly FakeGit inner = new();

        public async Task CloneAsync(RepositoryEntry repository, string destination, CancellationToken cancellationToken)
        {
            await inner.CloneAsync(repository, destination, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(destination, ConfigLoader.RepoOverrideFileName), json, cancellationToken);
        }

        public Task CheckoutAgentBranchAsync(RepoContext context, CancellationToken cancellationToken) => inner.CheckoutAgentBranchAsync(context, cancellationToken);

        public Task<string> GetHeadAsync(string workingDirectory, CancellationToken cancellationToken) => inner.GetHeadAsync(workingDirectory, cancellationToken);

        public Task<string?> CommitAllAsync(RepoContext context, string message, CancellationToken cancellationToken) => inner.CommitAllAsync(context, message, cancellationToken);

        public Task<bool> RemoteBranchHasForeignCommitsAsync(RepoContext context, CancellationToken cancellationToken) => inner.RemoteBranchHasForeignCommitsAsync(context, cancellationToken);

        public Task PushAsync(RepoContext context, CancellationToken cancellationToken) => inner.PushAsync(context, cancellationToken);
    }
}
