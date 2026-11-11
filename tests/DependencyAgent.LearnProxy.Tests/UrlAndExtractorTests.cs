using Xunit;

namespace DependencyAgent.LearnProxy.Tests;

public class DocsUrlPolicyTests
{
    [Theory]
    [InlineData("https://learn.microsoft.com/en-us/dotnet/core/compatibility/10.0", "https://learn.microsoft.com/en-us/dotnet/core/compatibility/10.0")]
    [InlineData("https://LEARN.microsoft.com/en-us/dotnet/api/system.timeprovider", "https://learn.microsoft.com/en-us/dotnet/api/system.timeprovider")]
    [InlineData("https://docs.microsoft.com/en-us/dotnet/api/system.timeprovider", "https://learn.microsoft.com/en-us/dotnet/api/system.timeprovider")]
    [InlineData("https://learn.microsoft.com/en-us/dotnet/api/x?view=net-10.0&secret=abc#section", "https://learn.microsoft.com/en-us/dotnet/api/x?view=net-10.0")]
    [InlineData("https://learn.microsoft.com/en-us/dotnet/api/x?token=abc&foo=bar#frag", "https://learn.microsoft.com/en-us/dotnet/api/x")]
    [InlineData("https://learn.microsoft.com:443/en-us/x", "https://learn.microsoft.com/en-us/x")]
    [InlineData("https://learn.microsoft.com/en-us/x?view=a%20b", "https://learn.microsoft.com/en-us/x")]
    public void Allowed_urls_are_normalized(string input, string expected)
    {
        var d = DocsUrlPolicy.Evaluate(input);
        Assert.True(d.Allowed, d.Reason);
        Assert.Equal(expected, d.Url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("learn.microsoft.com/en-us/x")]
    [InlineData("http://learn.microsoft.com/en-us/x")]
    [InlineData("ftp://learn.microsoft.com/x")]
    [InlineData("https://user:pw@learn.microsoft.com/x")]
    [InlineData("https://user@learn.microsoft.com/x")]
    [InlineData("https://learn.microsoft.com:8443/x")]
    [InlineData("https://127.0.0.1/x")]
    [InlineData("https://[::1]/x")]
    [InlineData("https://evil.com/learn.microsoft.com")]
    [InlineData("https://learn.microsoft.com.evil.com/x")]
    [InlineData("https://notlearn.microsoft.com/x")]
    [InlineData("https://contoso.com/docs")]
    [InlineData("https://learn.microsoft.com/x y")]
    public void Disallowed_urls_are_refused(string input)
    {
        var d = DocsUrlPolicy.Evaluate(input);
        Assert.False(d.Allowed);
        Assert.Null(d.Url);
        Assert.StartsWith("url refused", d.Reason);
    }

    [Fact]
    public void Very_long_urls_are_refused()
    {
        var d = DocsUrlPolicy.Evaluate("https://learn.microsoft.com/" + new string('a', 500));
        Assert.False(d.Allowed);
    }
}

public sealed class RepoTermExtractorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("repoterms").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string rel, string content)
    {
        var path = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Extracts_terms_from_projects_props_and_namespaces()
    {
        Write("src/Contoso.Orders.Api/Contoso.Orders.Api.csproj",
            "<Project><PropertyGroup><RootNamespace>Fabrikam.Orders</RootNamespace><AssemblyName>Fabrikam.Orders.Host</AssemblyName></PropertyGroup></Project>");
        Write("src/Microsoft.Extensions.Thing/Microsoft.Extensions.Thing.csproj", "<Project />");
        Write("Directory.Build.props",
            "<Project><PropertyGroup><Company>Northwind Traders Ltd</Company><Product>Zephyr Suite</Product><Authors>Jane Doe;Acme Team</Authors><Version>$(Foo)</Version></PropertyGroup></Project>");
        Write("src/Contoso.Orders.Api/Program.cs", "namespace Initech.Billing.Core;\nclass A {}");
        Write("src/Contoso.Orders.Api/Other.cs", "namespace System.Text.Extra { }");
        Write("src/Contoso.Orders.Api/obj/Gen.cs", "namespace Hidden.Generated { }");
        Write("src/Contoso.Orders.Api/Tests.cs", "  namespace Globex\n{\n}");

        var terms = RepoTermExtractor.Extract(_root, "my-repo", "OrgName", "ab", "Api");

        foreach (var expected in new[]
        {
            "Contoso", "Contoso.Orders.Api", "Fabrikam", "Fabrikam.Orders", "Fabrikam.Orders.Host", "Northwind", "Traders", "Zephyr", "Suite",
            "Jane", "Doe", "Acme", "Team", "Initech", "Globex", "my-repo", "OrgName", "Microsoft.Extensions.Thing",
        })
        {
            Assert.Contains(expected, terms, StringComparer.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("Hidden", terms);
        Assert.DoesNotContain("Microsoft", terms);
        Assert.DoesNotContain("System", terms);
        Assert.DoesNotContain("Api", terms, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ltd", terms, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("ab", terms);
        Assert.DoesNotContain(terms, t => t.Contains("$("));
    }

    [Fact]
    public void Result_is_sorted_and_deduplicated()
    {
        Write("A/Contoso.csproj", "<Project />");
        Write("B/Program.cs", "namespace Contoso;");
        var terms = RepoTermExtractor.Extract(_root, "contoso");
        Assert.Single(terms, t => t.Equals("contoso", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Missing_repo_returns_only_extras()
    {
        var terms = RepoTermExtractor.Extract(Path.Combine(_root, "nope"), "Initech", "Core");
        Assert.Equal(["Initech"], terms);
    }

    [Fact]
    public void Extracted_terms_feed_the_sanitizer()
    {
        Write("Contoso.Orders.csproj", "<Project />");
        var sanitizer = new QuerySanitizer(new QuerySanitizerOptions { DenyTerms = RepoTermExtractor.Extract(_root) });
        var result = sanitizer.Sanitize("ContosoHelper TimeProvider");
        Assert.Equal("TimeProvider", result.Query);
    }
}
