using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace DependencyAgent.Core.Configuration;

/// <summary>
/// Minimal, dependency-free glob matching used for package ids, repository filters and file paths.
/// Paths are compared with forward slashes and case-insensitively.
/// <c>**</c> matches any number of path segments (including none), <c>*</c> matches within a segment,
/// <c>?</c> matches one character. A pattern without <c>/</c> that starts with <c>**/</c> also matches root-level files.
/// </summary>
public static class GlobMatcher
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new();

    public static bool IsMatch(string pattern, string value)
    {
        var normalizedValue = Normalize(value);
        return Cache.GetOrAdd(Normalize(pattern), Compile).IsMatch(normalizedValue);
    }

    public static bool IsMatchAny(IEnumerable<string> patterns, string value) => patterns.Any(p => IsMatch(p, value));

    public static string Normalize(string path)
    {
        var p = path.Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal))
        {
            p = p[2..];
        }

        return p.TrimStart('/');
    }

    private static Regex Compile(string pattern)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*')
            {
                var isDouble = i + 1 < pattern.Length && pattern[i + 1] == '*';
                if (isDouble)
                {
                    var followedBySlash = i + 2 < pattern.Length && pattern[i + 2] == '/';
                    if (followedBySlash)
                    {
                        sb.Append("(?:.*/)?");
                        i += 2;
                    }
                    else
                    {
                        sb.Append(".*");
                        i += 1;
                    }
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }

        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}
