using System.Text;
using System.Text.RegularExpressions;
using DependencyAgent.Core;
using DependencyAgent.Core.Configuration;
using DependencyAgent.Core.Model;
using DependencyAgent.Updates.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DependencyAgent.Updates.Steps;

/// <summary>Moves official <c>mcr.microsoft.com/dotnet/{sdk,aspnet,runtime,runtime-deps}</c> base images to the target major version.</summary>
public sealed partial class DockerImageUpdateStep : IUpdateStep
{
    private static readonly string[] SupportedRepositories = ["sdk", "aspnet", "runtime", "runtime-deps"];

    [GeneratedRegex(@"^(?<pre>\s*FROM\s+(?:--\S+\s+)*)(?<image>\S+)(?<post>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FromRegex();

    [GeneratedRegex(@"^(?<host>[^/\s]+)/(?<path>[^:@\s]+)(?::(?<tag>[^@\s]+))?(?<digest>@\S+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ImageRegex();

    [GeneratedRegex(@"^(?:\$\{(?<name>[A-Za-z_]\w*)\}|\$(?<name2>[A-Za-z_]\w*))$", RegexOptions.CultureInvariant)]
    private static partial Regex VariableTagRegex();

    [GeneratedRegex(@"^(?<pre>\s*ARG\s+(?<name>[A-Za-z_]\w*)=)(?<quote>[""']?)(?<value>[^""'\s]*)(?<post>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArgRegex();

    public string Name => "docker";

    public Task<UpdateStepResult> ApplyAsync(RepoContext context, CancellationToken cancellationToken)
    {
        if (!context.Settings.Docker.Enabled)
        {
            return Task.FromResult(UpdateStepResult.Empty);
        }

        return Task.FromResult(Apply(context, cancellationToken));
    }

    private sealed record Line(string Text, string Terminator);

    private static UpdateStepResult Apply(RepoContext context, CancellationToken ct)
    {
        var changes = new List<ChangeRecord>();
        var notes = new NoteList();

        if (!TargetFrameworkMapper.TryParseTarget(context.Settings.TargetFramework, out var targetMajor, out _))
        {
            notes.Add($"Target framework '{context.Settings.TargetFramework}' is not a modern .NET moniker; docker step skipped");
            return new UpdateStepResult(changes, notes.ToList());
        }

        var layout = RepoLayout.Scan(context.WorkingDirectory);
        foreach (var relative in layout.AllFiles.Where(f => GlobMatcher.IsMatchAny(context.Settings.Docker.FilePatterns, f)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                ProcessFile(context, relative, targetMajor, changes, notes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                notes.Add($"Could not update {relative}: {ex.Message}");
            }
        }

        return new UpdateStepResult(changes, notes.ToList());
    }

    private static void ProcessFile(RepoContext context, string relative, int targetMajor, List<ChangeRecord> changes, NoteList notes)
    {
        var file = TextFile.Load(context.WorkingDirectory, Path.Combine(context.WorkingDirectory, relative));
        var lines = SplitLines(file.Text);
        var edited = false;

        // ARG name -> definition line indexes (in order), so a FROM uses the closest preceding default.
        var args = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Count; i++)
        {
            if (ArgRegex().Match(lines[i].Text) is { Success: true } a)
            {
                (args.TryGetValue(a.Groups["name"].Value, out var list) ? list : args[a.Groups["name"].Value] = []).Add(i);
            }
        }

        var argUpdated = new HashSet<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (FromRegex().Match(lines[i].Text) is not { Success: true } from || ImageRegex().Match(from.Groups["image"].Value) is not { Success: true } image)
            {
                continue;
            }

            if (!image.Groups["host"].Value.Equals("mcr.microsoft.com", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = image.Groups["path"].Value;
            var lowerPath = path.ToLowerInvariant();
            if (!lowerPath.StartsWith("dotnet/", StringComparison.Ordinal))
            {
                continue;
            }

            var imageName = $"{image.Groups["host"].Value}/{path}";
            var rest = lowerPath["dotnet/".Length..];
            if (rest.StartsWith("nightly/", StringComparison.Ordinal))
            {
                notes.Add($"{relative}:{i + 1}: skipped nightly image {imageName}");
                continue;
            }

            var legacy = rest.StartsWith("core/", StringComparison.Ordinal);
            var repository = legacy ? rest["core/".Length..] : rest;
            if (!SupportedRepositories.Contains(repository, StringComparer.Ordinal))
            {
                continue;
            }

            var newImageName = legacy ? $"{image.Groups["host"].Value}/dotnet/{repository}" : imageName;
            var tag = image.Groups["tag"].Success ? image.Groups["tag"].Value : null;
            if (image.Groups["digest"].Success)
            {
                var digestMap = tag is not null && DockerTagMapper.IsVersionLike(tag) ? DockerTagMapper.Map(tag, targetMajor) : null;
                if (digestMap is null || digestMap.NewTag is not null || digestMap.Notes.Count > 0)
                {
                    notes.Add($"{relative}:{i + 1}: skipped digest-pinned image {imageName}{(tag is null ? "" : ":" + tag)}; update it manually");
                }

                continue;
            }

            if (tag is null)
            {
                continue;
            }

            if (VariableTagRegex().Match(tag) is { Success: true } variable)
            {
                var name = variable.Groups["name"].Success ? variable.Groups["name"].Value : variable.Groups["name2"].Value;
                var definition = args.TryGetValue(name, out var defs) ? defs.LastOrDefault(d => d < i, -1) : -1;
                if (definition < 0)
                {
                    notes.Add($"{relative}:{i + 1}: {imageName} tag uses ${{{name}}} but no ARG default was found in the same file");
                    continue;
                }

                if (argUpdated.Contains(definition))
                {
                    // Already rewritten for an earlier FROM; still record the image it affects.
                    var done = ArgRegex().Match(lines[definition].Text);
                    changes.Add(new ChangeRecord(ChangeKind.DockerImage, relative, imageName, null, done.Groups["value"].Value, $"via ARG {name}"));
                    continue;
                }

                var arg = ArgRegex().Match(lines[definition].Text);
                var argValue = arg.Groups["value"].Value;
                var argResult = DockerTagMapper.Map(argValue, targetMajor);
                foreach (var note in argResult.Notes)
                {
                    notes.Add($"{relative}:{definition + 1}: {note}");
                }

                if (argResult.NewTag is null)
                {
                    continue;
                }

                lines[definition] = lines[definition] with
                {
                    Text = arg.Groups["pre"].Value + arg.Groups["quote"].Value + argResult.NewTag + arg.Groups["post"].Value,
                };
                argUpdated.Add(definition);
                edited = true;
                changes.Add(new ChangeRecord(ChangeKind.DockerImage, relative, newImageName, argValue, argResult.NewTag, $"via ARG {name}"));
                continue;
            }

            if (tag.Contains('$', StringComparison.Ordinal))
            {
                notes.Add($"{relative}:{i + 1}: {imageName} tag '{tag}' uses a variable expression; not edited");
                continue;
            }

            var result = DockerTagMapper.Map(tag, targetMajor);
            foreach (var note in result.Notes)
            {
                notes.Add($"{relative}:{i + 1}: {note}");
            }

            if (result.NewTag is null)
            {
                continue;
            }

            var newImage = $"{newImageName}:{result.NewTag}";
            lines[i] = lines[i] with { Text = from.Groups["pre"].Value + newImage + from.Groups["post"].Value };
            edited = true;
            if (legacy)
            {
                notes.Add($"{relative}:{i + 1}: legacy image path {imageName} replaced by {newImageName}");
            }

            changes.Add(new ChangeRecord(ChangeKind.DockerImage, relative, newImageName, tag, result.NewTag));
        }

        if (edited)
        {
            var text = new StringBuilder();
            foreach (var line in lines)
            {
                text.Append(line.Text).Append(line.Terminator);
            }

            if (file.Save(text.ToString()))
            {
                context.Logger.LogInformation("Updated base images in {File}", relative);
            }
        }
    }

    private static List<Line> SplitLines(string text)
    {
        var lines = new List<Line>();
        var pos = 0;
        while (pos < text.Length)
        {
            var end = text.AsSpan(pos).IndexOfAny('\r', '\n');
            if (end < 0)
            {
                lines.Add(new Line(text[pos..], ""));
                break;
            }

            end += pos;
            var terminatorLength = text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1;
            lines.Add(new Line(text[pos..end], text.Substring(end, terminatorLength)));
            pos = end + terminatorLength;
        }

        return lines;
    }
}
