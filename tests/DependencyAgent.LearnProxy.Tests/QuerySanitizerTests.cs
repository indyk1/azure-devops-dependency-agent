using Xunit;

namespace DependencyAgent.LearnProxy.Tests;

public class QuerySanitizerTests
{
    private static QuerySanitizer Create(int max = 200, params string[] deny) =>
        new(new QuerySanitizerOptions { MaxLength = max, DenyTerms = deny });

    public static TheoryData<string, string> LegitQueries => new()
    {
        { "ISystemClock obsolete replacement TimeProvider .NET 8", "ISystemClock obsolete replacement TimeProvider .NET 8" },
        { "SYSLIB0060 Rfc2898DeriveBytes Pbkdf2", "SYSLIB0060 Rfc2898DeriveBytes Pbkdf2" },
        { "ASPDEPR004 WebHostBuilder", "ASPDEPR004 WebHostBuilder" },
        { "CS0618 System.Text.Json.JsonSerializer obsolete", "CS0618 System.Text.Json.JsonSerializer obsolete" },
        { "NU1903 vulnerable package Newtonsoft.Json net10.0", "NU1903 vulnerable package Newtonsoft.Json net10.0" },
        { "MSB3277 conflicts between different versions", "MSB3277 conflicts between different versions" },
        { "IDE0005 CA2000 EF1001", "IDE0005 CA2000 EF1001" },
        { "Microsoft.AspNetCore.Http.HttpContext migrate 8.0", "Microsoft.AspNetCore.Http.HttpContext migrate 8.0" },
        { "IEnumerable<string> Task.Run Console.WriteLine", "IEnumerable Task.Run Console.WriteLine" },
        { "  Serilog.Log   CloseAndFlush \n replacement ", "Serilog.Log CloseAndFlush replacement" },
        { "what is the replacement for `WebHostBuilder`?", "what is the replacement for WebHostBuilder" },
        { "migrate to net10.0 and C# 14 field keyword", "migrate to net10.0 and C# 14 field keyword" },
        { "Polly.Policy.Handle 10.0.0-rc.1.25451.107", "Polly.Policy.Handle 10.0.0-rc.1.25451.107" },
    };

    [Theory]
    [MemberData(nameof(LegitQueries))]
    public void Legit_queries_stay_intact(string input, string expected)
    {
        var result = Create().Sanitize(input);
        Assert.True(result.Allowed, result.Reason);
        Assert.Equal(expected, result.Query);
        Assert.Null(result.Reason);
    }

    [Theory]
    [InlineData("TimeProvider /home/build/agent/_work/1/s/src/Foo/Bar.cs", "TimeProvider")]
    [InlineData("TimeProvider C:\\agent\\work\\Foo.cs", "TimeProvider")]
    [InlineData("TimeProvider src/Foo/Bar.cs", "TimeProvider")]
    [InlineData("TimeProvider ..\\..\\Foo", "TimeProvider")]
    [InlineData("TimeProvider Program.cs", "TimeProvider")]
    [InlineData("TimeProvider My.Product.csproj Directory.Build.props appsettings.json", "TimeProvider")]
    [InlineData("TimeProvider build.yml web.config", "TimeProvider")]
    [InlineData("TimeProvider https://dev.azure.com/contoso/_git/repo", "TimeProvider")]
    [InlineData("TimeProvider www.contoso.com", "TimeProvider")]
    [InlineData("TimeProvider contoso.com api.internal db01.corp host.local", "TimeProvider")]
    [InlineData("TimeProvider jane.doe@contoso.com", "TimeProvider")]
    [InlineData("TimeProvider 10.1.2.3", "TimeProvider")]
    [InlineData("TimeProvider 3fa85f64-5717-4562-b3fc-2c963f66afa6", "TimeProvider")]
    [InlineData("TimeProvider 3fa85f6457174562b3fc2c963f66afa6", "TimeProvider")]
    [InlineData("TimeProvider 1234567 order 99999999", "TimeProvider order")]
    [InlineData("TimeProvider ghp_Xk9fLq2ZpR7vBn4TcY8wHd3JmA5sUe6G0", "TimeProvider")]
    [InlineData("TimeProvider aGVsbG8xMjM0NTY3ODkwYWJjZGVm", "TimeProvider")]
    [InlineData("TimeProvider deadbeefdeadbeefdeadbeef", "TimeProvider")]
    [InlineData("TimeProvider password=hunter2 apikey:abc123", "TimeProvider")]
    [InlineData("Server=db01;Database=Orders;User Id=sa;Password=p4ss TimeProvider", "TimeProvider")]
    [InlineData("TimeProvider \"super secret literal\" 'another one'", "TimeProvider")]
    [InlineData("TimeProvider \"unterminated secret", "TimeProvider secret")]
    [InlineData("TimeProvider Contoso.Orders.OrderService", "TimeProvider")]
    [InlineData("TimeProvider Acme.Billing.Invoice.Create", "TimeProvider")]
    [InlineData("TimeProvider microsoft.com system.local", "TimeProvider")]
    [InlineData("TimeProvider supercalifragilisticexpialidocious", "TimeProvider")]
    [InlineData("TimeProvider $env:TOKEN %PATH% ~/secrets", "TimeProvider")]
    public void Sensitive_tokens_are_removed(string input, string expected)
    {
        var result = Create().Sanitize(input);
        Assert.True(result.Allowed, result.Reason);
        Assert.Equal(expected, result.Query);
        Assert.NotEmpty(result.Removed);
    }

