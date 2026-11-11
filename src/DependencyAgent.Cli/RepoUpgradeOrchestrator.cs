using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using DependencyAgent.Core;
using DependencyAgent.Core.Configuration;
using DependencyAgent.Core.Model;
using Microsoft.Extensions.Logging;

namespace DependencyAgent.Cli;

/// <summary>Collaborators for one repository run. Kept as a record so tests can swap any piece.</summary>
public sealed record OrchestratorServices(
    IGitWorkspace Git,
    IPullRequestClient? PullRequests,
    Func<string, IReadOnlyList<IUpdateStep>> UpdateStepsFactory,
    IVerifier Verifier,
    IChangeGuard Guard,
    ICodeFixer RuleFixer,
    ICodeFixer AiFixer,
    IReportRenderer Renderer,
    Func<IEnumerable<BuildDiagnostic>, IEnumerable<BuildDiagnostic>, IReadOnlyList<BuildDiagnostic>> NewWarnings,
    ILoggerFactory LoggerFactory);

/// <summary>
/// Processes one repository end to end:
/// clone → agent branch → baseline build → deterministic updates → commit → build/test → rule fixes → AI loop →
/// guards → new-warnings gate → push → PR create/update. Never throws; failures become <see cref="RunOutcome.Failed"/>.
/// </summary>
public sealed class RepoUpgradeOrchestrator(OrchestratorServices services)
{
    /// <summary>Patterns added to <c>.git/info/exclude</c> so build outputs can never be committed, even if the repo has no .gitignore.</summary>
    private static readonly string[] LocalExcludes = ["bin/", "obj/", "TestResults/", "*.trx", ".vs/", "*.binlog"];

