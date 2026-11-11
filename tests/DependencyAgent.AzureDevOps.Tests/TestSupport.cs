using System.Diagnostics;
using System.Net;
using System.Text;
using DependencyAgent.Core.Configuration;
using DependencyAgent.Core.Model;
using Microsoft.Extensions.Logging.Abstractions;

namespace DependencyAgent.AzureDevOps.Tests;

internal static class TestSupport
{
    public static RepoContext Context(string workingDirectory = "/tmp/none", Action<AgentSettings>? configure = null, string project = "Proj One", string repo = "My Repo")
    {
        var settings = new AgentSettings();
        configure?.Invoke(settings);
        return new RepoContext
        {
            Entry = new RepositoryEntry { Project = project, Repository = repo, BaseBranch = "main" },
            Settings = settings,
            WorkingDirectory = workingDirectory,
            Options = new RunOptions(),
            Logger = NullLogger.Instance,
        };
    }

    public static RepoRunReport SampleReport(RunOutcome outcome = RunOutcome.Green)
    {
        var report = new RepoRunReport
        {
            Project = "Proj One",
            Repository = "My Repo",
            BaseBranch = "main",
            Branch = "dependency-agent/net10-lts",
            Outcome = outcome,
            TargetFramework = "net10.0",
            Build = new VerificationResult(VerificationStage.Build, true, [], null, "build ok", TimeSpan.FromSeconds(3)),
            Tests = new VerificationResult(VerificationStage.Test, true, [], new TestSummary(10, 9, 0, 1, []), "tests ok", TimeSpan.FromSeconds(3)),
            Guard = new GuardResult(true, [], 2, 14, ["a.cs", "b.cs"]),
            StartedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            Duration = TimeSpan.FromMinutes(5),
        };
        report.Changes.Add(new ChangeRecord(ChangeKind.TargetFramework, "src/A/A.csproj", "TargetFramework", "net8.0", "net10.0"));
        report.Changes.Add(new ChangeRecord(ChangeKind.Package, "src/A/A.csproj", "Newtonsoft.Json", "12.0.1", "13.0.3"));
        report.Changes.Add(new ChangeRecord(ChangeKind.Package, "src/B/B.csproj", "Newtonsoft.Json", "12.0.1", "13.0.3"));
        report.Changes.Add(new ChangeRecord(ChangeKind.DockerImage, "Dockerfile", "mcr.microsoft.com/dotnet/aspnet", "8.0", "10.0"));
        report.Changes.Add(new ChangeRecord(ChangeKind.RuleFix, "src/A/Program.cs", "SYSLIB0014"));
        return report;
    }

    /// <summary>Scriptable HTTP handler that records every request.</summary>
    public sealed class FakeHandler : HttpMessageHandler
    {
        public sealed record Recorded(HttpMethod Method, string Url, string? Body, string? Authorization);

        public List<Recorded> Requests { get; } = [];

        public Func<Recorded, (HttpStatusCode Status, string Body)> Responder { get; set; } = _ => (HttpStatusCode.OK, "{}");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var rec = new Recorded(request.Method, request.RequestUri!.AbsoluteUri, body, request.Headers.Authorization?.ToString());
            Requests.Add(rec);
            var (status, text) = Responder(rec);
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }

    public static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args[0] == "config" ? [] : new[] { "-c", "user.name=Human", "-c", "user.email=human@example.com", "-c", "commit.gpgsign=false", "-c", "init.defaultBranch=main" })
        {
            psi.ArgumentList.Add(a);
        }

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error}");
        }

        return output.Trim();
    }

    public sealed class TempDir : IDisposable
    {
        public TempDir() => Path = Directory.CreateTempSubdirectory("da-test-").FullName;

        public string Path { get; }

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
        }
    }
}
