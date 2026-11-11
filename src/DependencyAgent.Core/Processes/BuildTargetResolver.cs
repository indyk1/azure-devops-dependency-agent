using DependencyAgent.Core.Configuration;

namespace DependencyAgent.Core.Processes;

/// <summary>Decides what to pass to <c>dotnet build/test/format</c> for a repository.</summary>
public static class BuildTargetResolver
{
    private static readonly string[] IgnoredDirectories = ["bin", "obj", ".git", "node_modules", ".vs", "packages"];

    /// <summary>
    /// Returns the repo-relative path (forward slashes) of the solution/project to build:
    /// <list type="number">
    /// <item><c>verification.solution</c> when configured;</item>
    /// <item>the single <c>.slnx</c>/<c>.sln</c> at the repository root (prefers <c>.slnx</c> when both have the same name);</item>
    /// <item>the single solution found within 3 directory levels;</item>
    /// <item>the single project file in the repository.</item>
    /// </list>
    /// Returns null when ambiguous — callers then build each SDK-style project individually or ask for configuration.
    /// </summary>
    public static string? Resolve(string repositoryRoot, VerificationSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Solution))
        {
            return GlobMatcher.Normalize(settings.Solution);
        }

        var rootSolutions = FindSolutions(repositoryRoot, maxDepth: 0);
        if (PickSolution(rootSolutions) is { } rootPick)
        {
            return Relative(repositoryRoot, rootPick);
        }

        if (rootSolutions.Count == 0)
        {
            var nested = FindSolutions(repositoryRoot, maxDepth: 3);
            if (PickSolution(nested) is { } nestedPick)
            {
                return Relative(repositoryRoot, nestedPick);
            }

            if (nested.Count == 0)
            {
                var projects = EnumerateFiles(repositoryRoot, 6, f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                    || f.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)).ToList();
                if (projects.Count == 1)
                {
                    return Relative(repositoryRoot, projects[0]);
                }
            }
        }

        return null;
    }

    /// <summary>All project files (repo-relative), excluding bin/obj/.git/node_modules.</summary>
    public static IReadOnlyList<string> FindProjects(string repositoryRoot) =>
        EnumerateFiles(repositoryRoot, 12, f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase))
            .Select(f => Relative(repositoryRoot, f)).OrderBy(f => f, StringComparer.Ordinal).ToList();

    private static string? PickSolution(IReadOnlyList<string> solutions)
    {
        if (solutions.Count == 1)
        {
            return solutions[0];
        }

        // foo.sln + foo.slnx side by side → prefer slnx.
        var byName = solutions.GroupBy(s => Path.Combine(Path.GetDirectoryName(s)!, Path.GetFileNameWithoutExtension(s))).ToList();
        if (byName.Count == 1)
        {
            return byName[0].FirstOrDefault(s => s.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)) ?? byName[0].First();
        }

        return null;
    }

    private static List<string> FindSolutions(string root, int maxDepth) =>
        EnumerateFiles(root, maxDepth, f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)).ToList();

    private static IEnumerable<string> EnumerateFiles(string directory, int maxDepth, Func<string, bool> predicate)
    {
        foreach (var file in Directory.EnumerateFiles(directory).Where(predicate).OrderBy(f => f, StringComparer.Ordinal))
        {
            yield return file;
        }

        if (maxDepth <= 0)
        {
            yield break;
        }

        foreach (var sub in Directory.EnumerateDirectories(directory).OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(sub);
            if (IgnoredDirectories.Contains(name, StringComparer.OrdinalIgnoreCase) || (File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            foreach (var file in EnumerateFiles(sub, maxDepth - 1, predicate))
            {
                yield return file;
            }
        }
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}
