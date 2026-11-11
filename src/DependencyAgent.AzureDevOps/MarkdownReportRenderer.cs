using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DependencyAgent.Core;
using DependencyAgent.Core.Configuration;
using DependencyAgent.Core.Model;
using DependencyAgent.Core.Processes;

namespace DependencyAgent.AzureDevOps;

/// <summary>
/// Renders run reports as markdown for PR descriptions, PR comments and the pipeline summary.
/// Output is deterministic and every dynamic string is neutralized against <c>##vso[</c> logging commands.
/// </summary>
public sealed partial class MarkdownReportRenderer : IReportRenderer
{
    /// <summary>Hidden marker ending every PR description.</summary>
    public const string FooterMarker = "<!-- dependency-agent -->";

    public const string TruncationNote = "…full report in the pinned comment";

    private const int TailChars = 6000;
    private const int MaxErrors = 200;

    public string RenderPullRequestTitle(RepoRunReport report, AgentSettings settings)
    {
        var tfm = string.IsNullOrWhiteSpace(report.TargetFramework) ? settings.TargetFramework : report.TargetFramework;
        var prefix = settings.PullRequest.TitlePrefix?.Trim() ?? "";
        var title = $"{(prefix.Length > 0 ? prefix + " " : "")}Upgrade to {tfm} (.NET LTS) and update dependencies";
        return Text(title);
    }

    public string RenderPullRequestDescription(RepoRunReport report, int maxLength = 4000)
    {
        var tail = $"\n\n{TruncationNote}\n\n{FooterMarker}\n";
        var footer = $"\n{FooterMarker}\n";
        foreach (var cap in new[] { 15, 8, 4, 0 })
        {
            var text = RenderBody(report, cap, full: false) + footer;
            if (text.Length <= maxLength)
            {
                return text;
            }
        }

        var minimal = RenderBody(report, 0, full: false);
        var keep = Math.Max(0, maxLength - tail.Length);
        if (keep < minimal.Length)
        {
            if (keep > 0 && char.IsHighSurrogate(minimal[keep - 1]))
            {
                keep--;
            }

            minimal = minimal[..keep].TrimEnd();
        }

        var result = minimal + tail;
        return result.Length <= maxLength ? result : result[..Math.Max(0, maxLength)];
    }

    public string RenderFullReport(RepoRunReport report)
    {
        var sb = new StringBuilder(RenderBody(report, int.MaxValue, full: true));
        AppendFullExtras(sb, report);
        sb.Append('\n').Append(FooterMarker).Append('\n');
        return sb.ToString();
    }

    public string RenderRunSummary(IReadOnlyList<RepoRunReport> reports)
    {
        var ordered = reports.OrderBy(r => r.Project, StringComparer.Ordinal).ThenBy(r => r.Repository, StringComparer.Ordinal).ToList();
        var sb = new StringBuilder();
        sb.Append("# Dependency agent run summary\n\n");
        if (ordered.Count == 0)
        {
            sb.Append("_No repositories were processed._\n");
            return sb.ToString();
        }

        sb.Append("| Repository | Outcome | Changes | Build | Tests | PR |\n|---|---|---|---|---|---|\n");
        foreach (var r in ordered)
        {
            var pr = r.PullRequest is null ? "—" : $"[#{r.PullRequest.Id}]({Url(r.PullRequest.Url)}){(r.PullRequest.IsDraft ? " (draft)" : "")}";
            sb.Append($"| {Cell($"{r.Project}/{r.Repository}")} | {OutcomeLabel(r.Outcome)}{(r.DryRun ? " (dry run)" : "")} | {r.Changes.Count} | {BuildCell(r.Build)} | {TestsCell(r.Tests)} | {pr} |\n");
        }

        sb.Append('\n');
        var counts = string.Join(", ", Enum.GetValues<RunOutcome>()
            .Select(o => (o, n: ordered.Count(r => r.Outcome == o)))
            .Where(x => x.n > 0)
            .Select(x => $"{x.n} {OutcomeLabel(x.o)}"));
        sb.Append($"**Totals:** {ordered.Count} repositories — {counts}; {ordered.Sum(r => r.Changes.Count)} changes; {ordered.Count(r => r.PullRequest is not null)} pull requests.\n");
        return sb.ToString();
    }

