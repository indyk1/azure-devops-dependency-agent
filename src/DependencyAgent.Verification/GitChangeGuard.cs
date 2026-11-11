using DependencyAgent.Core;
using DependencyAgent.Core.Configuration;
using DependencyAgent.Core.Model;
using DependencyAgent.Core.Processes;

namespace DependencyAgent.Verification;

/// <summary>
/// <see cref="IChangeGuard"/> implemented with plain git. All git invocations run with <see cref="SecretScope.None"/>.
/// <para>
/// <see cref="GuardResult.ChangedFiles"/> / <see cref="GuardResult.ChangedLines"/> are the budget counts (budget-excluded files
/// omitted); <see cref="GuardResult.Files"/> lists every changed file.
/// </para>
/// </summary>
public sealed class GitChangeGuard : IChangeGuard
{
    private readonly IProcessRunner _runner;

    public GitChangeGuard(IProcessRunner runner)
    {
        _runner = runner;
    }

    private sealed record Change(string Path, string? OldPath, int Lines);

    public async Task<GuardResult> EvaluateAsync(RepoContext context, string sinceRef, GuardScope scope, CancellationToken cancellationToken)
    {
        var root = context.WorkingDirectory;
        var guards = context.Settings.Guards;

        var changes = new Dictionary<string, Change>(StringComparer.Ordinal);

        var numstat = await GitAsync(root, ["diff", "--numstat", "-z", "-M", "--no-ext-diff", "--no-color", sinceRef, "--"], cancellationToken);
        foreach (var c in ParseNumstat(numstat.StandardOutput))
        {
            changes[c.Path] = c;
        }

        var untracked = await GitAsync(root, ["ls-files", "--others", "--exclude-standard", "-z"], cancellationToken);
        foreach (var path in SplitNul(untracked.StandardOutput).Where(p => p.Length > 0))
        {
            changes.TryAdd(path, new Change(path, null, CountLines(Path.Combine(root, path))));
        }

        var files = changes.Values.OrderBy(c => c.Path, StringComparer.Ordinal).ToList();
        var counted = files.Where(c => !GlobMatcher.IsMatchAny(guards.BudgetExcludePatterns, c.Path)).ToList();
        var changedFiles = counted.Count;
        var changedLines = counted.Sum(c => c.Lines);

        var violations = new List<string>();
        if (changedFiles > guards.MaxChangedFiles)
        {
            violations.Add($"Changed files ({changedFiles}) exceed the budget of {guards.MaxChangedFiles}.");
        }

        if (changedLines > guards.MaxChangedLines)
        {
            violations.Add($"Changed lines ({changedLines}) exceed the budget of {guards.MaxChangedLines}.");
        }

        if (scope == GuardScope.CodeFix)
        {
            foreach (var c in files)
            {
                foreach (var path in new[] { c.Path, c.OldPath }.Where(p => p is not null).Select(p => p!))
                {
                    if (Disallowed(guards, path) is { } reason)
                    {
                        violations.Add($"'{path}' {reason}.");
                    }
                }
            }
        }

        return new GuardResult(violations.Count == 0, violations, changedFiles, changedLines, files.Select(f => f.Path).ToList());
    }

