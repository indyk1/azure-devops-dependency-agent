using System.Security.Cryptography;
using System.Text;
using DependencyAgent.Core.Configuration;
using DependencyAgent.Core.Processes;

namespace DependencyAgent.Fixing;

/// <summary>Decoded text file that remembers how to write itself back byte-compatibly (line endings stay verbatim in the text).</summary>
internal sealed record DecodedText(string Text, Encoding Encoding, bool HasBom)
{
    public bool UsesCrLf => Text.Contains("\r\n", StringComparison.Ordinal);
}

internal static class TextFileIo
{
    /// <summary>Reads a text file. Returns null for binary / undecodable files.</summary>
    public static DecodedText? TryRead(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return Decode(bytes);
    }

    public static DecodedText? Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return new DecodedText(new UTF8Encoding(true, true).GetString(bytes, 3, bytes.Length - 3), new UTF8Encoding(true), true);
        }

        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0)
        {
            return null; // UTF-32: unsupported
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return new DecodedText(new UnicodeEncoding(false, false).GetString(bytes, 2, bytes.Length - 2), new UnicodeEncoding(false, true), true);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return new DecodedText(new UnicodeEncoding(true, false).GetString(bytes, 2, bytes.Length - 2), new UnicodeEncoding(true, true), true);
        }

        if (Array.IndexOf(bytes, (byte)0) >= 0)
        {
            return null;
        }

        try
        {
            return new DecodedText(new UTF8Encoding(false, true).GetString(bytes), new UTF8Encoding(false), false);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    public static void Write(string path, string text, DecodedText original)
    {
        var enc = original.Encoding;
        var preamble = original.HasBom ? enc.GetPreamble() : [];
        var body = enc.GetBytes(text);
        var all = new byte[preamble.Length + body.Length];
        preamble.CopyTo(all, 0);
        body.CopyTo(all, preamble.Length);
        File.WriteAllBytes(path, all);
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

internal static class RepoPaths
{
    /// <summary>
    /// Converts a diagnostic file path (relative or absolute) into a repo-relative forward-slash path.
    /// Returns null when the path escapes the repository or is empty.
    /// </summary>
    public static string? ToRelative(string root, string? file)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return null;
        }

        try
        {
            var rootFull = Path.GetFullPath(root);
            var full = Path.IsPathRooted(file) ? Path.GetFullPath(file) : Path.GetFullPath(Path.Combine(rootFull, file));
            var rel = Path.GetRelativePath(rootFull, full).Replace('\\', '/');
            if (rel == "." || rel == ".." || rel.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(rel))
            {
                return null;
            }

            return rel;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    public static bool IsEditable(GuardSettings guards, string relativePath) =>
        IsEditable(guards.AllowedEditPatterns, guards.ProtectedPatterns, relativePath);

    public static bool IsEditable(IReadOnlyList<string> allowed, IReadOnlyList<string> protectedPatterns, string relativePath) =>
        GlobMatcher.IsMatchAny(allowed, relativePath) && !GlobMatcher.IsMatchAny(protectedPatterns, relativePath);
}

internal static class GitCli
{
    public static Task<ProcessResult> RunAsync(IProcessRunner runner, string workingDirectory, IReadOnlyList<string> args, CancellationToken ct) =>
        runner.RunAsync(new ProcessSpec
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = workingDirectory,
            SecretScope = SecretScope.None,
            Timeout = TimeSpan.FromMinutes(2),
        }, ct);

    /// <summary>Parses <c>git status --porcelain=v1 -z</c> into repo-relative paths with their XY status.</summary>
    public static List<(string Status, string Path)> ParseStatus(string output)
    {
        var result = new List<(string, string)>();
        var parts = output.Split('\0');
        for (var i = 0; i < parts.Length; i++)
        {
            var entry = parts[i].TrimStart('\n', '\r');
            if (entry.Length < 4)
            {
                continue;
            }

            var status = entry[..2];
            var path = entry[3..];
            if (status[0] is 'R' or 'C' || status[1] is 'R' or 'C')
            {
                i++; // the following entry is the original path
            }

            result.Add((status, path.Replace('\\', '/')));
        }

        return result;
    }

    public static string Tail(string text, int max) => text.Length <= max ? text : text[^max..];

    public static string Head(string text, int max) => text.Length <= max ? text : text[..max];
}

internal static class ExecutableLocator
{
    /// <summary>Finds an executable by explicit path or on PATH. Returns null when not found.</summary>
    public static string? Find(string nameOrPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrPath))
        {
            return null;
        }

        var exts = OperatingSystem.IsWindows()
            ? (System.Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];

        if (nameOrPath.Contains(Path.DirectorySeparatorChar) || nameOrPath.Contains(Path.AltDirectorySeparatorChar))
        {
            return Probe(Path.GetFullPath(nameOrPath), exts);
        }

        var path = System.Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (Probe(Path.Combine(dir.Trim('"'), nameOrPath), exts) is { } found)
                {
                    return found;
                }
            }
            catch (ArgumentException)
            {
                // malformed PATH entry
            }
        }

        return null;
    }

    private static string? Probe(string candidate, string[] exts)
    {
        foreach (var ext in exts)
        {
            var p = candidate + ext;
            if (File.Exists(p))
            {
                return p;
            }
        }

        return null;
    }
}