    // ---- body ------------------------------------------------------------------------------------------------

    private static string RenderBody(RepoRunReport report, int rowCap, bool full)
    {
        var sb = new StringBuilder();
        var green = report.Outcome == RunOutcome.Green;
        var tfm = string.IsNullOrWhiteSpace(report.TargetFramework) ? "target framework" : report.TargetFramework;

        sb.Append("## ").Append(report.Outcome switch
        {
            RunOutcome.Green => "✅ Ready for review",
            RunOutcome.Draft => "⚠️ Draft — needs attention",
            RunOutcome.UpToDate => "➖ Already up to date",
            RunOutcome.Skipped => "⏭️ Skipped",
            _ => "❌ Failed",
        }).Append("\n\n");
        sb.Append($"Upgrade of `{Text($"{report.Project}/{report.Repository}")}` to `{Text(tfm)}`; base branch `{Text(report.BaseBranch)}`, agent branch `{Text(report.Branch)}`.\n\n");

        if (!green && report.FailureReasons.Count > 0)
        {
            sb.Append("**Why it is not green:**\n");
            foreach (var reason in report.FailureReasons.Take(full ? int.MaxValue : 5))
            {
                sb.Append("- ").Append(Text(reason)).Append('\n');
            }

            if (!full && report.FailureReasons.Count > 5)
            {
                sb.Append($"- … +{report.FailureReasons.Count - 5} more\n");
            }

            sb.Append('\n');
        }

        AppendCounts(sb, report);
        AppendChangeTables(sb, report, rowCap);
        AppendCodeFixes(sb, report, rowCap);
        AppendVerification(sb, report);
        AppendGuard(sb, report, rowCap);
        AppendChecklist(sb, report);
        return sb.ToString();
    }

    private static void AppendCounts(StringBuilder sb, RepoRunReport report)
    {
        int Count(ChangeKind k) => report.Changes.Count(c => c.Kind == k);
        var packages = report.Changes.Where(c => c.Kind == ChangeKind.Package).Select(c => c.Subject).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        sb.Append("### Summary\n\n");
        sb.Append($"- Target framework changes: {Count(ChangeKind.TargetFramework)}\n");
        sb.Append($"- Packages updated: {packages}\n");
        sb.Append($"- SDK / Docker changes: {Count(ChangeKind.Sdk) + Count(ChangeKind.DockerImage)}\n");
        sb.Append($"- Code fixes: {Count(ChangeKind.RuleFix)} rule-based, {Count(ChangeKind.AiFix)} AI\n\n");
    }

    private static void AppendChangeTables(StringBuilder sb, RepoRunReport report, int rowCap)
    {
        var tfm = report.Changes.Where(c => c.Kind == ChangeKind.TargetFramework)
            .OrderBy(c => c.File, StringComparer.Ordinal).ThenBy(c => c.Subject, StringComparer.Ordinal).ToList();
        if (tfm.Count > 0)
        {
            sb.Append("### Target framework\n\n| File | From | To |\n|---|---|---|\n");
            foreach (var c in tfm.Take(rowCap))
            {
                sb.Append($"| {Cell(c.File)} | {Cell(c.From ?? "")} | {Cell(c.To ?? "")} |\n");
            }

            More(sb, tfm.Count, rowCap);
        }

        var packages = report.Changes.Where(c => c.Kind == ChangeKind.Package)
            .GroupBy(c => (Id: c.Subject, c.From, c.To))
            .OrderBy(g => g.Key.Id, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Key.From, StringComparer.Ordinal)
            .ToList();
        if (packages.Count > 0)
        {
            sb.Append("### NuGet packages\n\n| Package | From | To |\n|---|---|---|\n");
            foreach (var g in packages.Take(rowCap))
            {
                var files = g.Select(c => c.File).Distinct(StringComparer.Ordinal).Count();
                sb.Append($"| {Cell(g.Key.Id)}{(files > 1 ? $" (×{files})" : "")} | {Cell(g.Key.From ?? "")} | {Cell(g.Key.To ?? "")} |\n");
            }

            More(sb, packages.Count, rowCap);
        }

        var other = report.Changes.Where(c => c.Kind is ChangeKind.Sdk or ChangeKind.DockerImage)
            .OrderBy(c => c.Kind).ThenBy(c => c.File, StringComparer.Ordinal).ThenBy(c => c.Subject, StringComparer.Ordinal).ToList();
        if (other.Count > 0)
        {
            sb.Append("### SDK and Docker\n\n| Kind | File | Subject | From | To |\n|---|---|---|---|---|\n");
            foreach (var c in other.Take(rowCap))
            {
                sb.Append($"| {(c.Kind == ChangeKind.Sdk ? "SDK" : "Docker")} | {Cell(c.File)} | {Cell(c.Subject)} | {Cell(c.From ?? "")} | {Cell(c.To ?? "")} |\n");
            }

            More(sb, other.Count, rowCap);
        }
    }

