using DependencyAgent.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace DependencyAgent.Core.Model;

/// <summary>Per-run options from the command line / pipeline parameters.</summary>
public sealed class RunOptions
{
    /// <summary>Do everything locally (clone, update, fix, verify) but never push or touch PRs.</summary>
    public bool DryRun { get; init; }

    /// <summary>Overrides <c>ai.provider</c> for this run when not null/"config".</summary>
    public string? AiProviderOverride { get; init; }

    /// <summary>Directory containing defaults.json, repositories.json and rewrite-rules.json.</summary>
    public string ConfigDirectory { get; init; } = "config";

    /// <summary>Directory where per-repo reports (JSON + markdown) are written.</summary>
    public string OutputDirectory { get; init; } = "out";

    /// <summary>Absolute path of the running agent executable, used to launch the Learn MCP proxy (<c>learn-proxy</c> sub-command).</summary>
    public string? AgentExecutablePath { get; init; }
}

/// <summary>Everything a step needs about the repository currently being processed.</summary>
public sealed class RepoContext
{
    public required RepositoryEntry Entry { get; init; }

    public required AgentSettings Settings { get; init; }

    /// <summary>Absolute path of the cloned target repository.</summary>
    public required string WorkingDirectory { get; init; }

    public required RunOptions Options { get; init; }

    public required ILogger Logger { get; init; }
}

public enum ChangeKind
{
    TargetFramework,
    Package,
    Sdk,
    DockerImage,
    RuleFix,
    AiFix,
}

/// <summary>One human-readable change, rendered into the PR description.</summary>
/// <param name="File">Repo-relative path, forward slashes.</param>
/// <param name="Subject">What changed, e.g. a package id, TFM, image name or diagnostic id.</param>
public sealed record ChangeRecord(ChangeKind Kind, string File, string Subject, string? From = null, string? To = null, string? Detail = null);

public sealed record UpdateStepResult(IReadOnlyList<ChangeRecord> Changes, IReadOnlyList<string> Notes)
{
    public static UpdateStepResult Empty { get; } = new([], []);
}

public enum DiagnosticSeverity
{
    Warning,
    Error,
}

/// <summary>An MSBuild/compiler diagnostic parsed from build output.</summary>
/// <param name="File">Repo-relative path (forward slashes) when it could be determined, else as reported.</param>
public sealed record BuildDiagnostic(DiagnosticSeverity Severity, string Code, string Message, string? File, int? Line, int? Column, string? Project)
{
    /// <summary>Stable identity used to compare baseline vs upgraded warnings (ignores line numbers, which move).</summary>
    public string Fingerprint => $"{Code}|{File}|{Message}";
}

public sealed record FailedTest(string Name, string? Message);

public sealed record TestSummary(int Total, int Passed, int Failed, int Skipped, IReadOnlyList<FailedTest> FailedTests);

public enum VerificationStage
{
    Restore,
    Build,
    Test,
}

public sealed record VerificationResult(
    VerificationStage Stage,
    bool Succeeded,
    IReadOnlyList<BuildDiagnostic> Diagnostics,
    TestSummary? Tests,
    string OutputTail,
    TimeSpan Duration)
{
    public IEnumerable<BuildDiagnostic> Errors => Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);

    public IEnumerable<BuildDiagnostic> Warnings => Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning);
}

public enum GuardScope
{
    /// <summary>Only check change budget.</summary>
    Budget,

    /// <summary>Check budget, allowed patterns and protected patterns (used after code fixers).</summary>
    CodeFix,
}

public sealed record GuardResult(bool Passed, IReadOnlyList<string> Violations, int ChangedFiles, int ChangedLines, IReadOnlyList<string> Files);

