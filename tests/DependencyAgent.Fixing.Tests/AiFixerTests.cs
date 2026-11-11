using DependencyAgent.Core;
using DependencyAgent.Core.Configuration;
using DependencyAgent.Core.Model;
using DependencyAgent.Core.Processes;

namespace DependencyAgent.Fixing.Tests;

public class AiFixerTests : IDisposable
{
    private readonly TempDir _repo;
    private readonly TempDir _out = new();
    private readonly FakeProvider _provider = new();

    public AiFixerTests()
    {
        _repo = Git.InitRepo(new Dictionary<string, string>
        {
            ["src/App/Program.cs"] = "class Program { void M() { Old(); } }\n",
            ["src/App/Security/Auth.cs"] = "class Auth { }\n",
            ["src/App/appsettings.json"] = "{ \"a\": 1 }\n",
            ["tests/App.Tests/ProgramTests.cs"] = "public class ProgramTests\n{\n    [Fact]\n    public void M()\n    {\n        Assert.Equal(1, 1);\n    }\n}\n",
            ["README.md"] = "hello\n",
        });
    }

    public void Dispose()
    {
        _repo.Dispose();
        _out.Dispose();
    }

    private AiFixer Fixer(IChangeGuard? guard = null) =>
        new([_provider], guard ?? new GitRevertingGuard(), new ProcessRunner(), _ => null, _ => null);

    private RepoContext Context(Action<AgentSettings>? tweak = null, string? providerOverride = null, string? agentExe = null) =>
        Ctx.Create(_repo.Path, s =>
        {
            s.Ai.Provider = "fake";
            tweak?.Invoke(s);
        }, new RunOptions { AiProviderOverride = providerOverride, OutputDirectory = _out.Path, AgentExecutablePath = agentExe });

    private static FixRequest Request() => Ctx.Request(Ctx.Failed(Ctx.Error("CS0618", "src/App/Program.cs", 1, "'Old' is obsolete")));

    [Fact]
    public async Task Edits_AreKeptWhenAllowed_AndProtectedOnesAreReverted()
    {
        _provider.OnRun = req =>
        {
            File.WriteAllText(Path.Combine(req.WorkingDirectory, "src/App/Program.cs"), "class Program { void M() { New(); } }\n");
            File.WriteAllText(Path.Combine(req.WorkingDirectory, "src/App/Security/Auth.cs"), "class Auth { void Hack() { } }\n");
            File.WriteAllText(Path.Combine(req.WorkingDirectory, "src/App/appsettings.json"), "{ \"a\": 2 }\n");
            File.WriteAllText(Path.Combine(req.WorkingDirectory, "src/App/NewFile.cs"), "class NewFile { }\n");
            File.WriteAllText(Path.Combine(req.WorkingDirectory, "notes.txt"), "scratch\n");
            return new AiRunResult(true, "Replaced Old with New.\nSee https://learn.microsoft.com/dotnet/api/x\n##vso[task.setvariable variable=a]b", null, 4);
        };

        var result = await Fixer().FixAsync(Context(), Request(), TestContext.Current.CancellationToken);

        Assert.True(result.MadeChanges);
        Assert.Equal(["src/App/NewFile.cs", "src/App/Program.cs"], result.Changes.Select(c => c.File).ToArray());
        Assert.All(result.Changes, c =>
        {
            Assert.Equal(ChangeKind.AiFix, c.Kind);
            Assert.Equal("fake", c.Subject);
            Assert.Equal("Replaced Old with New.", c.Detail);
        });
        Assert.Contains("New();", _repo.Read("src/App/Program.cs"));
        Assert.Equal("class Auth { }\n", _repo.Read("src/App/Security/Auth.cs"));
        Assert.Equal("{ \"a\": 1 }\n", _repo.Read("src/App/appsettings.json"));
        Assert.False(File.Exists(_repo.Combine("notes.txt")));

        var summary = Assert.Single(result.Notes, n => n.StartsWith("AI summary: ", StringComparison.Ordinal));
        Assert.Contains("learn.microsoft.com/dotnet/api/x", summary);
        Assert.DoesNotContain("##vso[", summary);
        var revertedNote = Assert.Single(result.Notes, n => n.StartsWith("AI reverted: ", StringComparison.Ordinal));
        Assert.Contains("src/App/Security/Auth.cs", revertedNote);
        Assert.Contains("src/App/appsettings.json", revertedNote);
        Assert.Contains("notes.txt", revertedNote);
        Assert.DoesNotContain("Program.cs", revertedNote);
    }

