using System.Text.RegularExpressions;
using DependencyAgent.Core.Model;

namespace DependencyAgent.Verification;

/// <summary>
/// Parses MSBuild / compiler / NuGet diagnostics from classic (<c>-tl:off</c>) console output.
/// Only <c>error</c> and <c>warning</c> lines that carry a diagnostic code are returned; identical diagnostics
/// (the same message is printed per target framework, and sometimes twice) are collapsed.
/// </summary>
public static partial class MsBuildDiagnosticParser
{
    // [origin[(line[,col[,endLine,endCol]])] : ] (error|warning) CODE : message [project[::TargetFramework=x]]
    [GeneratedRegex(
        @"^(?:(?<origin>.+?)(?:\((?<line>\d+)(?:,(?<col>\d+))?(?:,\d+,\d+)?\))?\s*:\s+)?(?<sev>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<msg>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex DiagnosticRegex();

    [GeneratedRegex(@"\s+\[(?<proj>[^\[\]]+?\.(?:csproj|fsproj|vbproj|vcxproj|proj|sln|slnx|props|targets))(?:::[^\]]*)?\]\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProjectSuffixRegex();

    [GeneratedRegex(@"^\s*\d+>")]
    private static partial Regex NodePrefixRegex();

    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj", ".vcxproj", ".proj"];

    public static IReadOnlyList<BuildDiagnostic> Parse(string output, string repositoryRoot) =>
        Parse(output.Split('\n'), repositoryRoot);

    public static IReadOnlyList<BuildDiagnostic> Parse(IEnumerable<string> lines, string repositoryRoot)
    {
        var root = NormalizePath(repositoryRoot).TrimEnd('/');
        var comparison = LooksLikeWindowsPath(root) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var seen = new HashSet<BuildDiagnostic>();
        var result = new List<BuildDiagnostic>();

        foreach (var raw in lines)
        {
            var line = NodePrefixRegex().Replace(raw.TrimEnd('\r', '\n').Trim(), "");
            if (line.Length == 0 || !(line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("warning", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var m = DiagnosticRegex().Match(line);
            if (!m.Success)
            {
                continue;
            }

            var severity = m.Groups["sev"].Value.Equals("error", StringComparison.OrdinalIgnoreCase) ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning;
            var message = m.Groups["msg"].Value.Trim();

            string? project = null;
            var suffix = ProjectSuffixRegex().Match(message);
            if (suffix.Success)
            {
                project = suffix.Groups["proj"].Value;
                message = message[..suffix.Index].TrimEnd();
            }

            string? file = null;
            var origin = m.Groups["origin"].Success ? m.Groups["origin"].Value.Trim() : null;
            if (!string.IsNullOrEmpty(origin) && IsFileOrigin(origin))
            {
                file = NormalizePath(origin);
            }

            int? lineNo = m.Groups["line"].Success ? int.Parse(m.Groups["line"].Value) : null;
            int? col = m.Groups["col"].Success ? int.Parse(m.Groups["col"].Value) : null;

            var absoluteProject = project is null ? null : NormalizePath(project);
            if (file is not null && !IsRooted(file) && absoluteProject is not null && IsRooted(absoluteProject))
            {
                // MSBuild reports relative paths relative to the project directory.
                var dir = absoluteProject[..absoluteProject.LastIndexOf('/')];
                file = NormalizePath(dir + "/" + file);
            }

            if (project is null && file is not null && ProjectExtensions.Any(e => file.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            {
                absoluteProject = file;
            }

            var diagnostic = new BuildDiagnostic(
                severity,
                m.Groups["code"].Value,
                message,
                file is null ? null : MakeRelative(file, root, comparison),
                lineNo,
                col,
                absoluteProject is null ? null : MakeRelative(absoluteProject, root, comparison));

            if (seen.Add(diagnostic))
            {
                result.Add(diagnostic);
            }
        }

        return result;
    }

    // "CSC", "MSBUILD", "dotnet" are tool names; anything with a path separator or an extension is a file.
    private static bool IsFileOrigin(string origin) =>
        origin.Contains('/') || origin.Contains('\\') || origin.Contains('.');

    private static bool LooksLikeWindowsPath(string p) => p.Length >= 2 && char.IsLetter(p[0]) && p[1] == ':';

    private static bool IsRooted(string p) => p.StartsWith('/') || LooksLikeWindowsPath(p);

    private static string MakeRelative(string path, string root, StringComparison comparison)
    {
        if (root.Length > 0 && path.StartsWith(root + "/", comparison))
        {
            return path[(root.Length + 1)..];
        }

        return path;
    }

    /// <summary>Forward slashes, with <c>.</c> and <c>..</c> segments resolved (without touching the file system).</summary>
    internal static string NormalizePath(string path)
    {
        var p = path.Replace('\\', '/');
        var prefix = "";
        if (LooksLikeWindowsPath(p))
        {
            prefix = p[..2];
            p = p[2..];
        }

        var rooted = p.StartsWith('/');
        var stack = new List<string>();
        foreach (var segment in p.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == ".." && stack.Count > 0 && stack[^1] != "..")
            {
                stack.RemoveAt(stack.Count - 1);
            }
            else if (segment == ".." && rooted)
            {
                // cannot go above root
            }
            else
            {
                stack.Add(segment);
            }
        }

        return prefix + (rooted ? "/" : "") + string.Join('/', stack);
    }
}
