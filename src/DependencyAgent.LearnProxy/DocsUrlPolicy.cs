using System.Text.RegularExpressions;

namespace DependencyAgent.LearnProxy;

public sealed record UrlDecision(bool Allowed, string? Url, string? Reason);

/// <summary>Only public Microsoft Learn pages may be fetched; the URL is normalised so it carries no extra data.</summary>
public static partial class DocsUrlPolicy
{
    public const int MaxUrlLength = 500;

    public static UrlDecision Evaluate(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return Refuse("empty url");
        }

        url = url.Trim();
        if (url.Length > MaxUrlLength)
        {
            return Refuse("url too long");
        }

        if (url.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return Refuse("url contains whitespace or control characters");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return Refuse("only absolute https URLs are allowed");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return Refuse("url must not contain credentials");
        }

        if (uri.Port != 443)
        {
            return Refuse("non-standard port");
        }

        if (uri.HostNameType != UriHostNameType.Dns)
        {
            return Refuse("host must be a DNS name");
        }

        var host = uri.IdnHost.ToLowerInvariant();
        if (host is not ("learn.microsoft.com" or "docs.microsoft.com"))
        {
            return Refuse("only learn.microsoft.com URLs are allowed");
        }

        var path = uri.AbsolutePath;
        if (path.Length > 400)
        {
            return Refuse("url path too long");
        }

        var result = "https://learn.microsoft.com" + path;
        var view = ExtractView(uri.Query);
        if (view is not null)
        {
            result += "?view=" + view;
        }

        return new UrlDecision(true, result, null);
    }

    private static string? ExtractView(string query)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq].Equals("view", StringComparison.OrdinalIgnoreCase))
            {
                var value = pair[(eq + 1)..];
                return ViewValue().IsMatch(value) ? value : null;
            }
        }

        return null;
    }

    private static UrlDecision Refuse(string reason) => new(false, null, "url refused: " + reason);

    [GeneratedRegex(@"^[A-Za-z0-9._\-]{1,60}$")]
    private static partial Regex ViewValue();
}
