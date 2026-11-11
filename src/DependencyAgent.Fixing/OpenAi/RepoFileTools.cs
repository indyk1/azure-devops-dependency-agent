using System.Text;
using System.Text.RegularExpressions;
using DependencyAgent.Core.Configuration;

namespace DependencyAgent.Fixing.OpenAi;

/// <summary>A refused or failed tool call. The message is returned to the model as the tool result.</summary>
public sealed class ToolException : Exception
{
    public ToolException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// File tools for the OpenAI tool loop, confined to one repository: no <c>..</c> segments, no absolute paths outside the repo,
/// no symlink escapes, no <c>.git</c>. Writes additionally require the allowed/protected edit patterns.
/// </summary>
public sealed class RepoFileTools
{
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    private static readonly string[] SkippedDirectories = [".git", "bin", "obj", "node_modules", ".vs"];

    private readonly string _root;
    private readonly string _rootReal;
    private readonly IReadOnlyList<string> _allowed;
    private readonly IReadOnlyList<string> _protected;
    private readonly int _maxReadChars;
    private readonly int _maxListResults;
    private readonly int _maxSearchMatches;

    public RepoFileTools(string repositoryRoot, IReadOnlyList<string> allowedEditPatterns, IReadOnlyList<string> protectedPatterns, int maxReadBytes = 200_000, int maxListResults = 500, int maxSearchMatches = 100)
    {
        _root = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _rootReal = ResolveLinks(_root) ?? _root;
        _allowed = allowedEditPatterns;
        _protected = protectedPatterns;
        _maxReadChars = maxReadBytes;
        _maxListResults = maxListResults;
        _maxSearchMatches = maxSearchMatches;
    }

    public string ListFiles(string? glob)
    {
        var pattern = string.IsNullOrWhiteSpace(glob) ? "**/*" : glob;
        var results = new List<string>();
        var truncated = false;
        foreach (var rel in EnumerateRepoFiles())
        {
            if (!GlobMatcher.IsMatch(pattern, rel))
            {
                continue;
            }

            if (results.Count >= _maxListResults)
            {
                truncated = true;
                break;
            }

            results.Add(rel);
        }

        if (results.Count == 0)
        {
            return "(no files matched)";
        }

        var text = string.Join('\n', results);
        return truncated ? text + $"\n[... list truncated at {_maxListResults} files; narrow the glob ...]" : text;
    }

    public string ReadFile(string path, int? startLine = null, int? endLine = null)
    {
        var (full, rel) = ResolveExisting(path);
        if (!File.Exists(full))
        {
            throw new ToolException($"File not found: {rel}");
        }

        var info = new FileInfo(full);
        if (info.Length > 5_000_000)
        {
            throw new ToolException($"File too large to read ({info.Length} bytes): {rel}");
        }

        var decoded = TextFileIo.TryRead(full) ?? throw new ToolException($"Not a readable text file: {rel}");
        var lines = decoded.Text.Split('\n');
        var start = Math.Max(1, startLine ?? 1);
        var end = Math.Min(lines.Length, endLine ?? lines.Length);
        if (start > end)
        {
            throw new ToolException($"Invalid line range {start}-{end}; file has {lines.Length} lines.");
        }

        var sb = new StringBuilder();
        var width = end.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
        for (var i = start; i <= end; i++)
        {
            var line = lines[i - 1].TrimEnd('\r');
            var piece = $"{i.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(width)}| {line}\n";
            if (sb.Length + piece.Length > _maxReadChars)
            {
                sb.Append($"[... output truncated at {_maxReadChars} characters; continue with start_line={i} ...]");
                break;
            }

            sb.Append(piece);
        }

        return $"{rel} (lines {start}-{end} of {lines.Length}; the 'N| ' prefix is not part of the file)\n{sb.ToString().TrimEnd()}";
    }

