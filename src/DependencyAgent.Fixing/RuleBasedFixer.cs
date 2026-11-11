using System.Text.Json;
using System.Text.RegularExpressions;
using DependencyAgent.Core;
using DependencyAgent.Core.Configuration;
using DependencyAgent.Core.Model;
using DependencyAgent.Core.Processes;
using Microsoft.Extensions.Logging;

namespace DependencyAgent.Fixing;

/// <summary>One declarative rewrite rule from <c>rewrite-rules.json</c>.</summary>
public sealed class RewriteRule
{
    public string Id { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public string? Description { get; set; }

    public List<string> Diagnostics { get; set; } = [];

    public string? FilePattern { get; set; }

    /// <summary>.NET regular expression.</summary>
    public string Find { get; set; } = "";

    /// <summary>Replacement (supports <c>$1</c> / <c>${name}</c> group references).</summary>
    public string Replace { get; set; } = "";

    public string? Docs { get; set; }
}

public sealed class RewriteRuleFile
{
    public List<RewriteRule> Rules { get; set; } = [];
}

/// <summary>
/// Deterministic fixer: <c>dotnet format analyzers</c> for configured diagnostic ids, then declarative regex rewrite rules.
/// Only touches files that failing diagnostics point at, and only files the guard settings allow.
/// </summary>
public sealed class RuleBasedFixer : ICodeFixer
{
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private static readonly Regex DiagnosticIdShape = new(@"^[A-Za-z]{1,10}[0-9]{1,6}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IProcessRunner _runner;

    public RuleBasedFixer(IProcessRunner runner) => _runner = runner;

    public string Name => "rules";

    public async Task<FixResult> FixAsync(RepoContext context, FixRequest request, CancellationToken cancellationToken)
    {
        var settings = context.Settings;
        if (!settings.RuleFixes.Enabled)
        {
            return FixResult.None("Rule-based fixes are disabled.");
        }

        var root = context.WorkingDirectory;
        var diagnostics = request.FailedVerification.Diagnostics.Concat(request.NewWarnings).ToList();

        // diagnostic id (upper) -> repo-relative files it points at (that exist inside the repo).
        var filesById = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in diagnostics)
        {
            if (!filesById.TryGetValue(d.Code, out var set))
            {
                filesById[d.Code] = set = new SortedSet<string>(StringComparer.Ordinal);
            }

            if (RepoPaths.ToRelative(root, d.File) is { } rel && File.Exists(Path.Combine(root, rel)))
            {
                set.Add(rel);
            }
        }

        var changes = new List<ChangeRecord>();
        var notes = new List<string>();

        await RunFormatAsync(context, filesById, changes, notes, cancellationToken);
        RunRewriteRules(context, filesById, changes, notes);

        return new FixResult(changes.Count > 0, changes, notes);
    }

    private async Task RunFormatAsync(RepoContext context, Dictionary<string, SortedSet<string>> filesById, List<ChangeRecord> changes, List<string> notes, CancellationToken ct)
    {
        var settings = context.Settings;
        var root = context.WorkingDirectory;
        var ids = settings.RuleFixes.FormatDiagnostics
            .Where(id => DiagnosticIdShape.IsMatch(id) && filesById.ContainsKey(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var fileToIds = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            foreach (var file in filesById[id])
            {
                if (!RepoPaths.IsEditable(settings.Guards, file))
                {
                    continue;
                }

                if (!fileToIds.TryGetValue(file, out var list))
                {
                    fileToIds[file] = list = [];
                }

                list.Add(id);
            }
        }

        if (fileToIds.Count == 0)
        {
            notes.Add("dotnet format: no editable files referenced by the diagnostics.");
            return;
        }

        var before = fileToIds.Keys.ToDictionary(f => f, f => TextFileIo.Sha256(Path.Combine(root, f)), StringComparer.Ordinal);

        // Build the invocations: one for the resolved solution/project, otherwise one per project containing the files.
        var invocations = new List<(string Target, List<string> Files, List<string> Ids)>();
        var target = BuildTargetResolver.Resolve(root, settings.Verification);
        if (target is not null)
        {
            invocations.Add((target, fileToIds.Keys.ToList(), ids));
        }
        else
        {
            foreach (var group in fileToIds.Keys.GroupBy(f => FindOwningProject(root, f) ?? "", StringComparer.Ordinal))
            {
                if (group.Key.Length == 0)
                {
                    notes.Add($"dotnet format: no project file found for {group.Count()} file(s); skipped.");
                    continue;
                }

                var groupFiles = group.ToList();
                var groupIds = groupFiles.SelectMany(f => fileToIds[f]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                invocations.Add((group.Key, groupFiles, groupIds));
            }
        }

        foreach (var (invTarget, files, invIds) in invocations)
        {
            var args = new List<string> { "format", "analyzers", invTarget, "--no-restore", "--severity", "info", "--diagnostics" };
            args.AddRange(invIds);
            args.Add("--include");
            args.AddRange(files);
            var result = await _runner.RunAsync(new ProcessSpec
            {
                FileName = "dotnet",
                Arguments = args,
                WorkingDirectory = root,
                SecretScope = SecretScope.None,
                Timeout = TimeSpan.FromMinutes(10),
            }, ct);
            if (!result.Succeeded)
            {
                var tail = PipelineCommandNeutralizer.Neutralize(GitCli.Tail(result.CombinedOutput.Trim(), 600));
                notes.Add($"dotnet format analyzers failed for {invTarget} (exit {result.ExitCode}{(result.TimedOut ? ", timed out" : "")}): {tail}");
            }
        }

        foreach (var (file, fileIds) in fileToIds)
        {
            var full = Path.Combine(root, file);
            if (!File.Exists(full) || TextFileIo.Sha256(full) == before[file])
            {
                continue;
            }

            foreach (var id in fileIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                changes.Add(new ChangeRecord(ChangeKind.RuleFix, file, id, Detail: DocsUrlForDiagnostic(id)));
            }

            notes.Add($"dotnet format analyzers fixed {string.Join(", ", fileIds.Distinct(StringComparer.OrdinalIgnoreCase))} in {file}.");
        }
    }

    private static string? DocsUrlForDiagnostic(string id) =>
        id.StartsWith("CA", StringComparison.OrdinalIgnoreCase) ? $"https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/{id.ToLowerInvariant()}" : null;

    private static string? FindOwningProject(string root, string relativeFile)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(root, relativeFile)));
        var rootFull = Path.GetFullPath(root);
        while (dir is not null && dir.StartsWith(rootFull, StringComparison.Ordinal))
        {
            var project = Directory.EnumerateFiles(dir)
                .Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal)
                .FirstOrDefault();
            if (project is not null)
            {
                return Path.GetRelativePath(rootFull, project).Replace('\\', '/');
            }

