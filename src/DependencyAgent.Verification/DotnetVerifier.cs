using System.Text.Json;
using System.Xml.Linq;
using DependencyAgent.Core;
using DependencyAgent.Core.Configuration;
using DependencyAgent.Core.Model;
using DependencyAgent.Core.Processes;
using Microsoft.Extensions.Logging;

namespace DependencyAgent.Verification;

/// <summary>
/// <see cref="IVerifier"/> backed by the <c>dotnet</c> CLI. Restore runs with NuGet feed credentials only; build and test
/// (which execute repository code) run with every secret scrubbed. No temp files are ever written inside the repository.
/// </summary>
public sealed class DotnetVerifier : IVerifier
{
    public const int OutputTailLines = 150;
    private const string SyntheticCode = "AGENT0001";

    private readonly IProcessRunner _runner;

    public DotnetVerifier(IProcessRunner runner)
    {
        _runner = runner;
    }

    public async Task<VerificationResult> BuildAsync(RepoContext context, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        using var target = ResolveTarget(context);
        if (target is null)
        {
            return NoTargetResult(VerificationStage.Build, started);
        }

        var v = context.Settings.Verification;
        var root = context.WorkingDirectory;
        var restoreArgs = new List<string> { "restore", target.Argument, "-nologo", "-tl:off" };
        var restore = await _runner.RunAsync(Spec(root, restoreArgs, SecretScope.NuGetFeeds, TimeSpan.FromMinutes(v.BuildTimeoutMinutes)), cancellationToken);
        var restoreDiagnostics = MsBuildDiagnosticParser.Parse(restore.CombinedOutput, root);
        if (!restore.Succeeded)
        {
            return Finish(VerificationStage.Restore, restore, "restore", restoreDiagnostics, null, started);
        }

        // --no-incremental: an up-to-date project would otherwise not re-emit its compiler warnings, which breaks baseline comparison.
        var buildArgs = new List<string> { "build", target.Argument, "--no-restore", "--no-incremental", "-nologo", "-tl:off", "-clp:NoSummary;ForceNoAlign" };
        buildArgs.AddRange(v.BuildArguments);
        var build = await _runner.RunAsync(Spec(root, buildArgs, SecretScope.None, TimeSpan.FromMinutes(v.BuildTimeoutMinutes)), cancellationToken);
        // Restore warnings (NU1903 etc.) are part of the build's warning set; the parser de-duplicates what the build replays.
        var diagnostics = MsBuildDiagnosticParser.Parse(restore.CombinedOutput + "\n" + build.CombinedOutput, root);
        return Finish(VerificationStage.Build, build, "build", diagnostics, null, started);
    }