    public string SearchText(string regex, string? glob = null)
    {
        Regex re;
        try
        {
            re = new Regex(regex, RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            throw new ToolException($"Invalid regular expression: {ex.Message}");
        }

        var pattern = string.IsNullOrWhiteSpace(glob) ? "**/*" : glob;
        var matches = new List<string>();
        try
        {
            foreach (var rel in EnumerateRepoFiles())
            {
                if (!GlobMatcher.IsMatch(pattern, rel))
                {
                    continue;
                }

                var full = Path.Combine(_root, rel);
                if (new FileInfo(full).Length > 1_000_000 || TextFileIo.TryRead(full) is not { } decoded)
                {
                    continue;
                }

                var lineNo = 0;
                foreach (var line in decoded.Text.Split('\n'))
                {
                    lineNo++;
                    if (re.IsMatch(line))
                    {
                        matches.Add($"{rel}:{lineNo}: {Clip(line.TrimEnd('\r').Trim(), 240)}");
                        if (matches.Count >= _maxSearchMatches)
                        {
                            return string.Join('\n', matches) + $"\n[... stopped after {_maxSearchMatches} matches ...]";
                        }
                    }
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            throw new ToolException("The regular expression took too long to evaluate; simplify it.");
        }

        return matches.Count == 0 ? "(no matches)" : string.Join('\n', matches);
    }

    public string ReplaceInFile(string path, string oldText, string newText)
    {
        var (full, rel) = ResolveForWrite(path);
        if (string.IsNullOrEmpty(oldText))
        {
            throw new ToolException("old_text must not be empty.");
        }

        if (!File.Exists(full))
        {
            throw new ToolException($"File not found: {rel}");
        }

        var decoded = TextFileIo.TryRead(full) ?? throw new ToolException($"Not a readable text file: {rel}");
        var crlf = decoded.UsesCrLf;
        var find = AdaptLineEndings(oldText, crlf);
        var replacement = AdaptLineEndings(newText ?? "", crlf);

        var count = CountOccurrences(decoded.Text, find);
        if (count == 0)
        {
            throw new ToolException($"old_text was not found in {rel}. It must match exactly (including whitespace). Re-read the file and try again.");
        }

        if (count > 1)
        {
            throw new ToolException($"old_text matches {count} places in {rel}; include more surrounding context so it matches exactly once.");
        }

        var idx = decoded.Text.IndexOf(find, StringComparison.Ordinal);
        var updated = string.Concat(decoded.Text.AsSpan(0, idx), replacement, decoded.Text.AsSpan(idx + find.Length));
        TextFileIo.Write(full, updated, decoded);
        return $"Replaced 1 occurrence in {rel}.";
    }

    public string WriteFile(string path, string content)
    {
        var (full, rel) = ResolveForWrite(path);
        content ??= "";
        if (content.Length > 1_000_000)
        {
            throw new ToolException("Content too large (limit 1,000,000 characters).");
        }

        if (File.Exists(full))
        {
            var decoded = TextFileIo.TryRead(full) ?? throw new ToolException($"Existing file is not a readable text file: {rel}");
            TextFileIo.Write(full, AdaptLineEndings(content, decoded.UsesCrLf), decoded);
            return $"Wrote {content.Length} characters to existing file {rel}.";
        }

        var dir = Path.GetDirectoryName(full)!;
        EnsureNoEscape(dir);
        Directory.CreateDirectory(dir);
        EnsureNoEscape(dir);
        File.WriteAllText(full, content, new UTF8Encoding(false));
        return $"Created {rel} ({content.Length} characters).";
    }

    /// <summary>Resolves a model-supplied path to a full path inside the repository, or throws.</summary>
    public (string Full, string Relative) ResolveExisting(string path)
    {
        var (full, rel) = Resolve(path);
        EnsureNoEscape(full);
        return (full, rel);
    }

    private (string Full, string Relative) ResolveForWrite(string path)
    {
        var (full, rel) = Resolve(path);
        EnsureNoEscape(full);
        if (!GlobMatcher.IsMatchAny(_allowed, rel))
        {
            throw new ToolException($"Edit refused: '{rel}' does not match the allowed edit patterns ({string.Join(", ", _allowed)}).");
        }

        if (GlobMatcher.IsMatchAny(_protected, rel))
        {
            throw new ToolException($"Edit refused: '{rel}' is protected and must not be modified.");
        }

        return (full, rel);
    }

    private (string Full, string Relative) Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ToolException("path must not be empty.");
        }

        if (path.Contains('\0'))
        {
            throw new ToolException("Invalid path.");
        }

        var normalized = path.Replace('\\', '/');
        if (normalized.Split('/').Any(s => s == ".."))
        {
            throw new ToolException("Paths containing '..' are not allowed.");
        }

        string full;
        try
        {
            full = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(_root, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ToolException("Invalid path.");
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = _root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, comparison))
        {
            throw new ToolException("Path is outside the repository.");
        }

        var rel = Path.GetRelativePath(_root, full).Replace('\\', '/');
        if (rel.Split('/').Any(s => s.Equals(".git", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ToolException("Access to .git is not allowed.");
        }

        return (full, rel);
    }

    /// <summary>Walks the path from the repository root and rejects any existing link component that resolves outside the repository.</summary>
    private void EnsureNoEscape(string fullPath)
    {
        var relative = Path.GetRelativePath(_root, fullPath);
        var current = _root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists && info.LinkTarget is null)
            {
                return; // remaining components do not exist yet
            }

            if (info.LinkTarget is not null)
            {
                var target = ResolveLinks(current);
                if (target is null || !IsInside(target))
                {
                    throw new ToolException("Path resolves outside the repository through a symbolic link.");
                }
            }
        }
    }

    private bool IsInside(string realPath)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return realPath.Equals(_rootReal, comparison) || realPath.StartsWith(_rootReal + Path.DirectorySeparatorChar, comparison)
            || realPath.Equals(_root, comparison) || realPath.StartsWith(_root + Path.DirectorySeparatorChar, comparison);
    }

    private static string? ResolveLinks(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            return Path.GetFullPath(target?.FullName ?? path).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private IEnumerable<string> EnumerateRepoFiles()
    {
        var stack = new Stack<string>();
        stack.Push(_root);
        var results = new List<string>();
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir).OrderBy(e => e, StringComparer.Ordinal).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var subdirs = new List<string>();
            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                var info = new FileInfo(entry);
                if (info.LinkTarget is not null)
                {
                    continue; // never follow links while enumerating
                }

                if (Directory.Exists(entry))
                {
                    if (!SkippedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        subdirs.Add(entry);
                    }
                }
                else
                {
                    results.Add(Path.GetRelativePath(_root, entry).Replace('\\', '/'));
                }
            }

            for (var i = subdirs.Count - 1; i >= 0; i--)
            {
                stack.Push(subdirs[i]);
            }
        }

        results.Sort(StringComparer.Ordinal);
        return results;
    }

    private static string AdaptLineEndings(string text, bool crlf)
    {
        var lf = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        return crlf ? lf.Replace("\n", "\r\n", StringComparison.Ordinal) : lf;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "...";
}
