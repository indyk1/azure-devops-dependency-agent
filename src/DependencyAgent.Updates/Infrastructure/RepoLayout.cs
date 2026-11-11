using DependencyAgent.Core.Processes;

namespace DependencyAgent.Updates.Infrastructure;

/// <summary>Result of one walk over the repository: every file plus the well-known MSBuild/SDK files.</summary>
internal sealed class RepoLayout
{
    private static readonly string[] IgnoredDirectories = ["bin", "obj", ".git", "node_modules", ".vs"];

    private RepoLayout(string root)
    {
        Root = root;
    }

    public string Root { get; }

    /// <summary>All files, repo-relative with forward slashes, sorted ordinally.</summary>
    public IReadOnlyList<string> AllFiles { get; private set; } = [];

    public IReadOnlyList<string> Projects { get; private set; } = [];

    /// <summary>Directory.Build.props and Directory.Build.targets files.</summary>
    public IReadOnlyList<string> DirectoryBuildFiles { get; private set; } = [];

    public IReadOnlyList<string> DirectoryPackagesProps { get; private set; } = [];

    public IReadOnlyList<string> GlobalJsonFiles { get; private set; } = [];

    /// <summary>Directory (repo-relative, "" = root) → Directory.Build.props path.</summary>
    public IReadOnlyDictionary<string, string> BuildPropsByDirectory { get; private set; } = new Dictionary<string, string>();

    public static RepoLayout Scan(string root)
    {
        var layout = new RepoLayout(root);
        var files = new List<string>();
        Walk(root, root, files);
        files.Sort(StringComparer.Ordinal);

        var props = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in files.Where(f => Name(f).Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase)))
        {
            props[PathUtil.DirectoryOf(f)] = f;
        }

        layout.AllFiles = files;
        layout.Projects = BuildTargetResolver.FindProjects(root);
        layout.BuildPropsByDirectory = props;
        layout.DirectoryBuildFiles = files.Where(f => Name(f).Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase)
            || Name(f).Equals("Directory.Build.targets", StringComparison.OrdinalIgnoreCase)).ToList();
        layout.DirectoryPackagesProps = files.Where(f => Name(f).Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase)).ToList();
        layout.GlobalJsonFiles = files.Where(f => Name(f).Equals("global.json", StringComparison.OrdinalIgnoreCase)).ToList();
        return layout;
    }

    private static string Name(string relativePath) => relativePath[(relativePath.LastIndexOf('/') + 1)..];

    private static void Walk(string root, string directory, List<string> files)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            files.Add(PathUtil.Relative(root, file));
        }

        foreach (var sub in Directory.EnumerateDirectories(directory))
        {
            var name = Path.GetFileName(sub);
            if (IgnoredDirectories.Contains(name, StringComparer.OrdinalIgnoreCase) || (File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            Walk(root, sub, files);
        }
    }
}
