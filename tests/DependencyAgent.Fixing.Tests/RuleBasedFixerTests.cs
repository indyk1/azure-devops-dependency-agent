using System.Text;
using DependencyAgent.Core.Model;

namespace DependencyAgent.Fixing.Tests;

public class RuleBasedFixerTests : IDisposable
{
    private readonly TempDir _repo = new();
    private readonly TempDir _config = new();
    private readonly FakeProcessRunner _runner = new();

    public void Dispose()
    {
        _repo.Dispose();
        _config.Dispose();
    }

    private RuleBasedFixer Fixer() => new(_runner);

    private RepoContext Context(Action<DependencyAgent.Core.Configuration.AgentSettings>? tweak = null) =>
        Ctx.Create(_repo.Path, tweak, new RunOptions { ConfigDirectory = _config.Path });

    private void Rules(string json) => _config.Write("rewrite-rules.json", json);

    private const string ObsoleteRule = """
        {
          "$schema": "../schemas/rewrite-rules.schema.json",
          "rules": [
            { "id": "webclient", "enabled": true, "description": "d", "diagnostics": ["SYSLIB0014"], "filePattern": "**/*.cs",
              "find": "new WebClient\\(\\)", "replace": "new HttpClient()", "docs": "https://learn.microsoft.com/dotnet/fundamentals/syslib-diagnostics/syslib0014" }
          ]
        }
        """;

    [Fact]
    public async Task Rule_AppliesOnlyToFilesReferencedByTheDiagnostic()
    {
        _repo.Write("src/A.cs", "var c = new WebClient();");
        _repo.Write("src/B.cs", "var c = new WebClient();");
        Rules(ObsoleteRule);

        var result = await Fixer().FixAsync(Context(), Ctx.Request(Ctx.Failed(Ctx.Error("SYSLIB0014", "src/A.cs"))), TestContext.Current.CancellationToken);

        Assert.True(result.MadeChanges);
        Assert.Equal("var c = new HttpClient();", _repo.Read("src/A.cs"));
        Assert.Equal("var c = new WebClient();", _repo.Read("src/B.cs"));
        var change = Assert.Single(result.Changes);
        Assert.Equal(ChangeKind.RuleFix, change.Kind);
        Assert.Equal("src/A.cs", change.File);
        Assert.Equal("SYSLIB0014", change.Subject);
        Assert.Equal("https://learn.microsoft.com/dotnet/fundamentals/syslib-diagnostics/syslib0014", change.Detail);
        Assert.Contains(result.Notes, n => n.Contains("webclient") && n.Contains("src/A.cs"));
    }

    [Fact]
    public async Task Rule_UsesNewWarningsToo_AndAbsolutePaths()
    {
        _repo.Write("src/A.cs", "var c = new WebClient();");
        Rules(ObsoleteRule);
        var request = Ctx.Request(Ctx.Failed(), [Ctx.Warning("SYSLIB0014", _repo.Combine("src/A.cs"))]);

        var result = await Fixer().FixAsync(Context(), request, TestContext.Current.CancellationToken);

        Assert.True(result.MadeChanges);
        Assert.Contains("HttpClient", _repo.Read("src/A.cs"));
    }

    [Fact]
    public async Task Rule_DoesNotApplyWhenDiagnosticAbsent()
    {
        _repo.Write("src/A.cs", "var c = new WebClient();");
        Rules(ObsoleteRule);

        var result = await Fixer().FixAsync(Context(), Ctx.Request(Ctx.Failed(Ctx.Error("CS0103", "src/A.cs"))), TestContext.Current.CancellationToken);

        Assert.False(result.MadeChanges);
        Assert.Contains("WebClient", _repo.Read("src/A.cs"));
    }

    [Fact]
    public async Task DisabledRule_IsSkipped()
    {
        _repo.Write("src/A.cs", "var c = new WebClient();");
        Rules(ObsoleteRule.Replace("\"enabled\": true", "\"enabled\": false"));

        var result = await Fixer().FixAsync(Context(), Ctx.Request(Ctx.Failed(Ctx.Error("SYSLIB0014", "src/A.cs"))), TestContext.Current.CancellationToken);

        Assert.False(result.MadeChanges);
    }

