using System.Text.RegularExpressions;
using System.Xml.Linq;
using DependencyAgent.Core;
using DependencyAgent.Core.Configuration;
using DependencyAgent.Core.Model;
using DependencyAgent.Updates.Infrastructure;
using DependencyAgent.Updates.Packages;
using Microsoft.Extensions.Logging;
using NuGet.Versioning;

namespace DependencyAgent.Updates.Steps;

/// <summary>Updates NuGet package versions in projects, Directory.Build.props/targets and Directory.Packages.props.</summary>
public sealed partial class NuGetPackageUpdateStep : IUpdateStep
{
    private const int MaxParallelLookups = 8;

    private static readonly string[] ItemNames = ["PackageReference", "PackageVersion", "GlobalPackageReference"];
    private static readonly string[] VersionNames = ["Version", "VersionOverride"];

    private readonly Func<string, IPackageVersionSource> _versionSourceFactory;

    public NuGetPackageUpdateStep(Func<string, IPackageVersionSource> versionSourceFactory)
    {
        _versionSourceFactory = versionSourceFactory;
    }

    public string Name => "nuget-packages";

    [GeneratedRegex(@"^\$\((?<name>[A-Za-z_][\w.\-]*)\)$")]
    private static partial Regex PropertyReference();

    /// <summary>Where a version string lives: an attribute or a child element.</summary>
    private sealed class VersionSlot(EditableXml file, XObject node, string raw)
    {
        public EditableXml File { get; } = file;

        public string Raw { get; } = raw;

        public bool TrySet(string value) => node switch
        {
            XAttribute a => File.TrySetAttribute(a, value),
            XElement e => File.TrySetElementText(e, value),
            _ => false,
        };
    }

    private sealed class PropertyDefinition(EditableXml file, XElement element)
    {
        public EditableXml File { get; } = file;

        public XElement Element { get; } = element;
    }

    private sealed class Usage(string id, EditableXml file, VersionSlot slot)
    {
        public string Id { get; } = id;

        public EditableXml File { get; } = file;

        public VersionSlot Slot { get; } = slot;

        public NuGetVersion? Current { get; set; }

        /// <summary>Non-null when the version is a <c>$(Property)</c> reference resolved to these definitions.</summary>
        public List<PropertyDefinition>? Property { get; set; }

        public string? PropertyName { get; set; }

        public PackageUpdateDecision? Decision { get; set; }
    }

    public async Task<UpdateStepResult> ApplyAsync(RepoContext context, CancellationToken cancellationToken)
    {
        var policy = context.Settings.Packages;
        if (!policy.Enabled)
        {
            return UpdateStepResult.Empty;
        }

        var notes = new NoteList();
        if (!TargetFrameworkMapper.TryParseTarget(context.Settings.TargetFramework, out var targetMajor, out _))
        {
            notes.Add($"Target framework '{context.Settings.TargetFramework}' is not a modern .NET moniker; package step skipped");
            return new UpdateStepResult([], notes.ToList());
        }

        var model = new MsBuildModel(context.WorkingDirectory);
        var usages = CollectUsages(model, notes, cancellationToken);

        var source = _versionSourceFactory(context.WorkingDirectory);
        try
        {
            var lookup = await LookUpVersionsAsync(source, usages, policy, notes, context.Logger, cancellationToken).ConfigureAwait(false);
            foreach (var usage in usages.Where(u => u.Current is not null))
            {
                if (lookup.Failed.Contains(usage.Id))
                {
                    continue;
                }

                var available = lookup.Versions.TryGetValue(usage.Id, out var v) ? v : [];
                usage.Decision = PackageUpdatePlanner.Plan(usage.Id, usage.Current!, available, policy, targetMajor);
                if (usage.Decision.Note is not null)
                {
                    notes.Add(usage.Decision.Note);
                }
            }
        }
        finally
        {
            (source as IDisposable)?.Dispose();
        }

        var changes = new List<ChangeRecord>();
        ApplyDirect(usages, changes, notes, context.Logger);
        ApplyProperties(usages, changes, notes, context.Logger);

        foreach (var written in model.SaveAll())
        {
            context.Logger.LogInformation("Updated package versions in {File}", written);
        }

        foreach (var error in model.LoadErrors)
        {
            notes.Add(error);
        }

        return new UpdateStepResult(changes, notes.ToList());
    }

