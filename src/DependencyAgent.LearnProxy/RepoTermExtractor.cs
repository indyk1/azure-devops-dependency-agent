using System.Text.RegularExpressions;
using DependencyAgent.Core.Processes;

namespace DependencyAgent.LearnProxy;

/// <summary>Derives terms identifying a repository (company, product, namespaces, ...) that must never be sent to doc services.</summary>
public static partial class RepoTermExtractor
{
    private const int MaxSourceFiles = 400;
    private const int MaxFileBytes = 64 * 1024;

    private static readonly HashSet<string> GenericWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "api", "core", "common", "tests", "test", "web", "app", "service", "services", "shared", "domain", "infrastructure", "data",
        "models", "model", "client", "server", "host", "lib", "library", "utils", "util", "tools", "tool", "worker", "console",
        "cli", "sdk", "contracts", "abstractions", "application", "persistence", "integration", "unit", "benchmarks", "samples",
        "sample", "demo", "ui", "frontend", "backend", "database", "db", "repository", "repositories", "extensions", "helpers",
        "mvc", "blazor", "functions", "function", "net", "dotnet", "csharp", "the", "and", "for", "ltd", "inc", "llc", "corp", "gmbh",
        "company", "corporation", "limited", "plc", "bv", "src", "main", "master", "project", "projects", "solution", "system", "microsoft",
    };

    public static IReadOnlyList<string> Extract(string repoRoot, params string[] extra)
    {
        var terms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? term)
        {
            term = term?.Trim().Trim('"', '\'');
            if (string.IsNullOrEmpty(term) || term.Length < 3 || term.Contains("$(", StringComparison.Ordinal) || GenericWords.Contains(term))
            {
                return;
            }

            terms.TryAdd(term, term);
        }

        void AddNameParts(string name)
        {
            Add(name);
            var first = name.Split('.')[0];
            if (!PublicNamespaces.Roots.Contains(first))
            {
                Add(first);
            }
        }

        void AddCompanyLike(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            foreach (var item in value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                Add(item);
                foreach (var word in item.Split([' ', '\t', '.', '-'], StringSplitOptions.RemoveEmptyEntries))
                {
                    Add(word);
                }
            }
        }

        foreach (var e in extra ?? [])
        {
            AddCompanyLike(e);
        }

        if (!Directory.Exists(repoRoot))
        {
            return Finish(terms);
        }

        var propsFiles = new List<string>();
        foreach (var project in SafeFindProjects(repoRoot))
        {
            AddNameParts(Path.GetFileNameWithoutExtension(project));
            propsFiles.Add(Path.Combine(repoRoot, project));
        }

        foreach (var props in new[] { "Directory.Build.props", "Directory.Packages.props", "Directory.Build.targets" })
        {
            var path = Path.Combine(repoRoot, props);
            if (File.Exists(path))
            {
                propsFiles.Add(path);
            }
        }

        foreach (var file in propsFiles)
        {
            var content = ReadBounded(file);
            foreach (var (element, company) in new[] { ("RootNamespace", false), ("AssemblyName", false), ("Company", true), ("Product", true), ("Authors", true) })
            {
                foreach (Match m in Element(element).Matches(content))
                {
                    var value = m.Groups[1].Value.Trim();
                    if (company)
                    {
                        AddCompanyLike(value);
                    }
                    else
                    {
                        AddNameParts(value);
                    }
                }
            }
        }

        foreach (var cs in EnumerateSourceFiles(repoRoot))
        {
            foreach (Match m in NamespaceDecl().Matches(ReadBounded(cs)))
            {
                var first = m.Groups[1].Value.Split('.')[0];
                if (!PublicNamespaces.Roots.Contains(first))
                {
                    Add(first);
                }
            }
        }

        return Finish(terms);
    }

    private static IReadOnlyList<string> Finish(Dictionary<string, string> terms) =>
        terms.Values.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ThenBy(t => t, StringComparer.Ordinal).ToList();

    private static IReadOnlyList<string> SafeFindProjects(string root)
    {
        try
        {
            return BuildTargetResolver.FindProjects(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            return Directory.EnumerateFiles(root, "*.cs", options)
                .Select(f => (Full: f, Rel: Path.GetRelativePath(root, f).Replace('\\', '/')))
                .Where(f => !f.Rel.Split('/').SkipLast(1).Any(s => s is "bin" or "obj" or ".git" or "node_modules" or ".vs" or "packages"))
                .Select(f => f.Rel)
                .Order(StringComparer.Ordinal)
                .Take(MaxSourceFiles)
                .Select(rel => Path.Combine(root, rel))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string ReadBounded(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[(int)Math.Min(stream.Length, MaxFileBytes)];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static Regex Element(string name) => new($"<{name}>\\s*([^<]+?)\\s*</{name}>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    [GeneratedRegex(@"^\s*namespace\s+([A-Za-z_][\w.]*)", RegexOptions.Multiline)]
    private static partial Regex NamespaceDecl();
}
