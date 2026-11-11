using System.Text;
using DependencyAgent.Core.Configuration;
using DependencyAgent.Core.Processes;

namespace DependencyAgent.Fixing;

/// <summary>Finds (or generates) the solution file Roslynk should open.</summary>
public static class SolutionPathResolver
{
    /// <summary>
    /// Returns the absolute path of an existing .sln/.slnx inside the repository, or, for a single project / ambiguous layouts, of a generated
    /// <c>.slnx</c> written to <paramref name="generatedDirectory"/> (which must be OUTSIDE the repository) that lists every SDK-style project by absolute path.
    /// Returns null when there is nothing Roslyn can load (no SDK-style project).
    /// </summary>
    public static string? Resolve(string repositoryRoot, VerificationSettings verification, string generatedDirectory)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var target = BuildTargetResolver.Resolve(root, verification);
        if (target is not null && (target.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || target.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)))
        {
            var full = Path.GetFullPath(Path.Combine(root, target));
            if (IsInside(root, full) && File.Exists(full))
            {
                return full;
            }
        }

        var projects = BuildTargetResolver.FindProjects(root)
            .Select(p => Path.GetFullPath(Path.Combine(root, p)))
            .Where(IsSdkStyle)
            .ToList();
        if (projects.Count == 0)
        {
            return null;
        }

        Directory.CreateDirectory(generatedDirectory);
        var path = Path.Combine(generatedDirectory, "roslynk.slnx");
        File.WriteAllText(path, BuildSlnx(projects), new UTF8Encoding(false));
        return path;
    }

    public static string BuildSlnx(IEnumerable<string> absoluteProjectPaths)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<Solution>");
        foreach (var p in absoluteProjectPaths)
        {
            sb.AppendLine($"  <Project Path=\"{System.Security.SecurityElement.Escape(p)}\" />");
        }

        sb.AppendLine("</Solution>");
        return sb.ToString();
    }

    /// <summary>True when the project file uses an SDK (<c>&lt;Project Sdk="..."&gt;</c> or an <c>&lt;Sdk Name=...&gt;</c> element); legacy projects are skipped.</summary>
    public static bool IsSdkStyle(string projectFile)
    {
        try
        {
            var text = File.ReadAllText(projectFile);
            var head = text.Length > 4000 ? text[..4000] : text;
            return System.Text.RegularExpressions.Regex.IsMatch(head, "<Project\\b[^>]*\\bSdk\\s*=", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                || System.Text.RegularExpressions.Regex.IsMatch(text, "<Sdk\\s+Name\\s*=", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool IsInside(string root, string path)
    {
        var rel = Path.GetRelativePath(root, path);
        return !rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel);
    }
}
