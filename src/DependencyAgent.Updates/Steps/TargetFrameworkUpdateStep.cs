using System.Globalization;
using System.Xml.Linq;
using DependencyAgent.Core;
using DependencyAgent.Core.Model;
using DependencyAgent.Updates.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DependencyAgent.Updates.Steps;

/// <summary>Moves modern .NET target frameworks (netcoreapp*, net5.0 - net9.0) to the configured target (e.g. net10.0).</summary>
public sealed class TargetFrameworkUpdateStep : IUpdateStep
{
    public string Name => "target-framework";

    public Task<UpdateStepResult> ApplyAsync(RepoContext context, CancellationToken cancellationToken) =>
        Task.FromResult(Apply(context, cancellationToken));

    private static UpdateStepResult Apply(RepoContext context, CancellationToken ct)
    {
        var changes = new List<ChangeRecord>();
        var notes = new NoteList();

        if (!TargetFrameworkMapper.TryParseTarget(context.Settings.TargetFramework, out var targetMajor, out var targetMinor))
        {
            notes.Add($"Target framework '{context.Settings.TargetFramework}' is not a modern .NET moniker; target-framework step skipped");
            return new UpdateStepResult(changes, notes.ToList());
        }

        var model = new MsBuildModel(context.WorkingDirectory);

        foreach (var path in model.Layout.DirectoryBuildFiles)
        {
            ct.ThrowIfCancellationRequested();
            if (model.Get(path) is { } file)
            {
                ProcessFile(file, targetMajor, targetMinor, changes, notes, context.Logger);
            }
        }

        foreach (var path in model.Layout.Projects)
        {
            ct.ThrowIfCancellationRequested();
            if (model.Get(path) is not { } project)
            {
                continue;
            }

            if (!MsBuildModel.IsSdkStyle(project))
            {
                notes.Add($"Skipped .NET Framework project {path} (report only; migration is out of scope)");
                continue;
            }

            ProcessFile(project, targetMajor, targetMinor, changes, notes, context.Logger);
            CheckFrameworkOnly(model, project, notes);
        }

        foreach (var file in model.Loaded)
        {
            CheckRuntimeFrameworkVersion(file, targetMajor, notes);
        }

        foreach (var written in model.SaveAll())
        {
            context.Logger.LogInformation("Updated target framework in {File}", written);
        }

        foreach (var error in model.LoadErrors)
        {
            notes.Add(error);
        }

        return new UpdateStepResult(changes, notes.ToList());
    }

    private static IEnumerable<XElement> TfmElements(EditableXml file) =>
        file.Descendants("TargetFramework").Concat(file.Descendants("TargetFrameworks"))
            .Where(e => string.Equals(e.Parent?.Name.LocalName, "PropertyGroup", StringComparison.OrdinalIgnoreCase));

    private static void ProcessFile(EditableXml file, int major, int minor, List<ChangeRecord> changes, NoteList notes, ILogger logger)
    {
        foreach (var element in TfmElements(file))
        {
            var value = element.Value.Trim();
            if (value.Length == 0)
            {
                continue;
            }

            if (value.Contains("$(", StringComparison.Ordinal))
            {
                notes.Add($"{file.RelativePath}: {element.Name.LocalName} '{value}' contains an MSBuild property reference; not edited");
                continue;
            }

            var mapped = new List<string>();
            var anyChanged = false;
            foreach (var entry in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var r = TargetFrameworkMapper.Map(entry, major, minor);
                anyChanged |= r.Changed;
                mapped.Add(r.Value);
                if (r.Note is not null)
                {
                    notes.Add($"{file.RelativePath}: {r.Note}");
                }
            }

            if (!anyChanged)
            {
                continue;
            }

            var newValue = string.Join(';', mapped.Distinct(StringComparer.OrdinalIgnoreCase));
            if (file.TrySetElementText(element, newValue))
            {
                changes.Add(new ChangeRecord(ChangeKind.TargetFramework, file.RelativePath, file.FileName, value, newValue));
            }
            else
            {
                logger.LogWarning("Could not edit {Element} in {File}", element.Name.LocalName, file.RelativePath);
                notes.Add($"{file.RelativePath}: could not edit {element.Name.LocalName} '{value}' automatically");
            }
        }
    }

    /// <summary>Notes SDK-style projects whose effective target frameworks are all .NET Framework.</summary>
    private static void CheckFrameworkOnly(MsBuildModel model, EditableXml project, NoteList notes)
    {
        var effective = TfmElements(project).ToList();
        if (effective.Count == 0)
        {
            effective = model.AncestorBuildProps(project.RelativePath).Select(p => TfmElements(p).ToList()).FirstOrDefault(l => l.Count > 0) ?? [];
        }

        var entries = effective
            .SelectMany(e => e.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();
        if (entries.Count > 0 && entries.All(e => TargetFrameworkMapper.Classify(e) == TfmKind.NetFramework))
        {
            notes.Add($"Skipped .NET Framework project {project.RelativePath} (report only; migration is out of scope)");
        }
    }

    private static void CheckRuntimeFrameworkVersion(EditableXml file, int targetMajor, NoteList notes)
    {
        foreach (var element in file.Descendants("RuntimeFrameworkVersion"))
        {
            var value = element.Value.Trim();
            var dot = value.IndexOf('.');
            if (dot > 0 && int.TryParse(value[..dot], NumberStyles.None, CultureInfo.InvariantCulture, out var major) && major < targetMajor)
            {
                notes.Add($"{file.RelativePath}: RuntimeFrameworkVersion {value} pins an old runtime; review and remove it");
            }
        }
    }
}

/// <summary>Insertion-ordered, de-duplicated list of notes.</summary>
internal sealed class NoteList
{
    private readonly List<string> _items = [];
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public void Add(string note)
    {
        if (_seen.Add(note))
        {
            _items.Add(note);
        }
    }

    public IReadOnlyList<string> ToList() => _items.ToList();
}
