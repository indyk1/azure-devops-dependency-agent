using System.Text;
using System.Text.RegularExpressions;

namespace DependencyAgent.LearnProxy;

/// <summary>Result of sanitizing a search query. <see cref="Removed"/> may contain sensitive text: never log it.</summary>
public sealed record SanitizedQuery(bool Allowed, string Query, IReadOnlyList<string> Removed, string? Reason);

public sealed class QuerySanitizerOptions
{
    public int MaxLength { get; init; } = 200;

    /// <summary>Case-insensitive substrings; any token containing one is removed.</summary>
    public IReadOnlyList<string> DenyTerms { get; init; } = [];
}

/// <summary>
/// Allowlist-based query sanitizer: only tokens that look like public API names, diagnostic codes, versions or
/// plain English survive. Everything else (paths, URLs, secrets, internal namespaces, deny terms, ...) is removed.
/// Deterministic; this is the privacy boundary for outbound documentation lookups.
/// </summary>
public sealed partial class QuerySanitizer
{
    public const string RefusalMessage = "query refused: contains repository-specific terms; search using public API names only";

    private const int MaxInputLength = 2000;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "of", "to", "in", "on", "for", "with", "from", "by", "at", "as", "is", "are", "was", "were",
        "be", "been", "this", "that", "these", "those", "it", "its", "how", "what", "why", "when", "where", "which", "who", "can",
        "could", "should", "would", "do", "does", "did", "i", "we", "you", "my", "our", "your", "me", "not", "no", "yes", "use",
        "using", "used", "get", "gets", "need", "needs", "fix", "fixes", "error", "errors", "issue", "problem", "help", "please", "way", "best", "if", "then", "than", "so", "but", "e.g", "i.e", "etc", "vs",
        ".net", "net", "dotnet", "c#", "f#", "csharp",
    };

    private static readonly HashSet<string> FileExtensions = new(StringComparer.Ordinal)
    {
        "cs", "csproj", "vbproj", "fsproj", "sln", "slnx", "props", "targets", "json", "yml", "yaml", "config", "xml", "txt", "md",
        "ps1", "sh", "bat", "cmd", "ini", "env", "pfx", "pem", "key", "cer", "crt", "sql", "log", "csv", "xaml", "razor", "cshtml",
        "resx", "js", "ts", "py", "java", "dll", "exe", "zip", "nupkg", "snk", "settings", "user", "lock", "toml", "pdb", "html",
        "css", "vb", "fs", "tf", "bicep", "dockerfile", "editorconfig", "ruleset", "nuspec", "binlog", "sarif", "pubxml", "csx",
    };

    private static readonly char[] ForbiddenChars =
        ['@', '/', '\\', '=', ':', '"', '~', '#', '$', '%', '&', '|', '*', '+', '^', '<', '>', '(', ')', '[', ']', '{', '}', ',', ';', '!', '?', '`'];

    private readonly QuerySanitizerOptions _options;
    private readonly string[] _denyTerms;
    private readonly string[] _denyPhrases;

    public QuerySanitizer(QuerySanitizerOptions? options = null)
    {
        _options = options ?? new QuerySanitizerOptions();
        _denyTerms = _options.DenyTerms
            .Select(t => t?.Trim() ?? string.Empty)
            .Where(t => t.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _denyPhrases = _denyTerms.Where(t => t.Any(char.IsWhiteSpace)).OrderByDescending(t => t.Length).ToArray();
    }

    public SanitizedQuery Sanitize(string? query)
    {
        var removed = new List<string>();
        if (string.IsNullOrWhiteSpace(query))
        {
            return new SanitizedQuery(false, string.Empty, removed, RefusalMessage);
        }

        var text = query.Length > MaxInputLength ? query[..MaxInputLength] : query;
        text = NormalizeWhitespace(text);

        foreach (var phrase in _denyPhrases)
        {
            text = ReplaceIgnoreCase(text, NormalizeWhitespace(phrase), " ", removed);
        }

        text = QuotedLiterals().Replace(text, m =>
        {
            removed.Add(m.Value);
            return " ";
        });
        text = text.Replace('`', ' ');

        var kept = new List<string>();
        foreach (var raw in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var decision = Classify(raw);
            if (decision is null)
            {
                removed.Add(raw);
            }
            else if (decision.Length > 0)
            {
                kept.Add(decision);
            }
        }

        var cut = CutToLength(kept, _options.MaxLength);
        var meaningful = cut.Where(IsMeaningful).ToList();
        if (meaningful.Count == 0 || meaningful.Sum(t => t.Count(char.IsLetterOrDigit)) < 2)
        {
            return new SanitizedQuery(false, string.Empty, removed, RefusalMessage);
        }

        return new SanitizedQuery(true, string.Join(' ', cut), removed, null);
    }

    private static string NormalizeWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                space = true;
                continue;
            }

            if (space && sb.Length > 0)
            {
                sb.Append(' ');
            }

            space = false;
            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static string ReplaceIgnoreCase(string text, string term, string replacement, List<string> removed)
    {
        if (term.Length == 0)
        {
            return text;
        }

        var sb = new StringBuilder();
        var index = 0;
        while (true)
        {
            var found = text.IndexOf(term, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                sb.Append(text, index, text.Length - index);
                break;
            }

            sb.Append(text, index, found - index).Append(replacement);
            removed.Add(text.Substring(found, term.Length));
            index = found + term.Length;
        }

        return sb.ToString();
    }

    private static List<string> CutToLength(List<string> tokens, int maxLength)
    {
        var result = new List<string>();
        var length = 0;
        foreach (var token in tokens)
        {
            var next = length + (result.Count > 0 ? 1 : 0) + token.Length;
            if (next > maxLength)
            {
                break;
            }

            result.Add(token);
            length = next;
        }

        return result;
    }

    private static bool IsMeaningful(string token) =>
        !StopWords.Contains(token) && !PureVersion().IsMatch(token) && !FrameworkMoniker().IsMatch(token);

    /// <summary>Returns the cleaned token to keep, "" to silently drop pure punctuation, or null when the token must be removed.</summary>
    private string? Classify(string raw)
    {
        foreach (var deny in _denyTerms)
        {
            if (raw.Contains(deny, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        // An unterminated quote means a string literal started here.
        if (raw[0] is '"' or '\'' or '“')
        {
            return raw.Any(char.IsLetterOrDigit) ? null : string.Empty;
        }

        var token = GenericArguments().Replace(raw, string.Empty);
        if (token.EndsWith("()", StringComparison.Ordinal))
        {
            token = token[..^2];
        }

        token = token.TrimStart('(', '[', '{', '`').TrimEnd(',', ';', ':', '!', '?', ')', ']', '}', '`', '.', '\'');
        if (token.Length == 0)
        {
            return string.Empty;
        }

        if (StopWords.Contains(token))
        {
            return token;
        }

        if (token.Contains("://", StringComparison.Ordinal) || token.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            || token.IndexOfAny(ForbiddenChars) >= 0)
        {
            return null;
        }

        if (Guid().IsMatch(token) || IpAddress().IsMatch(token) || LongNumber().IsMatch(token) || HexBlob().IsMatch(token))
        {
            return null;
        }

        if (token[0] == '.')
        {
            return token.Equals(".NET", StringComparison.OrdinalIgnoreCase) ? token : null;
        }

        var lastDot = token.LastIndexOf('.');
        if (lastDot > 0 && FileExtensions.Contains(token[(lastDot + 1)..]))
        {
            return null;
        }

        if (PureVersion().IsMatch(token) || FrameworkMoniker().IsMatch(token) || DiagnosticCode().IsMatch(token))
        {
            return token;
        }

        if (token.Contains('.'))
        {
            return IsPublicDottedName(token) ? token : null;
        }

        if (Identifier().IsMatch(token))
        {
            if (token.Length > 64 || (token.Length > 20 && token.All(char.IsAsciiLetterLower)) || LooksRandom(token))
            {
                return null;
            }

            return token;
        }

        if (HyphenatedWord().IsMatch(token))
        {
            return token;
        }

        return Contraction().IsMatch(token) ? token : null;
    }

    private static bool IsPublicDottedName(string token)
    {
        var segments = token.Split('.');
        if (segments.Any(s => !Identifier().IsMatch(s)) || segments.Skip(1).Any(s => !char.IsUpper(s[0]) && !char.IsDigit(s[0]) && s[0] != '_'))
        {
            return false;
        }

        return PublicNamespaces.Roots.Contains(segments[0]) || PublicNamespaces.WellKnownTypes.Contains(segments[0]);
    }

    private static bool LooksRandom(string token)
    {
        if (token.Length < 20 || !token.Any(char.IsDigit) || !token.Any(char.IsLetter))
        {
            return false;
        }

        var transitions = 0;
        for (var i = 1; i < token.Length; i++)
        {
            var (a, b) = (token[i - 1], token[i]);
            if ((char.IsLower(a) && char.IsUpper(b)) || (char.IsLetter(a) != char.IsLetter(b)))
            {
                transitions++;
            }
        }

        return transitions / (double)token.Length > 0.3;
    }

    [GeneratedRegex("\"[^\"]*\"|“[^”]*”|(?<![\\p{L}\\p{N}])'[^']*'")]
    private static partial Regex QuotedLiterals();

    [GeneratedRegex("<[^<>\\s]*>")]
    private static partial Regex GenericArguments();

    [GeneratedRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$|^[0-9a-fA-F]{32}$")]
    private static partial Regex Guid();

    [GeneratedRegex(@"^\d{1,3}(\.\d{1,3}){3}$")]
    private static partial Regex IpAddress();

    [GeneratedRegex(@"\d{6,}")]
    private static partial Regex LongNumber();

    [GeneratedRegex("^[0-9a-fA-F]{20,}$")]
    private static partial Regex HexBlob();

    [GeneratedRegex(@"^v?\d{1,5}(\.(\d{1,5}|x)){0,2}(-[A-Za-z0-9.]+)?$|^v?\d{1,5}(\.\d{1,5}){3}-[A-Za-z0-9.]+$")]
    private static partial Regex PureVersion();

    [GeneratedRegex(@"^(net|netstandard|netcoreapp)\d+(\.\d+)*(-[a-z0-9.]+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex FrameworkMoniker();

    [GeneratedRegex(@"^[A-Z]{2,10}\d{3,5}$")]
    private static partial Regex DiagnosticCode();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex Identifier();

    [GeneratedRegex(@"^[A-Za-z]{1,15}(-[A-Za-z]{1,15})+$")]
    private static partial Regex HyphenatedWord();

    [GeneratedRegex(@"^[A-Za-z]+'[a-z]{1,2}$")]
    private static partial Regex Contraction();
}
