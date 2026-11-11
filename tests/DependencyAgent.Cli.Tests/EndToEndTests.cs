using System.Diagnostics;
using System.Text.Json.Nodes;
using DependencyAgent.AzureDevOps;
using DependencyAgent.Core;
using DependencyAgent.Core.Configuration;
using DependencyAgent.Core.Model;
using DependencyAgent.Core.Processes;
using DependencyAgent.Fixing;
using DependencyAgent.Updates;
using DependencyAgent.Updates.Packages;
using DependencyAgent.Verification;
using Microsoft.Extensions.Logging.Abstractions;

namespace DependencyAgent.Cli.Tests;

/// <summary>
/// Full run against tests/fixtures/SampleLegacyApp (net8.0) with real git, NuGet, dotnet build/test, guards and rule fixes.
/// Only the AI provider is scripted (it applies Microsoft's documented replacements and also tries to touch protected files).
/// Needs the .NET 8 and 10 SDKs and nuget.org, so it only runs when DEPENDENCY_AGENT_E2E=1.
/// </summary>
public sealed class EndToEndTests : IDisposable
{
    private const string Branch = "dependency-agent/net10-lts";
    private readonly string root = Path.Combine(Path.GetTempPath(), "dagent-e2e-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private static void RequireOptIn() =>
        Assert.SkipUnless(Environment.GetEnvironmentVariable("DEPENDENCY_AGENT_E2E") == "1", "Set DEPENDENCY_AGENT_E2E=1 to run the end-to-end upgrade (needs .NET 8 + 10 SDKs and nuget.org).");

    [Fact]
    public async Task ScriptedAi_FixesAllowedFiles_RevertsProtectedEdits_AndOpensReadyPullRequest()
    {
        RequireOptIn();
        var (report, prs, provider) = await RunAsync(removeProtectedBreak: true);

        Assert.True(report.Outcome == RunOutcome.Green, Describe(report));
        Assert.Contains(report.Changes, c => c.Kind == ChangeKind.TargetFramework && c.To == "net10.0");
        Assert.Contains(report.Changes, c => c.Kind == ChangeKind.Sdk && c.File == "global.json");
        Assert.Contains(report.Changes, c => c.Kind == ChangeKind.DockerImage && c.To!.StartsWith("10.0", StringComparison.Ordinal));
        Assert.Contains(report.Changes, c => c.Kind == ChangeKind.Package && c.Subject == "System.Text.Json");
        Assert.Contains("src/SampleLegacyApp.Core/Hashing/PasswordHasher.cs", report.Ai.FilesTouched);
        Assert.Contains("src/SampleLegacyApp.Core/Security/Injected.cs", report.Ai.RevertedFiles);
        Assert.Contains(".dependency-agent.json", report.Ai.RevertedFiles);
        Assert.True(report.Tests!.Tests!.Passed > 0);
        Assert.Equal(0, report.Tests.Tests.Failed);
        Assert.True(provider.Calls >= 1);

        var pr = Assert.Single(prs.Calls);
        Assert.Equal(RunOutcome.Green, pr.Outcome);

        var subjects = Git(Path.Combine(root, "origin.git"), "log", "--format=%s", $"main..{Branch}");
        Assert.Contains("chore(deps): upgrade to net10.0", subjects, StringComparison.Ordinal);
        Assert.Contains("AI-assisted fixes", subjects, StringComparison.Ordinal);
        var tree = Git(Path.Combine(root, "origin.git"), "ls-tree", "-r", "--name-only", Branch);
        Assert.DoesNotContain("Security/Injected.cs", tree, StringComparison.Ordinal);
        Assert.DoesNotContain("/bin/", tree, StringComparison.Ordinal);
        Assert.DoesNotContain("/obj/", tree, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProtectedFileStillBroken_ProducesDraftPullRequest_AndLeavesProtectedFileUntouched()
    {
        RequireOptIn();
        var (report, prs, _) = await RunAsync(removeProtectedBreak: false);

        Assert.True(report.Outcome == RunOutcome.Draft, Describe(report));
        Assert.Contains(report.Build!.Errors, e => e.Code == "SYSLIB0060" && e.File!.Contains("Security/LegacyTokenSigner.cs", StringComparison.Ordinal));
        Assert.True(Assert.Single(prs.Calls).Outcome == RunOutcome.Draft);
        var signer = Git(Path.Combine(root, "origin.git"), "diff", "main", Branch, "--", "src/SampleLegacyApp.Core/Security/LegacyTokenSigner.cs");
        Assert.True(string.IsNullOrWhiteSpace(signer), "Protected file must not change:\n" + signer);
    }

    private async Task<(RepoRunReport Report, FakePullRequests Prs, ScriptedAiProvider Provider)> RunAsync(bool removeProtectedBreak)
    {
        var repoRoot = FindRepoRoot();
        var seed = Path.Combine(root, "seed");
        CopyDirectory(Path.Combine(repoRoot, "tests", "fixtures", "SampleLegacyApp"), seed);
        if (removeProtectedBreak)
        {
            File.Delete(Path.Combine(seed, "src", "SampleLegacyApp.Core", "Security", "LegacyTokenSigner.cs"));
            File.Delete(Path.Combine(seed, "tests", "SampleLegacyApp.Tests", "LegacyTokenSignerTests.cs"));
        }

        Git(seed, "init", "-q", "-b", "main");
        Git(seed, "-c", "user.name=Dev", "-c", "user.email=dev@example.test", "add", "-A");
        Git(seed, "-c", "user.name=Dev", "-c", "user.email=dev@example.test", "commit", "-q", "-m", "initial");
        var origin = Path.Combine(root, "origin.git");
        Git(root, "clone", "-q", "--bare", seed, origin);

        var runner = new ProcessRunner();
        var connection = new AzureDevOpsConnection("https://dev.azure.com/e2e-org/", "e2e-not-a-real-token", (_, _) => origin);
        var verifier = new DotnetVerifier(runner);
        var guard = new GitChangeGuard(runner);
        var provider = new ScriptedAiProvider();
        var prs = new FakePullRequests();
        var services = new OrchestratorServices(
            new GitWorkspace(runner, connection),
            prs,
            _ => UpdatePipeline.CreateDefault(r => new NuGetPackageVersionSource(r)),
            verifier,
            guard,
            new RuleBasedFixer(runner),
            new AiFixer([provider], guard, runner, _ => null, _ => null),
            new MarkdownReportRenderer(),
            (b, c) => WarningBaseline.NewWarnings(b, c),
            NullLoggerFactory.Instance);

        var entry = new RepositoryEntry
        {
            Project = "Lab",
            Repository = "SampleLegacyApp",
            BaseBranch = "main",
            Overrides = JsonNode.Parse("""{ "verification": { "failOnNewWarnings": false }, "ai": { "maxIterations": 2 } }""")!.AsObject(),
        };
        var options = new RunOptions
        {
            AiProviderOverride = ScriptedAiProvider.ProviderName,
            ConfigDirectory = Path.Combine(repoRoot, "config"),
            OutputDirectory = Path.Combine(root, "out"),
        };
        var defaults = ConfigLoader.LoadDefaultsNode(options.ConfigDirectory);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var report = await new RepoUpgradeOrchestrator(services).RunAsync(entry, defaults, options, Path.Combine(root, "work"), timeout.Token);
        await RepoUpgradeOrchestrator.WriteReportAsync(report, services.Renderer, options.OutputDirectory, timeout.Token);

        // Optional: keep the generated reports for inspection.
        if (Environment.GetEnvironmentVariable("DEPENDENCY_AGENT_E2E_OUT") is { Length: > 0 } keep)
        {
            CopyDirectory(options.OutputDirectory, Path.Combine(keep, removeProtectedBreak ? "green" : "draft"));
        }

        return (report, prs, provider);
    }

    private static string Describe(RepoRunReport report) =>
        $"Outcome {report.Outcome}. Reasons: {string.Join(" | ", report.FailureReasons)}. Notes: {string.Join(" | ", report.Notes)}. " +
        $"Errors: {string.Join(" | ", report.Build?.Errors.Select(e => $"{e.Code} {e.File}") ?? [])}. Tail: {report.Build?.OutputTail}";

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DependencyAgent.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var segments = relative.Split(Path.DirectorySeparatorChar);
            if (segments.Any(s => s is "bin" or "obj" or "TestResults"))
            {
                continue;
            }

            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static string Git(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
        return stdout;
    }

    /// <summary>Stands in for Copilot/OpenAI: applies the documented replacements, and misbehaves on purpose.</summary>
    internal sealed class ScriptedAiProvider : IAiProvider
    {
        public const string ProviderName = "scripted";

        public string Name => ProviderName;

        public int Calls { get; private set; }

        public string? CheckAvailability(AgentSettings settings) => null;

        public async Task<AiRunResult> RunAsync(AiRunRequest request, AgentSettings settings, CancellationToken cancellationToken)
        {
            Calls++;
            var wd = request.WorkingDirectory;
            await Replace(wd, "src/SampleLegacyApp.Core/Hashing/PasswordHasher.cs",
                """
                        using var kdf = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256);
                        return Convert.ToHexString(kdf.GetBytes(KeyLength));
                """,
                """
                        return Convert.ToHexString(Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeyLength));
                """, cancellationToken);
            await Replace(wd, "src/SampleLegacyApp.Core/Certificates/CertificateReader.cs",
                "new X509Certificate2(rawData)", "X509CertificateLoader.LoadCertificate(rawData)", cancellationToken);

            // Misbehaviour the guards must undo: a new file in a protected folder and an edit to the repo's agent config.
            var injected = Path.Combine(wd, "src", "SampleLegacyApp.Core", "Security", "Injected.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(injected)!);
            await File.WriteAllTextAsync(injected, "namespace X; public static class Injected { }", cancellationToken);
            await File.AppendAllTextAsync(Path.Combine(wd, ".dependency-agent.json"), "\n", cancellationToken);

            return new AiRunResult(true, "Replaced SYSLIB0060/SYSLIB0057 obsoletions with Rfc2898DeriveBytes.Pbkdf2 and X509CertificateLoader.", null);
        }

        private static async Task Replace(string root, string relative, string find, string replace, CancellationToken ct)
        {
            var path = Path.Combine(root, relative);
            var text = await File.ReadAllTextAsync(path, ct);
            var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (normalized.Contains(find.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal))
            {
                await File.WriteAllTextAsync(path, normalized.Replace(find.Replace("\r\n", "\n", StringComparison.Ordinal), replace.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal), ct);
            }
        }
    }
}
