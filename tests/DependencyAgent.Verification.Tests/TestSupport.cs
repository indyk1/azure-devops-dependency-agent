using DependencyAgent.Core.Configuration;
using DependencyAgent.Core.Model;
using DependencyAgent.Core.Processes;
using Microsoft.Extensions.Logging.Abstractions;

namespace DependencyAgent.Verification.Tests;

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        // Resolve symlinks (e.g. /tmp on macOS) so paths compare equal with what tools print.
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "depagent-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(string relative) => System.IO.Path.Combine(Path, relative);

    public void Write(string relative, string content)
    {
        var full = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }

            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class TestContext
{
    public static RepoContext Create(string workingDirectory, Action<AgentSettings>? configure = null)
    {
        var settings = new AgentSettings();
        configure?.Invoke(settings);
        return new RepoContext
        {
            Entry = new RepositoryEntry { Project = "P", Repository = "R" },
            Settings = settings,
            WorkingDirectory = workingDirectory,
            Options = new RunOptions(),
            Logger = NullLogger.Instance,
        };
    }
}

/// <summary>Records process specs and answers from a script; used to check arguments and secret scopes without running dotnet.</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Func<ProcessSpec, ProcessResult> _respond;

    public FakeProcessRunner(Func<ProcessSpec, ProcessResult> respond) => _respond = respond;

    public List<ProcessSpec> Specs { get; } = [];

    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        Specs.Add(spec);
        return Task.FromResult(_respond(spec));
    }

    public static ProcessResult Ok(string stdout = "") => new(0, stdout, "", false, TimeSpan.FromMilliseconds(5));

    public static ProcessResult Fail(string stdout, int exit = 1) => new(exit, stdout, "", false, TimeSpan.FromMilliseconds(5));
}

internal static class Git
{
    public static async Task<string> RunAsync(string dir, params string[] args)
    {
        var result = await new ProcessRunner().RunAsync(new ProcessSpec { FileName = "git", Arguments = args, WorkingDirectory = dir }, CancellationToken.None);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.StandardError}");
        }

        return result.StandardOutput;
    }

    public static async Task InitAsync(string dir)
    {
        await RunAsync(dir, "init", "-q", "-b", "main");
        await RunAsync(dir, "config", "user.email", "t@example.com");
        await RunAsync(dir, "config", "user.name", "Test");
        await RunAsync(dir, "config", "commit.gpgsign", "false");
        await RunAsync(dir, "config", "core.autocrlf", "false");
    }

    public static async Task CommitAllAsync(string dir, string message = "c")
    {
        await RunAsync(dir, "add", "-A");
        await RunAsync(dir, "commit", "-q", "-m", message);
    }
}
