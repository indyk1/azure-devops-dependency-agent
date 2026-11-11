using System.Text.RegularExpressions;

namespace DependencyAgent.Fixing;

/// <summary>A suspicious change to a test file.</summary>
public sealed record TamperingFinding(string File, string Reason);

/// <summary>
/// Heuristic detector that looks at a unified diff (<c>git diff HEAD</c>) of a file changed by the AI and reports
/// attempts to make tests "pass" by removing or skipping them or by dropping assertions. Also finds added warning suppressions.
/// </summary>
public static class TestTamperingDetector
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    private static readonly Regex TestAttribute = new(@"^\s*\[\s*(?:Fact|Theory|Test|TestMethod|TestCase|TestCaseSource|DataTestMethod|DataRow|InlineData)\b", RegexOptions.CultureInvariant, Timeout);

    private static readonly Regex SkipMarker = new(
        @"\bSkip\s*=|\[\s*Ignore\b|\[\s*Skip\b|\bAssert\s*\.\s*Skip|\bAssert\s*\.\s*Inconclusive|\[\s*Fact\s*\(\s*Skip|\bIgnore\s*=",
        RegexOptions.CultureInvariant, Timeout);

    private static readonly Regex Assertion = new(
        @"\bAssert\s*\.|\bAssert\w*\s*\(|\.\s*Should\w*\s*\(|\.\s*Should\b|\bShould\w*\.|\bExpect\s*\(|\bVerify\w*\s*\(",
        RegexOptions.CultureInvariant, Timeout);

    private static readonly Regex Suppression = new(
        @"#\s*pragma\s+warning\s+disable|\[\s*(?:assembly:\s*)?(?:System\.Diagnostics\.CodeAnalysis\.)?SuppressMessage\b|<\s*NoWarn\b|\bNoWarn\b\s*=|TreatWarningsAsErrors\s*>\s*false|WarningsNotAsErrors|dotnet_diagnostic\.\S+\.severity\s*=\s*none",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, Timeout);

    /// <summary>True when the path lies under a directory or in a file whose name contains "test" (case-insensitive).</summary>
    public static bool IsTestFile(string path) =>
        path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Any(s => s.Contains("test", StringComparison.OrdinalIgnoreCase));

    /// <summary>Analyzes a unified diff of <paramref name="file"/>. Non-test files yield no findings.</summary>
    public static IReadOnlyList<TamperingFinding> Analyze(string file, string unifiedDiff)
    {
        if (!IsTestFile(file))
        {
            return [];
        }

        var (added, removed) = SplitDiff(unifiedDiff);
        var findings = new List<TamperingFinding>();

        var removedAttrs = Count(removed, TestAttribute);
        var addedAttrs = Count(added, TestAttribute);
        if (removedAttrs > addedAttrs)
        {
            findings.Add(new TamperingFinding(file, $"removed {removedAttrs - addedAttrs} test attribute(s) ([Fact]/[Theory]/[Test]/[TestMethod]/[TestCase])"));
        }

        var addedSkips = Count(added, SkipMarker);
        var removedSkips = Count(removed, SkipMarker);
        if (addedSkips > removedSkips)
        {
            findings.Add(new TamperingFinding(file, $"added {addedSkips - removedSkips} skip/ignore marker(s)"));
        }

        var removedAsserts = Count(removed, Assertion);
        var addedAsserts = Count(added, Assertion);
        if (removedAsserts > addedAsserts)
        {
            findings.Add(new TamperingFinding(file, $"removed {removedAsserts - addedAsserts} assertion line(s) without replacement"));
        }

        return findings;
    }

    /// <summary>Treats an untracked (new) file as an all-added diff.</summary>
    public static IReadOnlyList<TamperingFinding> AnalyzeNewFile(string file, string content) =>
        Analyze(file, string.Join('\n', content.Split('\n').Select(l => "+" + l.TrimEnd('\r'))));

    /// <summary>Added lines that suppress warnings (<c>#pragma warning disable</c>, <c>[SuppressMessage]</c>, <c>NoWarn</c>, ...). Applies to every file.</summary>
    public static IReadOnlyList<string> FindAddedSuppressions(string file, string unifiedDiff)
    {
        var (added, _) = SplitDiff(unifiedDiff);
        var result = new List<string>();
        foreach (var line in added)
        {
            if (IsMatch(Suppression, line))
            {
                result.Add($"{file}: {line.Trim()}");
            }
        }

        return result;
    }

    private static (List<string> Added, List<string> Removed) SplitDiff(string diff)
    {
        var added = new List<string>();
        var removed = new List<string>();
        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
            {
                // File headers ("+++ b/x", "--- a/x"). A removed line "-- comment" would start with "---" only if the
                // content starts with "--", which is rare enough in C#/XML test files to accept.
                if (line.StartsWith("+++ ", StringComparison.Ordinal) || line.StartsWith("--- ", StringComparison.Ordinal))
                {
                    continue;
                }
            }

            if (line.StartsWith('+'))
            {
                added.Add(line[1..]);
            }
            else if (line.StartsWith('-'))
            {
                removed.Add(line[1..]);
            }
        }

        return (added, removed);
    }

    private static int Count(List<string> lines, Regex regex) => lines.Count(l => IsMatch(regex, l));

    private static bool IsMatch(Regex regex, string line)
    {
        try
        {
            return regex.IsMatch(line);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
