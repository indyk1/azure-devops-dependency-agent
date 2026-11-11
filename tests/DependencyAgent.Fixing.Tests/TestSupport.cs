using DependencyAgent.Core;
using DependencyAgent.Core.Configuration;
using DependencyAgent.Core.Model;
using DependencyAgent.Core.Processes;
using Microsoft.Extensions.Logging.Abstractions;

namespace DependencyAgent.Fixing.Tests;

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "depagent-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(string relative) => System.IO.Path.Combine(Path, relative);

    public string Write(string relative, string content)
    {
        var full = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public string Read(string relative) => File.ReadAllText(Combine(relative));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ignore
        }
    }
}

internal static class Ctx
{
    public static RepoContext Create(string workingDirectory, Action<AgentSettings>? tweak = null, RunOptions? options = null)
    {
        var settings = new AgentSettings();
        tweak?.Invoke(settings);
        return new RepoContext
        {
            Entry = new RepositoryEntry { Project = "Contoso", Repository = "billing-service" },
            Settings = settings,
            WorkingDirectory = workingDirectory,
            Options = options ?? new RunOptions(),
            Logger = NullLogger.Instance,
        };
    }

    public static BuildDiagnostic Error(string code, string? file, int line = 1, string message = "message") =>
        new(DiagnosticSeverity.Error, code, message, file, line, 1, null);

    public static BuildDiagnostic Warning(string code, string? file, int line = 1, string message = "message") =>
        new(DiagnosticSeverity.Warning, code, message, file, line, 1, null);

    public static VerificationResult Failed(params BuildDiagnostic[] diagnostics) =>
        new(VerificationStage.Build, false, diagnostics, null, "", TimeSpan.Zero);

    public static FixRequest Request(VerificationResult verification, IReadOnlyList<BuildDiagnostic>? warnings = null, IReadOnlyList<ChangeRecord>? prior = null, int iteration = 1) =>
        new(verification, warnings ?? [], prior ?? [], iteration);
}

/// <summary>Records process specs; returns a scripted result (default: success, empty output).</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    public List<ProcessSpec> Specs { get; } = [];

    public Func<ProcessSpec, ProcessResult>? Handler { get; set; }

    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        Specs.Add(spec);
        return Task.FromResult(Handler?.Invoke(spec) ?? new ProcessResult(0, "", "", false, TimeSpan.Zero));
    }
}

internal static class Git
{
    private static readonly ProcessRunner Runner = new();

    public static void Run(string dir, params string[] args)
    {
        var result = Runner.RunAsync(new ProcessSpec { FileName = "git", Arguments = args, WorkingDirectory = dir }, CancellationToken.None).GetAwaiter().GetResult();
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.CombinedOutput}");
        }
    }

    /// <summary>Creates a git repo with the given files committed.</summary>
    public static TempDir InitRepo(IDictionary<string, string> files)
    {
        var dir = new TempDir();
        Run(dir.Path, "init", "-q", "-b", "main");
        Run(dir.Path, "config", "user.email", "t@example.invalid");
        Run(dir.Path, "config", "user.name", "Test");
        Run(dir.Path, "config", "commit.gpgsign", "false");
        foreach (var (path, content) in files)
        {
            dir.Write(path, content);
        }

        Run(dir.Path, "add", "-A");
        Run(dir.Path, "commit", "-q", "-m", "initial");
        return dir;
    }
}

/// <summary>Reverts uncommitted changes to files that are not editable (like the real GitChangeGuard would).</summary>
internal sealed class GitRevertingGuard : IChangeGuard
{
    private readonly ProcessRunner _runner = new();

    public Task<GuardResult> EvaluateAsync(RepoContext context, string sinceRef, GuardScope scope, CancellationToken cancellationToken) =>
        Task.FromResult(new GuardResult(true, [], 0, 0, []));

    public async Task<IReadOnlyList<string>> RevertDisallowedAsync(RepoContext context, CancellationToken cancellationToken)
    {
        var status = await _runner.RunAsync(new ProcessSpec
        {
            FileName = "git",
            Arguments = ["status", "--porcelain=v1", "-z", "--untracked-files=all"],
            WorkingDirectory = context.WorkingDirectory,
        }, cancellationToken);

        var reverted = new List<string>();
        foreach (var (st, path) in status.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(e => e.Length > 3).Select(e => (e[..2], e[3..])))
        {
            if (GlobMatcher.IsMatchAny(context.Settings.Guards.AllowedEditPatterns, path) && !GlobMatcher.IsMatchAny(context.Settings.Guards.ProtectedPatterns, path))
            {
                continue;
            }

            if (st == "??")
            {
                File.Delete(Path.Combine(context.WorkingDirectory, path));
            }
            else
            {
                await _runner.RunAsync(new ProcessSpec
                {
                    FileName = "git",
                    Arguments = ["checkout", "HEAD", "--", path],
                    WorkingDirectory = context.WorkingDirectory,
                }, cancellationToken);
            }

            reverted.Add(path);
        }

        return reverted;
    }
}
