using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DependencyAgent.Core.Model;

namespace DependencyAgent.Verification;

/// <summary>Parses TRX files (VSTest schema) and <c>dotnet test</c> console summaries.</summary>
public static partial class TestResultParser
{
    public const int MaxFailedTests = 100;
    private const int MaxMessageLines = 8;
    private const int MaxStackLines = 5;
    private const int MaxMessageChars = 2000;

    [GeneratedRegex(@"(?:Passed|Failed)!\s+-\s+Failed:\s+(?<f>\d+),\s+Passed:\s+(?<p>\d+),\s+Skipped:\s+(?<s>\d+),\s+Total:\s+(?<t>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex VsTestSummary();

    // Aggregated summary printed by the .NET 10 Microsoft.Testing.Platform orchestrator.
    [GeneratedRegex(@"Test summary:\s*total:\s*(?<t>\d+),\s*failed:\s*(?<f>\d+),\s*succeeded:\s*(?<p>\d+),\s*skipped:\s*(?<s>\d+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MtpAggregate();

    [GeneratedRegex(@"^\s*(?<k>total|failed|succeeded|skipped):\s*(?<v>\d+)\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MtpCounter();

    [GeneratedRegex(@"^\s*Test run summary:", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MtpRunSummary();

    [GeneratedRegex(@"^\s+Failed\s+(?<name>\S.*?)\s+\[\d+(?:\.\d+)?\s*(?:ms|s|m|µs)\]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex VsTestFailedLine();

    [GeneratedRegex(@"^\s*failed\s+(?<name>\S.*?)\s+\(\d+(?:\.\d+)?\s*(?:ms|s|m)\)\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MtpFailedLine();

    /// <summary>Parses and merges one or more TRX documents. Unparseable documents are ignored; returns null if none parsed.</summary>
    public static TestSummary? ParseTrx(IEnumerable<string> trxDocuments)
    {
        int total = 0, passed = 0, failed = 0, skipped = 0;
        var failedTests = new List<FailedTest>();
        var any = false;

        foreach (var content in trxDocuments)
        {
            XDocument doc;
            try
            {
                doc = XDocument.Parse(content);
            }
            catch (System.Xml.XmlException)
            {
                continue;
            }

            any = true;
            var results = doc.Descendants().Where(e => e.Name.LocalName == "UnitTestResult"
                && e.Attribute("resultType")?.Value is not ("DataDrivenTest" or "OrderedTest")).ToList();

            foreach (var r in results)
            {
                total++;
                var outcome = r.Attribute("outcome")?.Value ?? "";
                switch (outcome)
                {
                    case "Passed" or "Completed":
                        passed++;
                        break;
                    case "Failed" or "Timeout" or "Aborted" or "Error" or "Disconnected" or "NotRunnable":
                        failed++;
                        if (failedTests.Count < MaxFailedTests)
                        {
                            failedTests.Add(new FailedTest(r.Attribute("testName")?.Value ?? "(unknown)", BuildMessage(r)));
                        }

                        break;
                    default:
                        skipped++;
                        break;
                }
            }

            if (results.Count == 0 && doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Counters") is { } counters)
            {
                int Get(string n) => int.TryParse(counters.Attribute(n)?.Value, out var v) ? v : 0;
                var f = Get("failed") + Get("error") + Get("timeout") + Get("aborted");
                total += Get("total");
                passed += Get("passed");
                failed += f;
                skipped += Math.Max(0, Get("total") - Get("passed") - f);
            }
        }

        return any ? new TestSummary(total, passed, failed, skipped, failedTests) : null;
    }

    /// <summary>Parses the summary lines of <c>dotnet test</c> console output (VSTest and Microsoft.Testing.Platform). Null when none found.</summary>
    public static TestSummary? ParseConsole(string output)
    {
        var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var failedTests = new List<FailedTest>();
        foreach (var line in lines)
        {
            var name = VsTestFailedLine().Match(line) is { Success: true } a ? a.Groups["name"].Value
                : MtpFailedLine().Match(line) is { Success: true } b ? b.Groups["name"].Value : null;
            if (name is not null && failedTests.Count < MaxFailedTests && failedTests.All(t => t.Name != name))
            {
                failedTests.Add(new FailedTest(name, null));
            }
        }

        // 1. Aggregated MTP summary wins (avoids double counting per-assembly blocks).
        if (MtpAggregate().Match(output) is { Success: true } agg)
        {
            return new TestSummary(
                int.Parse(agg.Groups["t"].Value), int.Parse(agg.Groups["p"].Value), int.Parse(agg.Groups["f"].Value), int.Parse(agg.Groups["s"].Value), failedTests);
        }

        // 2. VSTest per-assembly lines.
        int total = 0, passed = 0, failed = 0, skipped = 0;
        var found = false;
        foreach (Match m in VsTestSummary().Matches(output))
        {
            found = true;
            total += int.Parse(m.Groups["t"].Value);
            passed += int.Parse(m.Groups["p"].Value);
            failed += int.Parse(m.Groups["f"].Value);
            skipped += int.Parse(m.Groups["s"].Value);
        }

        if (found)
        {
            return new TestSummary(total, passed, failed, skipped, failedTests);
        }

        // 3. MTP per-assembly "Test run summary:" blocks.
        for (var i = 0; i < lines.Count; i++)
        {
            if (!MtpRunSummary().IsMatch(lines[i]))
            {
                continue;
            }

            int t = 0, p = 0, f = 0, s = 0;
            var blockFound = false;
            for (var j = i + 1; j < lines.Count && j <= i + 8; j++)
            {
                var c = MtpCounter().Match(lines[j]);
                if (!c.Success)
                {
                    if (blockFound)
                    {
                        break;
                    }

                    continue;
                }

                blockFound = true;
                var v = int.Parse(c.Groups["v"].Value);
                switch (c.Groups["k"].Value.ToLowerInvariant())
                {
                    case "total": t = v; break;
                    case "failed": f = v; break;
                    case "succeeded": p = v; break;
                    case "skipped": s = v; break;
                }
            }

            if (blockFound)
            {
                found = true;
                total += t;
                passed += p;
                failed += f;
                skipped += s;
            }
        }

        return found ? new TestSummary(total, passed, failed, skipped, failedTests) : null;
    }

    private static string? BuildMessage(XElement result)
    {
        var errorInfo = result.Descendants().FirstOrDefault(e => e.Name.LocalName == "ErrorInfo");
        if (errorInfo is null)
        {
            return null;
        }

        var message = errorInfo.Elements().FirstOrDefault(e => e.Name.LocalName == "Message")?.Value;
        var stack = errorInfo.Elements().FirstOrDefault(e => e.Name.LocalName == "StackTrace")?.Value;
        var sb = new StringBuilder();
        AppendLines(sb, message, MaxMessageLines);
        if (!string.IsNullOrWhiteSpace(stack))
        {
            sb.AppendLine();
            AppendLines(sb, stack, MaxStackLines);
        }

        var text = sb.ToString().Trim();
        if (text.Length == 0)
        {
            return null;
        }

        return text.Length > MaxMessageChars ? text[..MaxMessageChars] + "..." : text;
    }

    private static void AppendLines(StringBuilder sb, string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).Take(max + 1).ToList();
        foreach (var l in lines.Take(max))
        {
            sb.AppendLine(l);
        }

        if (lines.Count > max)
        {
            sb.AppendLine("...");
        }
    }
}