    public async Task<RepoRunReport> RunAsync(RepositoryEntry entry, JsonObject defaults, RunOptions options, string workRoot, CancellationToken cancellationToken)
    {
        var logger = services.LoggerFactory.CreateLogger($"repo:{entry.Key}");
        var sw = Stopwatch.StartNew();
        var report = new RepoRunReport
        {
            Project = entry.Project,
            Repository = entry.Repository,
            BaseBranch = entry.BaseBranch,
            DryRun = options.DryRun,
            StartedAt = DateTimeOffset.UtcNow,
        };

        try
        {
            await ProcessAsync(entry, defaults, options, workRoot, report, logger, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            report.Outcome = RunOutcome.Failed;
            report.FailureReasons.Add("Run was cancelled (pipeline timeout or manual cancel).");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Run failed for {Repository}", entry.Key);
            report.Outcome = RunOutcome.Failed;
            report.FailureReasons.Add($"Agent error: {ex.GetType().Name}: {ex.Message}");
        }

        report.Duration = sw.Elapsed;
        return report;
    }

    private async Task ProcessAsync(RepositoryEntry entry, JsonObject defaults, RunOptions options, string workRoot, RepoRunReport report, ILogger logger, CancellationToken ct)
    {
        var workingDirectory = Path.Combine(Path.GetFullPath(workRoot), SafeName(entry.Key));
        if (Directory.Exists(workingDirectory))
        {
            ForceDeleteDirectory(workingDirectory);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(workingDirectory)!);
        logger.LogInformation("Cloning {Repository} ({Branch})", entry.Key, entry.BaseBranch);
        await services.Git.CloneAsync(entry, workingDirectory, ct);
        AddLocalExcludes(workingDirectory);

        var overridePath = Path.Combine(workingDirectory, ConfigLoader.RepoOverrideFileName);
        string? overrideJson = null;
        if (File.Exists(overridePath))
        {
            overrideJson = await File.ReadAllTextAsync(overridePath, ct);
            report.Notes.Add($"Applied repository override file {ConfigLoader.RepoOverrideFileName} (it can only tighten central rules).");
        }

        var settings = ConfigLoader.BuildEffectiveSettings(defaults, overrideJson, entry.Overrides);
        report.Branch = settings.BranchName;
        report.TargetFramework = settings.TargetFramework;
        report.Ai.Provider = !settings.Ai.Enabled
            ? "none"
            : string.IsNullOrWhiteSpace(options.AiProviderOverride) || options.AiProviderOverride == "config" ? settings.Ai.Provider : options.AiProviderOverride!;

        var context = new RepoContext { Entry = entry, Settings = settings, WorkingDirectory = workingDirectory, Options = options, Logger = logger };
        await services.Git.CheckoutAgentBranchAsync(context, ct);

        if (await services.Git.RemoteBranchHasForeignCommitsAsync(context, ct))
        {
            report.Outcome = RunOutcome.Skipped;
            report.Notes.Add($"Skipped: branch '{settings.BranchName}' contains commits by someone other than the agent. Merge or delete that branch/PR to let the agent continue.");
            return;
        }

        // 1. Baseline build (best effort) to know which warnings already existed.
        logger.LogInformation("Baseline build");
        var baseline = await services.Verifier.BuildAsync(context, ct);
        var baselineReliable = baseline.Succeeded;
        if (!baselineReliable)
        {
            report.Notes.Add($"Baseline build (before any change) did not succeed at stage {baseline.Stage}; the new-warnings gate is informational only for this run.");
        }

        // 2. Deterministic updates.
        foreach (var step in services.UpdateStepsFactory(workingDirectory))
        {
            logger.LogInformation("Update step {Step}", step.Name);
            var result = await step.ApplyAsync(context, ct);
            report.Changes.AddRange(result.Changes);
            report.Notes.AddRange(result.Notes);
        }

        if (report.Changes.Count == 0)
        {
            report.Outcome = RunOutcome.UpToDate;
            report.Notes.Add($"Already on {settings.TargetFramework} with current packages; nothing to do.");
            return;
        }

        var updateCommit = await services.Git.CommitAllAsync(context, $"chore(deps): upgrade to {settings.TargetFramework} and update dependencies", ct)
            ?? await services.Git.GetHeadAsync(workingDirectory, ct);

        // 3. Verify / fix loop.
        var priorChanges = report.Changes.ToList();
        var rulesTried = false;
        var aiIterations = 0;
        VerificationResult build;
        VerificationResult? tests;
        IReadOnlyList<BuildDiagnostic> newWarnings;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            (build, tests, newWarnings) = await VerifyAsync(context, baseline, baselineReliable, ct);
            var warningsBlock = settings.Verification.FailOnNewWarnings && baselineReliable && newWarnings.Count > 0;
            var green = build.Succeeded && (tests?.Succeeded ?? true) && !warningsBlock;
            if (green)
            {
                break;
            }

            var failed = !build.Succeeded ? build : tests is { Succeeded: false } ? tests : build;
            if (!rulesTried)
            {
                rulesTried = true;
                var ruleResult = await services.RuleFixer.FixAsync(context, new FixRequest(failed, newWarnings, priorChanges, 1), ct);
                report.Notes.AddRange(ruleResult.Notes);
                if (ruleResult.MadeChanges)
                {
                    report.Changes.AddRange(ruleResult.Changes);
                    await services.Git.CommitAllAsync(context, "fix: apply rule-based fixes for breaking changes", ct);
                    continue;
                }
            }

            if (aiIterations >= settings.Ai.MaxIterations)
            {
                break;
            }

            aiIterations++;
            logger.LogInformation("AI fix iteration {Iteration}/{Max}", aiIterations, settings.Ai.MaxIterations);
            var aiResult = await services.AiFixer.FixAsync(context, new FixRequest(failed, newWarnings, priorChanges, aiIterations), ct);
            report.Ai.Iterations = aiIterations;
            foreach (var note in aiResult.Notes)
            {
                if (note.StartsWith("AI summary:", StringComparison.OrdinalIgnoreCase))
                {
                    report.Ai.Summaries.Add(note["AI summary:".Length..].Trim());
                }
                else if (note.StartsWith("AI reverted:", StringComparison.OrdinalIgnoreCase))
                {
                    report.Ai.RevertedFiles.AddRange(note["AI reverted:".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    report.Notes.Add(note);
                }
                else
                {
                    report.Notes.Add(note);
                }
            }

            if (!aiResult.MadeChanges)
            {
                if (aiIterations == 1 && aiResult.Changes.Count == 0)
                {
                    report.Ai.Iterations = 0;
                }

                break;
            }

            report.Changes.AddRange(aiResult.Changes);
            report.Ai.FilesTouched.AddRange(aiResult.Changes.Where(c => c.Kind == ChangeKind.AiFix).Select(c => c.File));
            await services.Git.CommitAllAsync(context, $"fix: AI-assisted fixes for breaking changes (iteration {aiIterations})", ct);
        }

        report.Ai.FilesTouched = report.Ai.FilesTouched.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        report.Ai.RevertedFiles = report.Ai.RevertedFiles.Distinct(StringComparer.Ordinal).ToList();
        report.Build = build;
        report.Tests = tests;
        report.NewWarnings = newWarnings.ToList();

        // 4. Guards over everything the code fixers committed after the deterministic update commit.
        report.Guard = await services.Guard.EvaluateAsync(context, updateCommit, GuardScope.CodeFix, ct);

        // 5. Outcome.
        if (!build.Succeeded)
        {
            report.FailureReasons.Add($"{build.Stage} failed with {build.Errors.Count()} error(s).");
        }
        else if (tests is { Succeeded: false })
        {
            report.FailureReasons.Add(tests.Tests is { } t ? $"{t.Failed} test(s) failed." : "Test run failed.");
        }

        if (build.Succeeded && settings.Verification.FailOnNewWarnings && baselineReliable && newWarnings.Count > 0)
        {
            report.FailureReasons.Add($"{newWarnings.Count} new warning(s) compared with the baseline build.");
        }

        if (!report.Guard.Passed)
        {
            report.FailureReasons.AddRange(report.Guard.Violations.Select(v => $"Guard: {v}"));
        }

        report.Outcome = report.FailureReasons.Count == 0 ? RunOutcome.Green : RunOutcome.Draft;

        // 6. Publish.
        if (options.DryRun)
        {
            report.Notes.Add("Dry run: changes were not pushed and no pull request was created or updated.");
            return;
        }

        if (services.PullRequests is null)
        {
            throw new InvalidOperationException("No pull request client configured for a non-dry run.");
        }

        await services.Git.PushAsync(context, ct);
        report.PullRequest = await services.PullRequests.CreateOrUpdateAsync(context, report, ct);
    }

    private async Task<(VerificationResult Build, VerificationResult? Tests, IReadOnlyList<BuildDiagnostic> NewWarnings)> VerifyAsync(
        RepoContext context, VerificationResult baseline, bool baselineReliable, CancellationToken ct)
    {
        var build = await services.Verifier.BuildAsync(context, ct);
        if (!build.Succeeded)
        {
            return (build, null, []);
        }

        // Without a reliable baseline we cannot tell new warnings from old ones, so we do not gate on (or ask fixers about) them.
        IReadOnlyList<BuildDiagnostic> newWarnings = baselineReliable ? services.NewWarnings(baseline.Warnings, build.Warnings) : [];
        var tests = context.Settings.Verification.RunTests ? await services.Verifier.TestAsync(context, ct) : null;
        return (build, tests, newWarnings);
    }

    /// <summary>Writes <c>&lt;project&gt;__&lt;repo&gt;.json</c> and <c>.md</c> into the output directory.</summary>
    public static async Task WriteReportAsync(RepoRunReport report, IReportRenderer renderer, string outputDirectory, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDirectory);
        var baseName = SafeName($"{report.Project}__{report.Repository}");
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, baseName + ".json"), JsonSerializer.Serialize(report, ReportJson.Options), ct);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, baseName + ".md"), renderer.RenderFullReport(report), ct);
    }

    public static string SafeName(string value)
    {
        var chars = value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray();
        return new string(chars).Trim('.');
    }

    private static void AddLocalExcludes(string workingDirectory)
    {
        var excludeFile = Path.Combine(workingDirectory, ".git", "info", "exclude");
        Directory.CreateDirectory(Path.GetDirectoryName(excludeFile)!);
        File.AppendAllLines(excludeFile, ["", "# added by dependency-agent (local only)", .. LocalExcludes]);
    }

    private static void ForceDeleteDirectory(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }
}

public static class ReportJson
{
    public static readonly JsonSerializerOptions Options = new(ConfigLoader.JsonOptions)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}