    public async Task<IReadOnlyList<string>> RevertDisallowedAsync(RepoContext context, CancellationToken cancellationToken)
    {
        var root = context.WorkingDirectory;
        var guards = context.Settings.Guards;
        var status = await GitAsync(root, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], cancellationToken);
        var reverted = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var entry in ParseStatus(status.StandardOutput))
        {
            var paths = new[] { entry.Path, entry.OrigPath }.Where(p => p is not null).Select(p => p!).ToList();
            if (paths.Any(p => !IsSafePath(root, p)) || paths.All(p => Disallowed(guards, p) is null))
            {
                continue;
            }

            if (entry.X == '?' && entry.Y == '?')
            {
                DeleteFile(root, entry.Path);
                reverted.Add(entry.Path);
            }
            else if (entry.OrigPath is not null)
            {
                // staged rename/copy: put the original back and drop the new path
                await RestoreAsync(root, entry.OrigPath, cancellationToken);
                await RemoveAsync(root, entry.Path, cancellationToken);
                reverted.Add(entry.OrigPath);
                reverted.Add(entry.Path);
            }
            else if (entry.X == 'A')
            {
                await RemoveAsync(root, entry.Path, cancellationToken);
                reverted.Add(entry.Path);
            }
            else
            {
                await RestoreAsync(root, entry.Path, cancellationToken);
                reverted.Add(entry.Path);
            }
        }

        return reverted.ToList();
    }

    /// <summary>Returns why the path is not editable by code fixers, or null when it is allowed.</summary>
    private static string? Disallowed(GuardSettings guards, string path)
    {
        var normalized = GlobMatcher.Normalize(path);
        foreach (var pattern in guards.ProtectedPatterns)
        {
            if (GlobMatcher.IsMatch(pattern, normalized))
            {
                return $"matches protected pattern '{pattern}'";
            }
        }

        if (normalized == ".git" || normalized.StartsWith(".git/", StringComparison.Ordinal))
        {
            return "is inside .git (always protected)";
        }

        return GlobMatcher.IsMatchAny(guards.AllowedEditPatterns, normalized) ? null : "is not within the allowed edit patterns";
    }

    private async Task<ProcessResult> GitAsync(string root, IReadOnlyList<string> args, CancellationToken cancellationToken, bool throwOnError = true)
    {
        var all = new List<string> { "-c", "core.quotepath=false" };
        all.AddRange(args);
        var result = await _runner.RunAsync(new ProcessSpec
        {
            FileName = "git",
            Arguments = all,
            WorkingDirectory = root,
            SecretScope = SecretScope.None,
            Timeout = TimeSpan.FromMinutes(5),
            Environment = new Dictionary<string, string?> { ["GIT_LITERAL_PATHSPECS"] = "1", ["GIT_OPTIONAL_LOCKS"] = "0" },
        }, cancellationToken);

        if (throwOnError && !result.Succeeded)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args.Take(2))} failed (exit {result.ExitCode}): {result.StandardError.Trim()}");
        }

        return result;
    }

    private Task RestoreAsync(string root, string path, CancellationToken ct) =>
        GitAsync(root, ["restore", "--source=HEAD", "--staged", "--worktree", "--", path], ct);

    private async Task RemoveAsync(string root, string path, CancellationToken ct)
    {
        await GitAsync(root, ["rm", "-f", "-q", "--cached", "--ignore-unmatch", "--", path], ct);
        DeleteFile(root, path);
    }

    private static void DeleteFile(string root, string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        if (File.Exists(full))
        {
            File.Delete(full);
        }

        // Remove directories that became empty, never the repository root and never .git.
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var dir = Path.GetDirectoryName(full);
        while (dir is not null && dir.Length > rootFull.Length && dir.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Directory.Delete(dir);
            dir = Path.GetDirectoryName(dir);
        }
    }

    /// <summary>Rejects absolute paths, traversal, <c>.git</c> and nested repositories (submodules).</summary>
    private static bool IsSafePath(string root, string relativePath)
    {
        var normalized = GlobMatcher.Normalize(relativePath);
        if (normalized.Length == 0 || normalized.EndsWith('/') || normalized == ".git" || normalized.StartsWith(".git/", StringComparison.Ordinal)
            || normalized.Split('/').Any(s => s == ".." || s == ".git"))
        {
            return false;
        }

        var full = Path.GetFullPath(Path.Combine(root, normalized));
        return full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static int CountLines(string fullPath)
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.LinkTarget is not null)
            {
                return info.LinkTarget is not null ? 1 : 0;
            }

            using var stream = File.OpenRead(fullPath);
            var buffer = new byte[64 * 1024];
            var lines = 0;
            var last = (byte)'\n';
            var first = true;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (first)
                {
                    first = false;
                    if (Array.IndexOf(buffer, (byte)0, 0, Math.Min(read, 8000)) >= 0)
                    {
                        return 0; // binary, same as git numstat
                    }
                }

                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] == (byte)'\n')
                    {
                        lines++;
                    }
                }

                last = buffer[read - 1];
            }

            return !first && last != (byte)'\n' ? lines + 1 : lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Parses <c>git diff --numstat -z</c>: <c>added\tdeleted\tpath\0</c> or, for renames, <c>added\tdeleted\t\0old\0new\0</c>.</summary>
    private static List<Change> ParseNumstat(string output)
    {
        var result = new List<Change>();
        var parts = SplitNul(output);
        for (var i = 0; i < parts.Length; i++)
        {
            var record = parts[i];
            if (record.Length == 0)
            {
                continue;
            }

            var fields = record.Split('\t', 3);
            if (fields.Length < 3)
            {
                continue;
            }

            var lines = int.TryParse(fields[0], out var added) && int.TryParse(fields[1], out var removed) ? added + removed : 0; // "-" = binary
            if (fields[2].Length == 0)
            {
                if (i + 2 >= parts.Length)
                {
                    break;
                }

                var oldPath = parts[i + 1];
                var newPath = parts[i + 2];
                i += 2;
                result.Add(new Change(newPath, oldPath, lines));
            }
            else
            {
                result.Add(new Change(fields[2], null, lines));
            }
        }

        return result;
    }

    // IProcessRunner captures output line by line and re-joins with newlines, which appends a stray "\n" after the final NUL.
    // (Consequence: paths that themselves contain newline characters are not supported.)
    private static string[] SplitNul(string output) => output.TrimEnd('\r', '\n').Split('\0');

    private sealed record StatusEntry(char X, char Y, string Path, string? OrigPath);

    private static List<StatusEntry> ParseStatus(string output)
    {
        var result = new List<StatusEntry>();
        var parts = SplitNul(output);
        for (var i = 0; i < parts.Length; i++)
        {
            var record = parts[i];
            if (record.Length < 4)
            {
                continue;
            }

            char x = record[0], y = record[1];
            var path = record[3..];
            string? orig = null;
            if (x is 'R' or 'C' || y is 'R' or 'C')
            {
                orig = i + 1 < parts.Length ? parts[++i] : null;
            }

            result.Add(new StatusEntry(x, y, path, orig));
        }

        return result;
    }
}
