using DependencyAgent.Core.Configuration;
using DependencyAgent.Core.Model;

namespace DependencyAgent.AzureDevOps.Tests;

public class RendererTests
{
    private readonly MarkdownReportRenderer _r = new();

    [Fact]
    public void Title_uses_prefix_and_framework()
    {
        var title = _r.RenderPullRequestTitle(TestSupport.SampleReport(), new AgentSettings());
        Assert.Equal("[dependency-agent] Upgrade to net10.0 (.NET LTS) and update dependencies", title);
    }

    [Fact]
    public void Green_description_has_ready_status_tables_and_footer()
    {
        var d = _r.RenderPullRequestDescription(TestSupport.SampleReport());
        Assert.Contains("✅ Ready for review", d);
        Assert.Contains("| src/A/A.csproj | net8.0 | net10.0 |", d);
        Assert.Contains("| Newtonsoft.Json (×2) | 12.0.1 | 13.0.3 |", d);
        Assert.Contains("| Docker | Dockerfile | mcr.microsoft.com/dotnet/aspnet | 8.0 | 10.0 |", d);
        Assert.Contains("9 passed, 0 failed, 1 skipped", d);
        Assert.Contains("Reviewer checklist", d);
        Assert.EndsWith("<!-- dependency-agent -->\n", d);
        Assert.DoesNotContain("Why it is not green", d);
    }

    [Fact]
    public void Draft_description_lists_failure_reasons_and_ai_checklist()
    {
        var report = TestSupport.SampleReport(RunOutcome.Draft);
        report.FailureReasons.Add("Build failed with 3 errors");
        report.Changes.Add(new ChangeRecord(ChangeKind.AiFix, "src/A/X.cs", "CS0246"));
        report.Ai.Provider = "copilot";
        report.Ai.Iterations = 2;
        report.Ai.FilesTouched.Add("src/A/X.cs");
        report.Ai.RevertedFiles.Add("src/Security/Y.cs");
        report.Notes.Add("Docker base image OS changed from Debian bookworm to Alpine");
        report.Notes.Add("Dropped patch pin for Foo");
        var d = _r.RenderPullRequestDescription(report);
        Assert.Contains("⚠️ Draft — needs attention", d);
        Assert.Contains("Build failed with 3 errors", d);
        Assert.Contains("provider `copilot`, 2 iterations, 1 file touched, 1 reverted", d);
        Assert.Contains("AI-made edits", d);
        Assert.Contains("Docker base image OS", d);
        Assert.Contains("pins were dropped", d);
    }

    [Fact]
    public void Description_never_exceeds_limit_with_huge_input()
    {
        var report = TestSupport.SampleReport(RunOutcome.Draft);
        for (var i = 0; i < 2000; i++)
        {
            report.Changes.Add(new ChangeRecord(ChangeKind.Package, $"src/P{i}/P.csproj", $"Package.Number{i}", "1.0.0", "2.0.0"));
            report.FailureReasons.Add(new string('x', 500));
            report.Notes.Add("note " + i);
        }

        foreach (var max in new[] { 4000, 1500, 600, 200 })
        {
            var d = _r.RenderPullRequestDescription(report, max);
            Assert.True(d.Length <= max, $"{d.Length} > {max}");
        }
    }

    [Fact]
    public void Row_cap_adds_more_marker_and_truncation_note_appears_when_needed()
    {
        var report = TestSupport.SampleReport();
        for (var i = 0; i < 100; i++)
        {
            report.Changes.Add(new ChangeRecord(ChangeKind.Package, "a.csproj", $"Pkg{i:000}", "1", "2"));
        }

        var d = _r.RenderPullRequestDescription(report);
        Assert.Contains("more_", d);
        Assert.DoesNotContain("Pkg099", d);

        var tiny = _r.RenderPullRequestDescription(report, 300);
        Assert.Contains(MarkdownReportRenderer.TruncationNote, tiny);
        Assert.EndsWith(MarkdownReportRenderer.FooterMarker + "\n", tiny);
    }