    private static void More(StringBuilder sb, int total, int cap)
    {
        if (total > cap)
        {
            sb.Append($"\n_+{total - cap} more_\n");
        }

        sb.Append('\n');
    }

    private static void AppendCodeFixes(StringBuilder sb, RepoRunReport report, int rowCap)
    {
        var rule = report.Changes.Where(c => c.Kind == ChangeKind.RuleFix)
            .GroupBy(c => c.Subject, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        var ai = report.Ai;
        var aiUsed = ai.Provider != "none" && (ai.Iterations > 0 || ai.FilesTouched.Count > 0 || ai.RevertedFiles.Count > 0);
        if (rule.Count == 0 && !aiUsed)
        {
            return;
        }

        sb.Append("### Code fixes\n\n");
        if (rule.Count > 0)
        {
            sb.Append("**Rule-based fixes**\n");
            foreach (var g in rule.Take(rowCap))
            {
                var files = g.Select(c => c.File).Distinct(StringComparer.Ordinal).Count();
                sb.Append($"- `{Text(g.Key)}` — {files} file{(files == 1 ? "" : "s")}\n");
            }

            if (rule.Count > rowCap)
            {
                sb.Append($"- _+{rule.Count - rowCap} more_\n");
            }

            sb.Append('\n');
        }

        if (aiUsed)
        {
            sb.Append($"**AI fixes** — provider `{Text(ai.Provider)}`, {ai.Iterations} iteration{(ai.Iterations == 1 ? "" : "s")}, {ai.FilesTouched.Count} file{(ai.FilesTouched.Count == 1 ? "" : "s")} touched, {ai.RevertedFiles.Count} reverted by guards\n");
            AppendFileList(sb, "Touched", ai.FilesTouched, rowCap);
            AppendFileList(sb, "Reverted", ai.RevertedFiles, rowCap);
            sb.Append('\n');
        }
    }

    private static void AppendFileList(StringBuilder sb, string label, IReadOnlyList<string> files, int cap)
    {
        if (files.Count == 0)
        {
            return;
        }

        var shown = files.OrderBy(f => f, StringComparer.Ordinal).Take(cap).Select(f => $"`{Text(f)}`");
        sb.Append($"- {label}: {string.Join(", ", shown)}{(files.Count > cap ? $" _+{files.Count - Math.Min(cap, files.Count)} more_" : "")}\n");
    }

    private static void AppendVerification(StringBuilder sb, RepoRunReport report)
    {
        sb.Append("### Verification\n\n");
        sb.Append($"- Build: {(report.Build is null ? "not run" : report.Build.Succeeded ? "✅ succeeded" : $"❌ failed ({report.Build.Stage}, {report.Build.Errors.Count()} errors)")}\n");
        sb.Append($"- Tests: {(report.Tests is null ? "not run" : TestsDetail(report.Tests))}\n");
        sb.Append($"- New warnings: {report.NewWarnings.Count}\n\n");
    }

    private static void AppendGuard(StringBuilder sb, RepoRunReport report, int rowCap)
    {
        sb.Append("### Guards\n\n");
        if (report.Guard is null)
        {
            sb.Append("- Not evaluated\n\n");
            return;
        }

        var g = report.Guard;
        sb.Append($"- {(g.Passed ? "✅ Passed" : "❌ Violations")} — {g.ChangedFiles} files, {g.ChangedLines} lines changed by code fixes\n");
        foreach (var v in g.Violations.Take(rowCap))
        {
            sb.Append("  - ").Append(Text(v)).Append('\n');
        }

        if (g.Violations.Count > rowCap)
        {
            sb.Append($"  - _+{g.Violations.Count - rowCap} more_\n");
        }

        sb.Append('\n');
    }

    private static void AppendChecklist(StringBuilder sb, RepoRunReport report)
    {
        sb.Append("### Reviewer checklist\n\n");
        sb.Append("- [ ] Build and tests are green in your own PR validation pipeline\n");
        if (report.Changes.Any(c => c.Kind == ChangeKind.AiFix) || report.Ai.FilesTouched.Count > 0)
        {
            sb.Append("- [ ] Review AI-made edits for behavioural changes (a green build does not prove equivalent behaviour)\n");
        }

        if (report.Notes.Any(n => DockerOsRegex().IsMatch(n)))
        {
            sb.Append("- [ ] Docker base image OS/distro changed — check native dependencies, user/permissions and image size\n");
        }

        if (report.Notes.Any(n => DroppedPinRegex().IsMatch(n)))
        {
            sb.Append("- [ ] Patch-level version pins were dropped — confirm that floating versions are acceptable\n");
        }

        sb.Append('\n');
    }

    // ---- full report extras ----------------------------------------------------------------------------------

    private static void AppendFullExtras(StringBuilder sb, RepoRunReport report)
    {
        sb.Append("---\n\n## Details\n\n");
        sb.Append($"- Outcome: {report.Outcome}{(report.DryRun ? " (dry run)" : "")}\n");
        sb.Append($"- Started: {report.StartedAt.ToString("u", CultureInfo.InvariantCulture)}\n");
        sb.Append($"- Duration: {report.Duration.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)}\n\n");

        if (report.Changes.Count > 0)
        {
            sb.Append("### All changes\n\n| Kind | File | Subject | From | To | Detail |\n|---|---|---|---|---|---|\n");
            foreach (var c in report.Changes.OrderBy(c => c.Kind).ThenBy(c => c.File, StringComparer.Ordinal).ThenBy(c => c.Subject, StringComparer.Ordinal))
            {
                sb.Append($"| {c.Kind} | {Cell(c.File)} | {Cell(c.Subject)} | {Cell(c.From ?? "")} | {Cell(c.To ?? "")} | {Cell(c.Detail ?? "")} |\n");
            }

            sb.Append('\n');
        }

        if (report.Notes.Count > 0)
        {
            sb.Append("### Notes\n\n");
            foreach (var n in report.Notes)
            {
                sb.Append("- ").Append(Text(n)).Append('\n');
            }

            sb.Append('\n');
        }

        var errors = new[] { report.Build, report.Tests }.Where(v => v is not null).SelectMany(v => v!.Errors).ToList();
        if (errors.Count > 0)
        {
            sb.Append($"### Errors ({errors.Count})\n\n");
            foreach (var e in errors.Take(MaxErrors))
            {
                sb.Append($"- `{Text(e.Code)}` {Text(Location(e))}: {Text(e.Message)}\n");
            }

            if (errors.Count > MaxErrors)
            {
                sb.Append($"- _+{errors.Count - MaxErrors} more_\n");
            }

            sb.Append('\n');
        }

        if (report.NewWarnings.Count > 0)
        {
            sb.Append($"### New warnings ({report.NewWarnings.Count})\n\n");
            foreach (var w in report.NewWarnings.Take(MaxErrors))
            {
                sb.Append($"- `{Text(w.Code)}` {Text(Location(w))}: {Text(w.Message)}\n");
            }

            if (report.NewWarnings.Count > MaxErrors)
            {
                sb.Append($"- _+{report.NewWarnings.Count - MaxErrors} more_\n");
            }

            sb.Append('\n');
        }

        if (report.Tests?.Tests is { FailedTests.Count: > 0 } summary)
        {
            sb.Append($"### Failed tests ({summary.FailedTests.Count})\n\n");
            foreach (var t in summary.FailedTests)
            {
                sb.Append($"- `{Text(t.Name)}`\n");
                if (!string.IsNullOrWhiteSpace(t.Message))
                {
                    sb.Append(Fence(Clip(t.Message!, 1500, keepEnd: false)));
                }
            }

            sb.Append('\n');
        }

        if (report.Ai.Summaries.Count > 0)
        {
            sb.Append("### AI summaries\n\n");
            var i = 1;
            foreach (var s in report.Ai.Summaries)
            {
                sb.Append($"**Iteration {i++}**\n\n{Text(Clip(s, 4000, keepEnd: false))}\n\n");
            }
        }

        foreach (var (label, v) in new[] { ("Build", report.Build), ("Test", report.Tests) })
        {
            if (v is null || string.IsNullOrWhiteSpace(v.OutputTail))
            {
                continue;
            }

            sb.Append($"<details><summary>{label} output (tail)</summary>\n\n");
            sb.Append(Fence(Clip(v.OutputTail, TailChars, keepEnd: true)));
            sb.Append("\n</details>\n\n");
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    private static string Location(BuildDiagnostic d) =>
        d.File is null ? "" : d.Line is { } l ? $"{d.File}({l})" : d.File;

    private static string OutcomeLabel(RunOutcome o) => o switch
    {
        RunOutcome.Green => "✅ Green",
        RunOutcome.Draft => "⚠️ Draft",
        RunOutcome.UpToDate => "➖ Up to date",
        RunOutcome.Skipped => "⏭️ Skipped",
        _ => "❌ Failed",
    };

    private static string BuildCell(VerificationResult? b) => b is null ? "—" : b.Succeeded ? "✅" : "❌";

    private static string TestsCell(VerificationResult? t) =>
        t is null ? "—" : t.Tests is null ? (t.Succeeded ? "✅" : "❌") : t.Tests.Failed > 0 || !t.Succeeded ? $"❌ {t.Tests.Failed} failed" : $"✅ {t.Tests.Passed}";

    private static string TestsDetail(VerificationResult t) =>
        t.Tests is null
            ? (t.Succeeded ? "✅ passed" : "❌ failed")
            : $"{(t.Succeeded && t.Tests.Failed == 0 ? "✅" : "❌")} {t.Tests.Passed} passed, {t.Tests.Failed} failed, {t.Tests.Skipped} skipped";

    /// <summary>Single-line text with logging commands neutralized.</summary>
    private static string Text(string? value) =>
        PipelineCommandNeutralizer.Neutralize(value ?? "").Replace("\r", "").Replace('\n', ' ').Trim();

    /// <summary>Table cell: neutralized, single line, pipes escaped.</summary>
    private static string Cell(string? value) => Text(value).Replace("|", "\\|");

    private static string Url(string url) => Text(url).Replace(" ", "%20").Replace(")", "%29");

    private static string Fence(string content)
    {
        var safe = PipelineCommandNeutralizer.Neutralize(content).Replace("```", "'''").TrimEnd();
        return $"```text\n{safe}\n```\n";
    }

    private static string Clip(string value, int max, bool keepEnd)
    {
        if (value.Length <= max)
        {
            return value;
        }

        return keepEnd ? "…" + value[^max..] : value[..max] + "…";
    }

    [GeneratedRegex(@"base[ -]?os|distro|debian|ubuntu|alpine|bookworm|trixie|noble|jammy|azure ?linux|mariner", RegexOptions.IgnoreCase)]
    private static partial Regex DockerOsRegex();

    [GeneratedRegex(@"patch[ -]pin|pin.*drop|drop.*pin|unpinned|floating", RegexOptions.IgnoreCase)]
    private static partial Regex DroppedPinRegex();
}