    [Fact]
    public void Unterminated_quote_removes_the_literal_start()
    {
        var result = Create().Sanitize("TimeProvider \"unterminated secret");
        Assert.DoesNotContain("unterminated", result.Query);
    }

    [Theory]
    [InlineData("Contoso.Orders.OrderService obsolete TimeProvider", "obsolete TimeProvider")]
    [InlineData("ContosoClient TimeProvider", "TimeProvider")]
    [InlineData("contoso TimeProvider", "TimeProvider")]
    public void Deny_terms_remove_whole_tokens_case_insensitively(string input, string expected)
    {
        var result = Create(200, "Contoso").Sanitize(input);
        Assert.True(result.Allowed);
        Assert.Equal(expected, result.Query);
    }

    [Fact]
    public void Multi_word_deny_terms_are_removed_as_phrases()
    {
        var result = Create(200, "Fabrikam Bank").Sanitize("Fabrikam Bank ledger TimeProvider fabrikam   bank");
        Assert.Equal("ledger TimeProvider", result.Query);
    }

    [Fact]
    public void Deny_term_inside_public_namespace_token_is_removed()
    {
        var result = Create(200, "Orders").Sanitize("System.Orders.Thing TimeProvider");
        Assert.Equal("TimeProvider", result.Query);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("how to fix the")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\secret\\file.cs")]
    [InlineData("Contoso.Orders.OrderService")]
    [InlineData("a")]
    [InlineData("password=abc")]
    [InlineData("what is .NET 10")]
    [InlineData("\"only a literal\"")]
    public void Queries_without_meaningful_content_are_refused(string input)
    {
        var result = Create().Sanitize(input);
        Assert.False(result.Allowed);
        Assert.Equal(string.Empty, result.Query);
        Assert.Contains("repository-specific", result.Reason);
    }

    [Fact]
    public void Null_is_refused()
    {
        Assert.False(Create().Sanitize(null).Allowed);
    }

    [Fact]
    public void Long_queries_are_cut_at_word_boundary()
    {
        var result = Create(30).Sanitize("TimeProvider obsolete replacement WebHostBuilder Rfc2898DeriveBytes");
        Assert.True(result.Allowed);
        Assert.Equal("TimeProvider obsolete", result.Query);
        Assert.True(result.Query.Length <= 30);
    }

    [Fact]
    public void Removed_tokens_are_reported_for_callers_but_not_in_query()
    {
        var result = Create().Sanitize("TimeProvider /srv/app/Secret.cs jane@corp.example");
        Assert.Equal("TimeProvider", result.Query);
        Assert.Contains("/srv/app/Secret.cs", result.Removed);
        Assert.Contains("jane@corp.example", result.Removed);
    }

    [Fact]
    public void Sanitizing_is_deterministic_and_idempotent()
    {
        var input = "ISystemClock Contoso.Foo /a/b.cs 10.0.0.1 \"x\" TimeProvider";
        var sanitizer = Create();
        var first = sanitizer.Sanitize(input);
        Assert.Equal(first.Query, sanitizer.Sanitize(input).Query);
        Assert.Equal(first.Query, sanitizer.Sanitize(first.Query).Query);
    }

    [Fact]
    public void Output_never_contains_path_separators_or_urls()
    {
        var nasty = "a/b c\\d http://x.y/z user@host.com ../../up C:\\x \\\\server\\share file:///etc TimeProvider";
        var result = Create().Sanitize(nasty);
        Assert.DoesNotContain('/', result.Query);
        Assert.DoesNotContain('\\', result.Query);
        Assert.DoesNotContain('@', result.Query);
        Assert.DoesNotContain(':', result.Query);
        Assert.Equal("TimeProvider", result.Query);
    }
}