    [Fact]
    public void Pipeline_commands_are_neutralized_everywhere()
    {
        var report = TestSupport.SampleReport(RunOutcome.Draft);
        report.FailureReasons.Add("##vso[task.setvariable variable=x]1");
        report.Notes.Add("##[error]boom");
        report.Changes.Add(new ChangeRecord(ChangeKind.Package, "a.csproj", "##vso[task.complete result=Failed]", "1", "2"));
        report.Tests = new VerificationResult(VerificationStage.Test, false, [], new TestSummary(1, 0, 1, 0, [new FailedTest("T##vso[x]", "##vso[task.logissue]msg")]), "##vso[tail]", TimeSpan.Zero);
        report.Ai.Summaries.Add("##vso[task.setvariable]ai");
        foreach (var text in new[]
        {
            _r.RenderPullRequestDescription(report), _r.RenderFullReport(report), _r.RenderRunSummary([report]),
            _r.RenderPullRequestTitle(report, new AgentSettings { PullRequest = { TitlePrefix = "##vso[x]" } }),
        })
        {
            Assert.DoesNotContain("##vso[", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("##[", text);
        }
    }

    [Fact]
    public void Pipes_are_escaped_in_table_cells()
    {
        var report = TestSupport.SampleReport();
        report.Changes.Add(new ChangeRecord(ChangeKind.Package, "a.csproj", "We|rd", "1|0", "2", "de|tail"));
        Assert.Contains("| We\\|rd | 1\\|0 | 2 |", _r.RenderPullRequestDescription(report));
        Assert.Contains("de\\|tail", _r.RenderFullReport(report));
        report.Repository = "re|po";
        Assert.Contains("Proj One/re\\|po", _r.RenderRunSummary([report]));
    }

    [Fact]
    public void Full_report_contains_details()
    {
        var report = TestSupport.SampleReport(RunOutcome.Draft);
        report.Notes.Add("a note");
        report.Build = new VerificationResult(
            VerificationStage.Build, false,
            [new BuildDiagnostic(DiagnosticSeverity.Error, "CS1002", "; expected", "src/A/P.cs", 3, 4, "A"), new BuildDiagnostic(DiagnosticSeverity.Warning, "CS8600", "warn", null, null, null, null)],
            null, "last lines ``` of output", TimeSpan.Zero);
        report.NewWarnings.Add(new BuildDiagnostic(DiagnosticSeverity.Warning, "CS8600", "possible null", "src/A/Q.cs", 9, 1, "A"));
        report.Tests = new VerificationResult(VerificationStage.Test, false, [], new TestSummary(2, 1, 1, 0, [new FailedTest("Ns.Class.Test", "Expected 1 but was 2")]), "test tail", TimeSpan.Zero);
        report.Ai.Summaries.Add("Fixed nullable annotations");
        var full = _r.RenderFullReport(report);
        Assert.Contains("### All changes", full);
        Assert.Contains("a note", full);
        Assert.Contains("`CS1002` src/A/P.cs(3): ; expected", full);
        Assert.Contains("### New warnings (1)", full);
        Assert.Contains("Ns.Class.Test", full);
        Assert.Contains("Expected 1 but was 2", full);
        Assert.Contains("Fixed nullable annotations", full);
        Assert.Contains("<details><summary>Build output (tail)</summary>", full);
        Assert.Contains("''' of output", full);
    }

    [Fact]
    public void Full_report_caps_errors_at_200()
    {
        var report = TestSupport.SampleReport(RunOutcome.Draft);
        var diags = Enumerable.Range(0, 250).Select(i => new BuildDiagnostic(DiagnosticSeverity.Error, "CS0001", $"err{i}", "f.cs", i, 1, null)).ToList();
        report.Build = new VerificationResult(VerificationStage.Build, false, diags, null, "", TimeSpan.Zero);
        var full = _r.RenderFullReport(report);
        Assert.Contains("err199", full);
        Assert.DoesNotContain("err200", full);
        Assert.Contains("+50 more", full);
    }

    [Fact]
    public void Output_is_deterministic()
    {
        var a = TestSupport.SampleReport();
        var b = TestSupport.SampleReport();
        b.Changes.Reverse();
        Assert.Equal(_r.RenderFullReport(a), _r.RenderFullReport(b));
        Assert.Equal(_r.RenderPullRequestDescription(a), _r.RenderPullRequestDescription(b));
    }

    [Fact]
    public void Run_summary_has_rows_and_totals()
    {
        var green = TestSupport.SampleReport();
        green.PullRequest = new PullRequestInfo(5, "https://dev.azure.com/o/p/_git/r/pullrequest/5", false, true);
        var failed = new RepoRunReport { Project = "A", Repository = "Z", Outcome = RunOutcome.Failed };
        var summary = _r.RenderRunSummary([green, failed]);
        Assert.Contains("| Repository | Outcome | Changes | Build | Tests | PR |", summary);
        Assert.Contains("[#5](https://dev.azure.com/o/p/_git/r/pullrequest/5)", summary);
        Assert.True(summary.IndexOf("A/Z", StringComparison.Ordinal) < summary.IndexOf("Proj One/My Repo", StringComparison.Ordinal));
        Assert.Contains("2 repositories", summary);
        Assert.Contains("5 changes", summary);
        Assert.Contains("✅ 9", summary);
        Assert.Contains("_No repositories", _r.RenderRunSummary([]));
    }
}