    public async Task<VerificationResult> TestAsync(RepoContext context, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        var v = context.Settings.Verification;
        if (!v.RunTests)
        {
            return new VerificationResult(VerificationStage.Test, true, [], null, "Tests disabled by configuration (verification.runTests=false).", TimeSpan.Zero);
        }

        using var target = ResolveTarget(context);
        if (target is null)
        {
            return NoTargetResult(VerificationStage.Test, started);
        }

        var root = context.WorkingDirectory;
        var resultsDir = Path.Combine(Path.GetTempPath(), "dependency-agent-trx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(resultsDir);
        try
        {
            var mtp = UsesMicrosoftTestingPlatform(root);
            var args = new List<string> { "test" };
            if (mtp)
            {
                // .NET 10 MTP mode: the target must be given via --solution / --project.
                args.Add(target.IsSolution ? "--solution" : "--project");
                args.Add(target.Argument);
                args.AddRange(["--no-build", "-nologo", "-tl:off", "--report-trx", "--results-directory", resultsDir]);
            }
            else
            {
                args.Add(target.Argument);
                args.AddRange(["--no-build", "-nologo", "-tl:off", "--logger", "trx", "--results-directory", resultsDir]);
            }

            args.AddRange(v.TestArguments);
            var run = await _runner.RunAsync(Spec(root, args, SecretScope.None, TimeSpan.FromMinutes(v.TestTimeoutMinutes)), cancellationToken);
            var output = run.CombinedOutput;

            var trx = Directory.EnumerateFiles(resultsDir, "*.trx", SearchOption.AllDirectories).Select(SafeRead).Where(c => c is not null).Select(c => c!).ToList();
            var summary = (trx.Count > 0 ? TestResultParser.ParseTrx(trx) : null) ?? TestResultParser.ParseConsole(output);

            var noTests = summary is null && (output.Contains("No test is available", StringComparison.OrdinalIgnoreCase)
                || output.Contains("does not contain any test", StringComparison.OrdinalIgnoreCase)
                || output.Contains("Could not find any test project", StringComparison.OrdinalIgnoreCase));
            if (summary is null && run.ExitCode == 8 && output.Contains("Zero tests ran", StringComparison.OrdinalIgnoreCase))
            {
                noTests = true; // Microsoft.Testing.Platform exit code 8
            }

            summary ??= new TestSummary(0, 0, 0, 0, []);
            var diagnostics = MsBuildDiagnosticParser.Parse(output, root);
            var succeeded = run.Succeeded ? summary.Failed == 0 : (noTests || (run.ExitCode == 8 && summary.Total == 0 && !run.TimedOut));
            var result = Finish(VerificationStage.Test, run, "test", diagnostics, summary, started, forceSuccess: succeeded);
            return result;
        }
        finally
        {
            TryDeleteDirectory(resultsDir);
        }
    }

    private VerificationResult Finish(
        VerificationStage stage, ProcessResult run, string verb, IReadOnlyList<BuildDiagnostic> diagnostics, TestSummary? tests, DateTime started, bool? forceSuccess = null)
    {
        var succeeded = forceSuccess ?? run.Succeeded;
        var list = diagnostics.ToList();
        if (!succeeded && !list.Any(d => d.Severity == DiagnosticSeverity.Error) && (tests is null || tests.Failed == 0))
        {
            var reason = run.TimedOut ? $"dotnet {verb} timed out after {run.Duration.TotalMinutes:0.#} minutes" : $"dotnet {verb} failed with exit code {run.ExitCode} without reporting an error diagnostic";
            list.Add(new BuildDiagnostic(DiagnosticSeverity.Error, SyntheticCode, reason, null, null, null, null));
        }

        return new VerificationResult(stage, succeeded, list, tests, Tail(run.CombinedOutput), DateTime.UtcNow - started);
    }

    private static ProcessSpec Spec(string workingDirectory, IReadOnlyList<string> args, SecretScope scope, TimeSpan timeout) => new()
    {
        FileName = "dotnet",
        Arguments = args,
        WorkingDirectory = workingDirectory,
        SecretScope = scope,
        Timeout = timeout,
        Environment = new Dictionary<string, string?>
        {
            ["MSBUILDDISABLENODEREUSE"] = "1",
            ["DOTNET_CLI_UI_LANGUAGE"] = "en",
            ["VSLANG"] = "1033",
            ["DOTNET_NOLOGO"] = "1",
        },
    };

    private static VerificationResult NoTargetResult(VerificationStage stage, DateTime started) => new(
        stage, false,
        [new BuildDiagnostic(DiagnosticSeverity.Error, SyntheticCode, "No SDK-style project found to build.", null, null, null, null)],
        null, "No SDK-style project found to build.", DateTime.UtcNow - started);

    internal static string Tail(string output)
    {
        var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join('\n', lines.Count > OutputTailLines ? lines.Skip(lines.Count - OutputTailLines) : lines);
    }

    private static string? SafeRead(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>True when <c>global.json</c> opts into the Microsoft.Testing.Platform runner for <c>dotnet test</c> (.NET 10).</summary>
    public static bool UsesMicrosoftTestingPlatform(string repositoryRoot)
    {
        var path = Path.Combine(repositoryRoot, "global.json");
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var top in doc.RootElement.EnumerateObject())
                {
                    if (top.NameEquals("test") || top.Name.Equals("test", StringComparison.OrdinalIgnoreCase))
                    {
                        if (top.Value.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var p in top.Value.EnumerateObject())
                            {
                                if (p.Name.Equals("runner", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String
                                    && string.Equals(p.Value.GetString(), "Microsoft.Testing.Platform", StringComparison.OrdinalIgnoreCase))
                                {
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }

        return false;
    }

    private BuildTarget? ResolveTarget(RepoContext context)
    {
        var root = context.WorkingDirectory;
        var resolved = BuildTargetResolver.Resolve(root, context.Settings.Verification);
        if (resolved is not null)
        {
            var isSolution = resolved.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || resolved.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
            return new BuildTarget(resolved, isSolution, null);
        }

        var projects = BuildTargetResolver.FindProjects(root)
            .Where(p => IsSdkStyle(Path.Combine(root, p)))
            .Select(p => Path.GetFullPath(Path.Combine(root, p)))
            .ToList();
        if (projects.Count == 0)
        {
            return null;
        }

        context.Logger.LogInformation("No unambiguous solution found; building {Count} SDK-style projects through a temporary solution outside the repository.", projects.Count);
        var dir = Path.Combine(Path.GetTempPath(), "dependency-agent-sln-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var slnx = Path.Combine(dir, "all-projects.slnx");
        var doc = new XDocument(new XElement("Solution", projects.Select(p => new XElement("Project", new XAttribute("Path", p)))));
        doc.Save(slnx);
        return new BuildTarget(slnx, true, dir);
    }

    /// <summary>SDK-style = root <c>Sdk</c> attribute, a nested <c>&lt;Sdk&gt;</c> element or an <c>&lt;Import Sdk=...&gt;</c>.</summary>
    internal static bool IsSdkStyle(string projectPath)
    {
        try
        {
            var root = XDocument.Load(projectPath).Root;
            if (root is null)
            {
                return false;
            }

            return root.Attribute("Sdk") is not null
                || root.Elements().Any(e => e.Name.LocalName == "Sdk")
                || root.Descendants().Any(e => e.Name.LocalName == "Import" && e.Attribute("Sdk") is not null);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort; the directory lives in the temp folder, not in the repository
        }
    }

    private sealed class BuildTarget : IDisposable
    {
        private readonly string? _tempDirectory;

        public BuildTarget(string argument, bool isSolution, string? tempDirectory)
        {
            Argument = argument;
            IsSolution = isSolution;
            _tempDirectory = tempDirectory;
        }

        public string Argument { get; }

        public bool IsSolution { get; }

        public void Dispose()
        {
            if (_tempDirectory is not null)
            {
                TryDeleteDirectory(_tempDirectory);
            }
        }
    }
}