    [Fact]
    public async Task Rule_RespectsFilePattern_AllowedAndProtectedPatterns()
    {
        _repo.Write("src/Security/A.cs", "var c = new WebClient();");
        _repo.Write("src/A.razor", "var c = new WebClient();");
        _repo.Write("src/Data.json", "new WebClient()");
        _repo.Write("src/Gen.g.cs", "var c = new WebClient();");
        Rules(ObsoleteRule.Replace("\"**/*.cs\"", "\"src/**/*.cs\""));

        var request = Ctx.Request(Ctx.Failed(
            Ctx.Error("SYSLIB0014", "src/Security/A.cs"),   // protected
            Ctx.Error("SYSLIB0014", "src/A.razor"),         // not matching filePattern
            Ctx.Error("SYSLIB0014", "src/Data.json"),       // not allowed / not matching
            Ctx.Error("SYSLIB0014", "../outside.cs"),       // outside the repo
            Ctx.Error("SYSLIB0014", "src/Gen.g.cs")));      // fine
        var result = await Fixer().FixAsync(Context(), request, TestContext.Current.CancellationToken);

        Assert.Equal(["src/Gen.g.cs"], result.Changes.Select(c => c.File).ToArray());
        Assert.Contains("WebClient", _repo.Read("src/Security/A.cs"));
        Assert.Contains("WebClient", _repo.Read("src/A.razor"));
        Assert.Contains("WebClient", _repo.Read("src/Data.json"));
    }

