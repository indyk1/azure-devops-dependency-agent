using System.Text;

namespace DependencyAgent.Updates.Infrastructure;

/// <summary>A UTF-8 text file that remembers its BOM so it can be written back byte-for-byte except for edits.</summary>
internal sealed class TextFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private TextFile(string fullPath, string relativePath, string text, bool hasBom)
    {
        FullPath = fullPath;
        RelativePath = relativePath;
        Text = text;
        HasBom = hasBom;
    }

    public string FullPath { get; }

    /// <summary>Repo-relative, forward slashes.</summary>
    public string RelativePath { get; }

    public string Text { get; }

    public bool HasBom { get; }

    public static TextFile Load(string root, string fullPath)
    {
        var bytes = File.ReadAllBytes(fullPath);
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
        {
            throw new NotSupportedException("UTF-16 encoded files are not supported.");
        }

        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var offset = hasBom ? 3 : 0;
        var text = Utf8NoBom.GetString(bytes, offset, bytes.Length - offset);
        return new TextFile(fullPath, PathUtil.Relative(root, fullPath), text, hasBom);
    }

    /// <summary>Writes <paramref name="newText"/> when it differs from the loaded text. Returns true when written.</summary>
    public bool Save(string newText)
    {
        if (string.Equals(newText, Text, StringComparison.Ordinal))
        {
            return false;
        }

        var body = Utf8NoBom.GetBytes(newText);
        using var stream = new FileStream(FullPath, FileMode.Create, FileAccess.Write, FileShare.None);
        if (HasBom)
        {
            stream.Write([0xEF, 0xBB, 0xBF]);
        }

        stream.Write(body);
        return true;
    }
}

internal static class PathUtil
{
    public static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>Directory part of a repo-relative path; empty string for the repository root.</summary>
    public static string DirectoryOf(string relativePath)
    {
        var i = relativePath.LastIndexOf('/');
        return i < 0 ? "" : relativePath[..i];
    }
}