/// <summary>Input for a code fixer iteration.</summary>
/// <param name="FailedVerification">The latest failing build or test result.</param>
/// <param name="NewWarnings">Warnings not present in the baseline build (fixers should try to resolve these too).</param>
/// <param name="PriorChanges">Deterministic changes already applied this run (TFM, packages, SDK, Docker) — context for fixers.</param>
/// <param name="Iteration">1-based fix iteration.</param>
public sealed record FixRequest(VerificationResult FailedVerification, IReadOnlyList<BuildDiagnostic> NewWarnings, IReadOnlyList<ChangeRecord> PriorChanges, int Iteration);

public sealed record FixResult(bool MadeChanges, IReadOnlyList<ChangeRecord> Changes, IReadOnlyList<string> Notes)
{
    public static FixResult None(params string[] notes) => new(false, [], notes);
}

/// <summary>Request to an AI provider. Providers edit files in <see cref="WorkingDirectory"/> directly.</summary>
public sealed class AiRunRequest
{
    public required string WorkingDirectory { get; init; }

    public required string SystemPrompt { get; init; }

    public required string TaskPrompt { get; init; }

    public required IReadOnlyList<string> AllowedEditPatterns { get; init; }

    public required IReadOnlyList<string> ProtectedPatterns { get; init; }

    public string? Model { get; init; }

    public required TimeSpan Timeout { get; init; }

    /// <summary>Command + args that start the sanitizing Learn MCP proxy over stdio (null = docs lookups disabled).</summary>
    public IReadOnlyList<string>? LearnProxyCommand { get; init; }

    /// <summary>Command + args that start the Roslynk MCP server over stdio (null = Roslyn tools disabled).</summary>
    public IReadOnlyList<string>? RoslynkCommand { get; init; }

    /// <summary>Absolute path of the .sln/.slnx the AI should open with Roslynk (may be a generated solution outside the repo).</summary>
    public string? SolutionPath { get; init; }

    /// <summary>Reasoning effort to request (none … max). Null = provider default.</summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>In-process docs client for providers that implement their own tool loop (null = disabled).</summary>
    public ILearnDocsClient? LearnDocs { get; init; }

    /// <summary>Lets in-process providers run a verification build as a tool.</summary>
    public Func<CancellationToken, Task<VerificationResult>>? BuildTool { get; init; }
}

public sealed record AiRunResult(bool Completed, string Summary, string? Error, int ToolCalls = 0);

public enum RunOutcome
{
    /// <summary>Nothing to update.</summary>
    UpToDate,

    /// <summary>Changes made, build/tests/guards green → ready-for-review PR.</summary>
    Green,

    /// <summary>Changes made but not green → draft PR with failure notes.</summary>
    Draft,

    /// <summary>Repo skipped (e.g. foreign commits on agent branch, unsupported layout).</summary>
    Skipped,

    /// <summary>Agent error.</summary>
    Failed,
}

public sealed record PullRequestInfo(int Id, string Url, bool IsDraft, bool Created);

public sealed class AiUsage
{
    public string Provider { get; set; } = "none";

    public int Iterations { get; set; }

    public List<string> FilesTouched { get; set; } = [];

    public List<string> RevertedFiles { get; set; } = [];

    public List<string> Summaries { get; set; } = [];
}

/// <summary>Result of processing one repository. Serialized to <c>out/&lt;project&gt;__&lt;repo&gt;.json</c>.</summary>
public sealed class RepoRunReport
{
    public string Project { get; set; } = "";

    public string Repository { get; set; } = "";

    public string BaseBranch { get; set; } = "";

    public string Branch { get; set; } = "";

    public RunOutcome Outcome { get; set; }

    public string TargetFramework { get; set; } = "";

    public List<ChangeRecord> Changes { get; set; } = [];

    public List<string> Notes { get; set; } = [];

    public List<string> FailureReasons { get; set; } = [];

    public VerificationResult? Build { get; set; }

    public VerificationResult? Tests { get; set; }

    public List<BuildDiagnostic> NewWarnings { get; set; } = [];

    public GuardResult? Guard { get; set; }

    public AiUsage Ai { get; set; } = new();

    public PullRequestInfo? PullRequest { get; set; }

    public bool DryRun { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public TimeSpan Duration { get; set; }
}