    [Fact]
    public async Task Rule_PreservesBomAndCrLf()
    {
        var path = _repo.Combine("src/A.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var original = "using System;\r\nvar c = new WebClient();\r\nvar d = 1;\r\n";
        File.WriteAllBytes(path, [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(original)]);
        Rules(ObsoleteRule);

        await Fixer().FixAsync(Context(), Ctx.Request(Ctx.Failed(Ctx.Error("SYSLIB0014", "src/A.cs"))), TestContext.Current.CancellationToken);

        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.AsSpan(0, 3).SequenceEqual(Encoding.UTF8.GetPreamble()));
        Assert.Equal("using System;\r\nvar c = new HttpClient();\r\nvar d = 1;\r\n", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
    }

    [Fact]
    public async Task Rule_PreservesUtf16AndNoBomEncodings()
    {
        _repo.Write("src/Plain.cs", "// héllo\nnew WebClient();");
        var utf16 = _repo.Combine("src/U16.cs");
        File.WriteAllBytes(utf16, [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("new WebClient();")]);
        Rules(ObsoleteRule);

        await Fixer().FixAsync(Context(), Ctx.Request(Ctx.Failed(Ctx.Error("SYSLIB0014", "src/Plain.cs"), Ctx.Error("SYSLIB0014", "src/U16.cs"))), TestContext.Current.CancellationToken);

        var plain = File.ReadAllBytes(_repo.Combine("src/Plain.cs"));
        Assert.NotEqual(0xEF, plain[0]);
        Assert.Equal("// héllo\nnew HttpClient();", Encoding.UTF8.GetString(plain));
        var u = File.ReadAllBytes(utf16);
        Assert.Equal(0xFF, u[0]);
        Assert.Equal(0xFE, u[1]);
        Assert.Equal("new HttpClient();", Encoding.Unicode.GetString(u, 2, u.Length - 2));
    }

    [Fact]
    public async Task CatastrophicRegex_TimesOutSafelyWithNote()
    {
        _repo.Write("src/A.cs", new string('a', 60) + "!");
        Rules("""{"rules":[{"id":"evil","diagnostics":["CS0618"],"find":"^(a+)+$","replace":"b"}]}""");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await Fixer().FixAsync(Context(), Ctx.Request(Ctx.Failed(Ctx.Error("CS0618", "src/A.cs"))), TestContext.Current.CancellationToken);

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30));
        Assert.False(result.MadeChanges);
        Assert.Contains(result.Notes, n => n.Contains("evil") && n.Contains("timed out"));
    }

    [Fact]
    public async Task InvalidRegexAndInvalidJson_AreReportedNotThrown()
    {
        _repo.Write("src/A.cs", "x");
        Rules("""{"rules":[{"id":"bad","diagnostics":["CS0618"],"find":"(","replace":"b"}]}""");
        var r1 = await Fixer().FixAsync(Context(), Ctx.Request(Ctx.Failed(Ctx.Error("CS0618", "src/A.cs"))), TestContext.Current.CancellationToken);
        Assert.Contains(r1.Notes, n => n.Contains("invalid regex"));

        Rules("{ not json");
        var r2 = await Fixer().FixAsync(Context(), Ctx.Request(Ctx.Failed(Ctx.Error("CS0618", "src/A.cs"))), TestContext.Current.CancellationToken);
        Assert.Contains(r2.Notes, n => n.Contains("could not be parsed"));
    }

    [Fact]
    public async Task MissingRulesFile_IsFine()
    {
        _repo.Write("src/A.cs", "x");
        var result = await Fixer().FixAsync(Context(), Ctx.Request(Ctx.Failed(Ctx.Error("CS0618", "src/A.cs"))), TestContext.Current.CancellationToken);
        Assert.False(result.MadeChanges);
    }

    [Fact]
    public async Task Disabled_DoesNothing()
    {
        _repo.Write("src/A.cs", "var c = new WebClient();");
        Rules(ObsoleteRule);
        var result = await Fixer().FixAsync(Context(s => s.RuleFixes.Enabled = false), Ctx.Request(Ctx.Failed(Ctx.Error("SYSLIB0014", "src/A.cs"))), TestContext.Current.CancellationToken);
        Assert.False(result.MadeChanges);
        Assert.Contains("WebClient", _repo.Read("src/A.cs"));
        Assert.Empty(_runner.Specs);
    }

    [Fact]
    public async Task DotnetFormat_RunsForPresentIdsOnReferencedFilesOnly_WithNoSecrets()
    {
        _repo.Write("App.csproj", "<Project />");
        _repo.Write("src/A.cs", "x");
        _repo.Write("src/B.cs", "y");
        _runner.Handler = spec =>
        {
            File.WriteAllText(_repo.Combine("src/A.cs"), "formatted");
            return new DependencyAgent.Core.Processes.ProcessResult(0, "", "", false, TimeSpan.Zero);
        };

        var result = await Fixer().FixAsync(
            Context(s => s.RuleFixes.FormatDiagnostics = ["IDE0005", "CA1822", "IDE9999"]),
            Ctx.Request(Ctx.Failed(Ctx.Error("CS0103", "src/B.cs")), [Ctx.Warning("IDE0005", "src/A.cs"), Ctx.Warning("CA1822", "src/A.cs")]),
            TestContext.Current.CancellationToken);

        var spec = Assert.Single(_runner.Specs);
        Assert.Equal("dotnet", spec.FileName);
        Assert.Equal(DependencyAgent.Core.Processes.SecretScope.None, spec.SecretScope);
        Assert.Equal(["format", "analyzers", "App.csproj", "--no-restore", "--severity", "info", "--diagnostics", "IDE0005", "CA1822", "--include", "src/A.cs"], spec.Arguments);
        Assert.Equal(["src/A.cs", "src/A.cs"], result.Changes.Select(c => c.File).ToArray());
        Assert.Equal(["CA1822", "IDE0005"], result.Changes.Select(c => c.Subject).Order().ToArray());
        Assert.True(result.MadeChanges);
    }

    [Fact]
    public async Task DotnetFormat_WithoutResolvableTarget_RunsPerProject()
    {
        _repo.Write("a/A.csproj", "<Project />");
        _repo.Write("b/B.csproj", "<Project />");
        _repo.Write("a/X.cs", "x");
        _repo.Write("b/Y.cs", "y");

        await Fixer().FixAsync(
            Context(s => s.RuleFixes.FormatDiagnostics = ["IDE0005"]),
            Ctx.Request(Ctx.Failed(Ctx.Warning("IDE0005", "a/X.cs"), Ctx.Warning("IDE0005", "b/Y.cs"))),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, _runner.Specs.Count);
        Assert.Contains(_runner.Specs, s => s.Arguments[2] == "a/A.csproj" && s.Arguments[^1] == "a/X.cs");
        Assert.Contains(_runner.Specs, s => s.Arguments[2] == "b/B.csproj" && s.Arguments[^1] == "b/Y.cs");
    }

    [Fact]
    public async Task DotnetFormat_NotRunWhenDiagnosticNotPresent()
    {
        _repo.Write("App.csproj", "<Project />");
        _repo.Write("src/A.cs", "x");
        await Fixer().FixAsync(Context(s => s.RuleFixes.FormatDiagnostics = ["IDE0005"]), Ctx.Request(Ctx.Failed(Ctx.Error("CS0103", "src/A.cs"))), TestContext.Current.CancellationToken);
        Assert.Empty(_runner.Specs);
    }

    [Fact]
    public async Task DotnetFormat_Failure_IsNoted()
    {
        _repo.Write("App.csproj", "<Project />");
        _repo.Write("src/A.cs", "x");
        _runner.Handler = _ => new DependencyAgent.Core.Processes.ProcessResult(1, "", "boom", false, TimeSpan.Zero);
        var result = await Fixer().FixAsync(Context(s => s.RuleFixes.FormatDiagnostics = ["IDE0005"]), Ctx.Request(Ctx.Failed(Ctx.Warning("IDE0005", "src/A.cs"))), TestContext.Current.CancellationToken);
        Assert.False(result.MadeChanges);
        Assert.Contains(result.Notes, n => n.Contains("failed") && n.Contains("boom"));
    }
}