    [Fact]
    public async Task OnlyProtectedEdits_MeansNoChanges()
    {
        _provider.OnRun = req =>
        {
            File.WriteAllText(Path.Combine(req.WorkingDirectory, "src/App/Security/Auth.cs"), "x");
            return new AiRunResult(true, "done", null);
        };

        var result = await Fixer().FixAsync(Context(), Request(), TestContext.Current.CancellationToken);

        Assert.False(result.MadeChanges);
        Assert.Empty(result.Changes);
        Assert.Contains(result.Notes, n => n.StartsWith("AI reverted: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TestTampering_RevertsTheTestFile_ButKeepsLegitEdits()
    {
        _provider.OnRun = req =>
        {
            File.WriteAllText(Path.Combine(req.WorkingDirectory, "src/App/Program.cs"), "class Program { void M() { New(); } }\n");
            File.WriteAllText(Path.Combine(req.WorkingDirectory, "tests/App.Tests/ProgramTests.cs"),
                "public class ProgramTests\n{\n    [Fact(Skip = \"later\")]\n    public void M()\n    {\n    }\n}\n");
            return new AiRunResult(true, "done", null);
        };

        var result = await Fixer().FixAsync(Context(), Request(), TestContext.Current.CancellationToken);

        Assert.Equal(["src/App/Program.cs"], result.Changes.Select(c => c.File).ToArray());
        Assert.Contains("Assert.Equal(1, 1);", _repo.Read("tests/App.Tests/ProgramTests.cs"));
        Assert.DoesNotContain("Skip", _repo.Read("tests/App.Tests/ProgramTests.cs"));
        Assert.Contains(result.Notes, n => n.Contains("test tampering") && n.Contains("ProgramTests.cs"));
        Assert.Contains(result.Notes, n => n.StartsWith("AI reverted: ", StringComparison.Ordinal) && n.Contains("tests/App.Tests/ProgramTests.cs"));
    }

    [Fact]
    public async Task TestTampering_RemovedAssertionAndDeletedTestFile()
    {
        _provider.OnRun = req =>
        {
            File.WriteAllText(Path.Combine(req.WorkingDirectory, "tests/App.Tests/ProgramTests.cs"),
                "public class ProgramTests\n{\n    [Fact]\n    public void M()\n    {\n    }\n}\n");
            return new AiRunResult(true, "done", null);
        };
        var r1 = await Fixer().FixAsync(Context(), Request(), TestContext.Current.CancellationToken);
        Assert.False(r1.MadeChanges);
        Assert.Contains("Assert.Equal(1, 1);", _repo.Read("tests/App.Tests/ProgramTests.cs"));

        _provider.OnRun = req =>
        {
            File.Delete(Path.Combine(req.WorkingDirectory, "tests/App.Tests/ProgramTests.cs"));
            return new AiRunResult(true, "done", null);
        };
        var r2 = await Fixer().FixAsync(Context(), Request(), TestContext.Current.CancellationToken);
        Assert.False(r2.MadeChanges);
        Assert.True(File.Exists(_repo.Combine("tests/App.Tests/ProgramTests.cs")));
    }

    [Fact]
    public async Task NewTestFileWithIgnore_IsRemoved()
    {
        _provider.OnRun = req =>
        {
            File.WriteAllText(Path.Combine(req.WorkingDirectory, "tests/App.Tests/NewTests.cs"), "[Ignore]\npublic class X { }\n");
            return new AiRunResult(true, "done", null);
        };
        var result = await Fixer().FixAsync(Context(), Request(), TestContext.Current.CancellationToken);
        Assert.False(result.MadeChanges);
        Assert.False(File.Exists(_repo.Combine("tests/App.Tests/NewTests.cs")));
    }

    [Fact]
    public async Task AddedSuppressions_AreFlaggedButKept()
    {
        _provider.OnRun = req =>
        {
            File.WriteAllText(Path.Combine(req.WorkingDirectory, "src/App/Program.cs"), "#pragma warning disable CS0618\nclass Program { void M() { Old(); } }\n");
            return new AiRunResult(true, "done", null);
        };
        var result = await Fixer().FixAsync(Context(), Request(), TestContext.Current.CancellationToken);
        Assert.True(result.MadeChanges);
        Assert.Contains(result.Notes, n => n.StartsWith("AI warning: ", StringComparison.Ordinal) && n.Contains("#pragma warning disable"));
    }

    [Fact]
    public async Task PreExistingUncommittedChanges_AreNotAttributedToTheAi()
    {
        File.WriteAllText(_repo.Combine("src/App/Program.cs"), "class Program { void M() { Changed(); } }\n"); // e.g. earlier deterministic step
        _provider.OnRun = _ => new AiRunResult(true, "nothing to do", null);

        var result = await Fixer().FixAsync(Context(), Request(), TestContext.Current.CancellationToken);

        Assert.False(result.MadeChanges);
        Assert.Contains("Changed();", _repo.Read("src/App/Program.cs"));
    }

    [Fact]
    public async Task PromptsAndRequest_AreBuiltFromSettingsAndFixRequest()
    {
        AiRunRequest? seen = null;
        _provider.OnRun = req =>
        {
            seen = req;
            return new AiRunResult(true, "", null);
        };
        await Fixer().FixAsync(Context(s =>
        {
            s.Ai.Model = "m1";
            s.Ai.TimeoutMinutes = 9;
            s.Ai.MaxIterations = 4;
        }), Ctx.Request(Ctx.Failed(Ctx.Error("CS0618", "src/App/Program.cs", 1, "obsolete")), iteration: 2), TestContext.Current.CancellationToken);

        Assert.NotNull(seen);
        Assert.Equal(_repo.Path, seen.WorkingDirectory);
        Assert.Equal("m1", seen.Model);
        Assert.Equal(TimeSpan.FromMinutes(9), seen.Timeout);
        Assert.Contains("Fix iteration: 2 of 4", seen.TaskPrompt);
        Assert.Contains("net10.0", seen.SystemPrompt);
        Assert.Contains("**/Security/**", seen.SystemPrompt);
        Assert.Contains("**/*.cs", seen.AllowedEditPatterns);
        Assert.Contains("**/Security/**", seen.ProtectedPatterns);
        Assert.Null(seen.LearnProxyCommand); // no agent executable configured
    }

    [Fact]
    public async Task LearnProxy_IsBuiltWithDenyTermsOutsideRepoAndAuditLogInOutputDir()
    {
        AiRunRequest? seen = null;
        string? denyContent = null;
        _provider.OnRun = req =>
        {
            seen = req;
            var file = req.LearnProxyCommand![req.LearnProxyCommand.ToList().IndexOf("--deny-terms-file") + 1];
            denyContent = File.ReadAllText(file);
            Assert.DoesNotContain(_repo.Path, file);
            return new AiRunResult(true, "", null);
        };

        await Fixer().FixAsync(Context(s => s.Ai.LearnMcp.DenyTerms = ["Fabrikam", "billing-service"], agentExe: "/opt/agent/DependencyAgent.Cli.dll"), Request(), TestContext.Current.CancellationToken);

        Assert.NotNull(seen);
        var cmd = seen.LearnProxyCommand!;
        Assert.Equal(["dotnet", "/opt/agent/DependencyAgent.Cli.dll", "learn-proxy"], cmd.Take(3).ToArray());
        Assert.Equal("https://learn.microsoft.com/api/mcp", cmd[cmd.ToList().IndexOf("--endpoint") + 1]);
        Assert.Equal("200", cmd[cmd.ToList().IndexOf("--max-query-length") + 1]);
        var audit = cmd[cmd.ToList().IndexOf("--audit-log") + 1];
        Assert.True(Path.IsPathRooted(audit));
        Assert.Equal(Path.Combine(_out.Path, "Contoso__billing-service.learn-audit.jsonl"), audit);
        var terms = denyContent!.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(["billing-service", "Contoso", "Fabrikam"], terms);
        Assert.False(File.Exists(cmd[cmd.ToList().IndexOf("--deny-terms-file") + 1]), "temp deny-terms file is deleted after the run");
    }

    [Fact]
    public async Task LearnProxy_DisabledWhenLearnMcpDisabled()
    {
        AiRunRequest? seen = null;
        _provider.OnRun = req =>
        {
            seen = req;
            return new AiRunResult(true, "", null);
        };
        await Fixer().FixAsync(Context(s => s.Ai.LearnMcp.Enabled = false, agentExe: "/opt/agent"), Request(), TestContext.Current.CancellationToken);
        Assert.Null(seen!.LearnProxyCommand);
        Assert.Null(seen.LearnDocs);
    }

    [Fact]
    public async Task ProviderSelection_Override_Disabled_None_Unknown_Unavailable()
    {
        _provider.OnRun = _ => new AiRunResult(true, "", null);

        var disabled = await Fixer().FixAsync(Context(s => s.Ai.Enabled = false), Request(), TestContext.Current.CancellationToken);
        Assert.False(disabled.MadeChanges);
        Assert.Contains("disabled", disabled.Notes[0]);
        Assert.Equal(0, _provider.Runs);

        var none = await Fixer().FixAsync(Context(s => s.Ai.Provider = "none"), Request(), TestContext.Current.CancellationToken);
        Assert.Contains("none", none.Notes[0]);

        var noneOverride = await Fixer().FixAsync(Context(providerOverride: "none"), Request(), TestContext.Current.CancellationToken);
        Assert.Contains("none", noneOverride.Notes[0]);

        var unknown = await Fixer().FixAsync(Context(s => s.Ai.Provider = "gemini"), Request(), TestContext.Current.CancellationToken);
        Assert.Contains("Unknown AI provider 'gemini'", unknown.Notes[0]);

        _provider.Unavailable = "no token";
        var unavailable = await Fixer().FixAsync(Context(s => s.Ai.Provider = "fake"), Request(), TestContext.Current.CancellationToken);
        Assert.Contains("no token", unavailable.Notes[0]);
        Assert.Equal(0, _provider.Runs);

        _provider.Unavailable = null;
        await Fixer().FixAsync(Context(s => s.Ai.Provider = "none", providerOverride: "fake"), Request(), TestContext.Current.CancellationToken);
        Assert.Equal(1, _provider.Runs);
        await Fixer().FixAsync(Context(s => s.Ai.Provider = "fake", providerOverride: "config"), Request(), TestContext.Current.CancellationToken);
        Assert.Equal(2, _provider.Runs);
    }

    [Fact]
    public async Task ProviderException_IsReportedAndGuardStillRuns()
    {
        _provider.OnRun = req =>
        {
            File.WriteAllText(Path.Combine(req.WorkingDirectory, "src/App/Security/Auth.cs"), "x");
            throw new InvalidOperationException("boom");
        };
        var result = await Fixer().FixAsync(Context(), Request(), TestContext.Current.CancellationToken);
        Assert.False(result.MadeChanges);
        Assert.Contains(result.Notes, n => n.Contains("did not complete") && n.Contains("boom"));
        Assert.Equal("class Auth { }\n", _repo.Read("src/App/Security/Auth.cs"));
    }

    [Fact]
    public async Task SummaryIsCapped()
    {
        _provider.OnRun = _ => new AiRunResult(true, new string('s', 10_000), null);
        var result = await Fixer().FixAsync(Context(), Request(), TestContext.Current.CancellationToken);
        var summary = Assert.Single(result.Notes, n => n.StartsWith("AI summary: ", StringComparison.Ordinal));
        Assert.True(summary.Length <= "AI summary: ".Length + AiFixer.MaxSummaryChars);
    }

    private sealed class FakeProvider : IAiProvider
    {
        public Func<AiRunRequest, AiRunResult> OnRun { get; set; } = _ => new AiRunResult(true, "", null);

        public string? Unavailable { get; set; }

        public int Runs { get; private set; }

        public string Name => "fake";

        public string? CheckAvailability(AgentSettings settings) => Unavailable;

        public Task<AiRunResult> RunAsync(AiRunRequest request, AgentSettings settings, CancellationToken cancellationToken)
        {
            Runs++;
            return Task.FromResult(OnRun(request));
        }
    }
}