    private static List<Usage> CollectUsages(MsBuildModel model, NoteList notes, CancellationToken ct)
    {
        var usages = new List<Usage>();
        var paths = model.Layout.DirectoryPackagesProps
            .Concat(model.Layout.DirectoryBuildFiles)
            .Concat(model.Layout.Projects)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (model.Get(path) is not { } file)
            {
                continue;
            }

            foreach (var item in file.Document.Descendants().Where(e => ItemNames.Contains(e.Name.LocalName, StringComparer.OrdinalIgnoreCase)))
            {
                var id = Attribute(item, "Include")?.Value.Trim();
                if (string.IsNullOrEmpty(id) || id.Contains("$(", StringComparison.Ordinal) || id.Contains(';', StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var slot in Slots(file, item))
                {
                    var usage = new Usage(id, file, slot);
                    if (Resolve(model, usage, notes))
                    {
                        usages.Add(usage);
                    }
                }
            }
        }

        return usages;
    }

    private static XAttribute? Attribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(a => a.Name.Namespace == XNamespace.None && a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<VersionSlot> Slots(EditableXml file, XElement item)
    {
        foreach (var name in VersionNames)
        {
            if (Attribute(item, name) is { } attr)
            {
                yield return new VersionSlot(file, attr, attr.Value.Trim());
            }

            foreach (var child in item.Elements().Where(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase) && !e.HasElements))
            {
                yield return new VersionSlot(file, child, child.Value.Trim());
            }
        }
    }

    /// <summary>Classifies the raw version; returns false (with a note) when the usage cannot be updated.</summary>
    private static bool Resolve(MsBuildModel model, Usage usage, NoteList notes)
    {
        var raw = usage.Slot.Raw;
        var where = $"{usage.Id} in {usage.File.RelativePath}";
        if (raw.Length == 0)
        {
            return false;
        }

        if (PropertyReference().Match(raw) is { Success: true } m)
        {
            var name = m.Groups["name"].Value;
            var definitions = FindProperty(model, usage.File, name);
            if (definitions is null)
            {
                notes.Add($"Skipped {where}: version property $({name}) is not defined in the same file or a Directory.Build.props in scope");
                return false;
            }

            var first = definitions[0].Element.Value.Trim();
            if (!IsPlainVersion(first, out var version))
            {
                notes.Add($"Skipped {where}: version property $({name}) has value '{first}' which cannot be updated");
                return false;
            }

            usage.Property = definitions.Where(d => d.Element.Value.Trim() == first).ToList();
            usage.PropertyName = name;
            usage.Current = version;
            return true;
        }

        if (raw.Contains("$(", StringComparison.Ordinal) || raw.Contains("@(", StringComparison.Ordinal))
        {
            notes.Add($"Skipped {where}: version '{raw}' uses MSBuild expressions");
            return false;
        }

        if (raw.Contains('*', StringComparison.Ordinal))
        {
            notes.Add($"Skipped {where}: floating version '{raw}'");
            return false;
        }

        if (raw.StartsWith('[') || raw.StartsWith('(') || raw.Contains(',', StringComparison.Ordinal))
        {
            notes.Add($"Skipped {where}: version range '{raw}'");
            return false;
        }

        if (!NuGetVersion.TryParse(raw, out var current))
        {
            notes.Add($"Skipped {where}: '{raw}' is not a valid NuGet version");
            return false;
        }

        usage.Current = current;
        return true;
    }

    private static bool IsPlainVersion(string text, out NuGetVersion version)
    {
        version = null!;
        if (text.Length == 0 || text.Contains("$(", StringComparison.Ordinal) || text.Contains('*', StringComparison.Ordinal)
            || text.StartsWith('[') || text.StartsWith('(') || text.Contains(',', StringComparison.Ordinal))
        {
            return false;
        }

        return NuGetVersion.TryParse(text, out version!);
    }

    private static List<PropertyDefinition>? FindProperty(MsBuildModel model, EditableXml from, string name)
    {
        foreach (var file in new[] { from }.Concat(model.AncestorBuildProps(from.RelativePath)))
        {
            var definitions = file.Descendants(name)
                .Where(e => string.Equals(e.Parent?.Name.LocalName, "PropertyGroup", StringComparison.OrdinalIgnoreCase) && !e.HasElements)
                .Select(e => new PropertyDefinition(file, e))
                .ToList();
            if (definitions.Count > 0)
            {
                return definitions;
            }
        }

        return null;
    }

    private sealed record LookupResult(Dictionary<string, IReadOnlyList<NuGetVersion>> Versions, HashSet<string> Failed);