            if (string.Equals(dir, rootFull, StringComparison.Ordinal))
            {
                break;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    private static void RunRewriteRules(RepoContext context, Dictionary<string, SortedSet<string>> filesById, List<ChangeRecord> changes, List<string> notes)
    {
        var settings = context.Settings;
        var root = context.WorkingDirectory;
        var rules = LoadRules(context, notes);
        foreach (var rule in rules)
        {
            if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.Find) || rule.Diagnostics.Count == 0)
            {
                continue;
            }

            var applicableFiles = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var id in rule.Diagnostics)
            {
                if (filesById.TryGetValue(id, out var files))
                {
                    applicableFiles.UnionWith(files);
                }
            }

            if (applicableFiles.Count == 0)
            {
                continue;
            }

            Regex regex;
            try
            {
                regex = new Regex(rule.Find, RegexOptions.CultureInvariant, RegexTimeout);
            }
            catch (ArgumentException ex)
            {
                notes.Add($"Rewrite rule '{rule.Id}' skipped: invalid regex ({ex.Message}).");
                continue;
            }

            var pattern = string.IsNullOrWhiteSpace(rule.FilePattern) ? "**/*" : rule.FilePattern;
            foreach (var file in applicableFiles)
            {
                if (!GlobMatcher.IsMatch(pattern, file) || !RepoPaths.IsEditable(settings.Guards, file))
                {
                    continue;
                }

                var full = Path.Combine(root, file);
                var decoded = TextFileIo.TryRead(full);
                if (decoded is null)
                {
                    continue;
                }

                string updated;
                var count = 0;
                try
                {
                    updated = regex.Replace(decoded.Text, m =>
                    {
                        count++;
                        return m.Result(rule.Replace);
                    });
                }
                catch (RegexMatchTimeoutException)
                {
                    notes.Add($"Rewrite rule '{rule.Id}' timed out on {file}; skipped.");
                    continue;
                }
                catch (ArgumentException ex)
                {
                    notes.Add($"Rewrite rule '{rule.Id}' has an invalid replacement ({ex.Message}); skipped.");
                    break;
                }

                if (count == 0 || string.Equals(updated, decoded.Text, StringComparison.Ordinal))
                {
                    continue;
                }

                TextFileIo.Write(full, updated, decoded);
                var subject = rule.Diagnostics.FirstOrDefault(d => filesById.TryGetValue(d, out var fs) && fs.Contains(file)) ?? rule.Id;
                changes.Add(new ChangeRecord(ChangeKind.RuleFix, file, subject, Detail: rule.Docs));
                notes.Add($"Rewrite rule '{rule.Id}' applied to {file} ({count} replacement(s)).");
                context.Logger.LogInformation("Rewrite rule {Rule} applied to {File} ({Count} replacements)", rule.Id, file, count);
            }
        }
    }

    internal static IReadOnlyList<RewriteRule> LoadRules(RepoContext context, List<string> notes)
    {
        var fileName = context.Settings.RuleFixes.RewriteRulesFile;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return [];
        }

        var path = Path.Combine(context.Options.ConfigDirectory, fileName);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<RewriteRuleFile>(File.ReadAllText(path), JsonOptions);
            return parsed?.Rules ?? [];
        }
        catch (JsonException ex)
        {
            notes.Add($"Rewrite rules file '{fileName}' could not be parsed: {ex.Message}");
            return [];
        }
    }
}
