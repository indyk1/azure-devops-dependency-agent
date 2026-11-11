namespace DependencyAgent.Updates.Infrastructure;

/// <summary>Lazy, cached access to the MSBuild XML files of one repository, with Directory.Build.props ancestry.</summary>
internal sealed class MsBuildModel
{
    private readonly Dictionary<string, EditableXml?> _files = new(StringComparer.Ordinal);
    private readonly List<string> _loadErrors = [];

    public MsBuildModel(string root)
    {
        Root = root;
        Layout = RepoLayout.Scan(root);
    }

    public string Root { get; }

    public RepoLayout Layout { get; }

    public IReadOnlyList<string> LoadErrors => _loadErrors;

    public IEnumerable<EditableXml> Loaded => _files.Values.OfType<EditableXml>();

    /// <summary>Loads a repo-relative MSBuild file; null when unreadable or not an MSBuild <c>Project</c>.</summary>
    public EditableXml? Get(string relativePath)
    {
        if (_files.TryGetValue(relativePath, out var cached))
        {
            return cached;
        }

        var xml = EditableXml.TryLoad(Root, Path.Combine(Root, relativePath), out var error);
        if (error is not null)
        {
            _loadErrors.Add(error);
        }

        if (xml is not null && !string.Equals(xml.Root?.Name.LocalName, "Project", StringComparison.Ordinal))
        {
            xml = null;
        }

        _files[relativePath] = xml;
        return xml;
    }

    /// <summary>Directory.Build.props files that apply to <paramref name="relativePath"/>, nearest first.</summary>
    public IEnumerable<EditableXml> AncestorBuildProps(string relativePath)
    {
        var dir = PathUtil.DirectoryOf(relativePath);
        while (true)
        {
            if (Layout.BuildPropsByDirectory.TryGetValue(dir, out var props)
                && !string.Equals(props, relativePath, StringComparison.Ordinal)
                && Get(props) is { } xml)
            {
                yield return xml;
            }

            if (dir.Length == 0)
            {
                yield break;
            }

            dir = PathUtil.DirectoryOf(dir);
        }
    }

    /// <summary>Saves every file with queued edits. Returns the repo-relative paths written.</summary>
    public IReadOnlyList<string> SaveAll()
    {
        var written = new List<string>();
        foreach (var xml in Loaded.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            if (xml.Save())
            {
                written.Add(xml.RelativePath);
            }
        }

        return written;
    }

    /// <summary>True for a project using an MSBuild SDK (<c>Sdk</c> attribute, <c>&lt;Sdk&gt;</c> or <c>&lt;Import Sdk&gt;</c>).</summary>
    public static bool IsSdkStyle(EditableXml project)
    {
        var root = project.Root;
        if (root is null)
        {
            return false;
        }

        if (root.Attributes().Any(a => a.Name.LocalName.Equals("Sdk", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return root.Elements().Any(e => e.Name.LocalName.Equals("Sdk", StringComparison.OrdinalIgnoreCase))
            || project.Descendants("Import").Any(e => e.Attributes().Any(a => a.Name.LocalName.Equals("Sdk", StringComparison.OrdinalIgnoreCase)));
    }
}