    private static async Task<LookupResult> LookUpVersionsAsync(
        IPackageVersionSource source, List<Usage> usages, PackagePolicySettings policy, NoteList notes, ILogger logger, CancellationToken ct)
    {
        var versions = new Dictionary<string, IReadOnlyList<NuGetVersion>>(StringComparer.OrdinalIgnoreCase);
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failureMessages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var gate = new object();

        // Ignored and pinned packages never need a lookup.
        var ids = usages
            .Where(u => u.Current is not null
                && !GlobMatcher.IsMatchAny(policy.Ignore, u.Id)
                && !policy.Pins.Keys.Any(k => string.Equals(k, u.Id, StringComparison.OrdinalIgnoreCase)))
            .Select(u => u.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = MaxParallelLookups, CancellationToken = ct }, async (id, token) =>
        {
            try
            {
                var result = await source.GetVersionsAsync(id, token).ConfigureAwait(false);
                lock (gate)
                {
                    versions[id] = result;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                lock (gate)
                {
                    failed.Add(id);
                    failureMessages[id] = SingleLine(ex.Message);
                }
            }
        }).ConfigureAwait(false);

        foreach (var id in ids.Where(failed.Contains))
        {
            var current = usages.First(u => string.Equals(u.Id, id, StringComparison.OrdinalIgnoreCase)).Current;
            logger.LogWarning("Version lookup failed for {PackageId}: {Message}", id, failureMessages[id]);
            notes.Add($"Could not look up versions for {id}: {failureMessages[id]}; kept {current}");
        }

        return new LookupResult(versions, failed);
    }

    private static string SingleLine(string message)
    {
        var line = message.Split('\r', '\n')[0].Trim();
        return line.Length > 200 ? line[..200] + "..." : line;
    }

    private static void ApplyDirect(List<Usage> usages, List<ChangeRecord> changes, NoteList notes, ILogger logger)
    {
        foreach (var usage in usages.Where(u => u.Property is null && u.Decision?.NewVersion is not null))
        {
            var target = usage.Decision!.NewVersion!;
            if (target.Equals(usage.Current))
            {
                continue;
            }

            var text = PackageUpdatePlanner.Format(target);
            if (usage.Slot.TrySet(text))
            {
                changes.Add(new ChangeRecord(ChangeKind.Package, usage.File.RelativePath, usage.Id, usage.Slot.Raw, text, usage.Decision.Reason));
                logger.LogInformation("{PackageId}: {From} -> {To} in {File}", usage.Id, usage.Slot.Raw, text, usage.File.RelativePath);
            }
            else
            {
                notes.Add($"Could not edit the version of {usage.Id} in {usage.File.RelativePath} automatically");
            }
        }
    }

    /// <summary>
    /// A version property is edited once, however many packages reference it. When the referencing packages want
    /// different versions the lowest wins (so a held-back or ignored package keeps everything at its version).
    /// </summary>
    private static void ApplyProperties(List<Usage> usages, List<ChangeRecord> changes, NoteList notes, ILogger logger)
    {
        var groups = usages
            .Where(u => u.Property is not null && u.Decision is not null)
            .GroupBy(u => (File: u.Property![0].File.RelativePath, Name: u.PropertyName!.ToUpperInvariant()));

        foreach (var group in groups)
        {
            var members = group.ToList();
            var current = members[0].Current!;
            var desired = members.Select(u => u.Decision!.NewVersion ?? u.Current!).Min()!;
            if (desired.Equals(current))
            {
                if (members.Any(u => u.Decision!.NewVersion is not null && !u.Decision.NewVersion.Equals(current)))
                {
                    notes.Add($"Property $({members[0].PropertyName}) in {group.Key.File} was not updated because it is shared with packages that must stay at {current}");
                }

                continue;
            }

            var definitions = members[0].Property!;
            var text = PackageUpdatePlanner.Format(desired);
            if (!definitions.All(d => d.File.TrySetElementText(d.Element, text)))
            {
                notes.Add($"Could not edit property $({members[0].PropertyName}) in {group.Key.File} automatically");
                continue;
            }

            var ids = members.Select(u => u.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (members.Any(u => u.Decision!.NewVersion is not null && !u.Decision.NewVersion.Equals(desired)))
            {
                notes.Add($"Property $({members[0].PropertyName}) in {group.Key.File} is shared by {string.Join(", ", ids)}; set to the lowest wanted version {text}");
            }

            foreach (var id in ids)
            {
                changes.Add(new ChangeRecord(ChangeKind.Package, group.Key.File, id, PackageUpdatePlanner.Format(current), text, $"via property $({members[0].PropertyName})"));
            }

            logger.LogInformation("Property {Property} in {File}: {From} -> {To}", members[0].PropertyName, group.Key.File, current, text);
        }
    }
}
