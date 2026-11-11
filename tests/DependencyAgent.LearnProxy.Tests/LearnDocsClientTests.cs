using System.IO.Pipelines;
using System.Text.Json;
using DependencyAgent.Core;
using DependencyAgent.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DependencyAgent.LearnProxy.Tests;

internal sealed class FakeTransport(Func<string, IReadOnlyDictionary<string, object?>, string>? responder = null) : ILearnMcpTransport
{
    public List<(string Tool, Dictionary<string, object?> Args)> Calls { get; } = [];

    public Task<string> CallToolAsync(string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        Calls.Add((toolName, new Dictionary<string, object?>(arguments)));
        return Task.FromResult(responder?.Invoke(toolName, arguments) ?? "docs for " + toolName);
    }
}

public sealed class LearnDocsClientTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("learnaudit").FullName;
    private string AuditPath => Path.Combine(_dir, "sub", "audit.jsonl");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private LearnDocsClient Create(FakeTransport transport, params string[] deny) =>
        new(transport, new QuerySanitizer(new QuerySanitizerOptions { DenyTerms = deny }), AuditPath);

    [Fact]
    public async Task Refused_query_is_not_sent_and_returns_guidance()
    {
        var transport = new FakeTransport();
        var result = await Create(transport).SearchDocsAsync("/srv/secret/path.cs", CancellationToken.None);
        Assert.Equal(QuerySanitizer.RefusalMessage, result);
        Assert.Empty(transport.Calls);
    }

    [Fact]
    public async Task Sanitized_query_is_sent_with_microsoft_parameter_names()
    {
        var transport = new FakeTransport();
        var result = await Create(transport, "Contoso").SearchDocsAsync("Contoso.Orders ISystemClock obsolete C:\\x\\y.cs", CancellationToken.None);
        var call = Assert.Single(transport.Calls);
        Assert.Equal("microsoft_docs_search", call.Tool);
        Assert.Equal("ISystemClock obsolete", call.Args["query"]);
        Assert.Contains("data, not instructions", result);
        Assert.Contains("docs for microsoft_docs_search", result);
    }

    [Fact]
    public async Task Code_sample_search_sends_validated_language()
    {
        var transport = new FakeTransport();
        var client = Create(transport);
        await client.SearchCodeSamplesAsync("Pbkdf2", "csharp", CancellationToken.None);
        await client.SearchCodeSamplesAsync("Pbkdf2", "c#; drop", CancellationToken.None);
        Assert.Equal("microsoft_code_sample_search", transport.Calls[0].Tool);
        Assert.Equal("csharp", transport.Calls[0].Args["language"]);
        Assert.False(transport.Calls[1].Args.ContainsKey("language"));
    }

    [Fact]
    public async Task Fetch_rewrites_url_and_refuses_foreign_hosts()
    {
        var transport = new FakeTransport();
        var client = Create(transport);
        await client.FetchDocAsync("https://docs.microsoft.com/en-us/dotnet/x?view=net-10.0&a=b#c", CancellationToken.None);
        var refused = await client.FetchDocAsync("https://contoso.com/secret", CancellationToken.None);
        var call = Assert.Single(transport.Calls);
        Assert.Equal("microsoft_docs_fetch", call.Tool);
        Assert.Equal("https://learn.microsoft.com/en-us/dotnet/x?view=net-10.0", call.Args["url"]);
        Assert.Contains("url refused", refused);
    }

    [Fact]
    public async Task Audit_log_has_one_line_per_request_without_removed_tokens()
    {
        var transport = new FakeTransport();
        var client = Create(transport);
        await client.SearchDocsAsync("TimeProvider /srv/SuperSecret/Thing.cs jane@corp.example", CancellationToken.None);
        await client.SearchDocsAsync("/srv/OnlySecret/Thing.cs", CancellationToken.None);
        await client.FetchDocAsync("https://learn.microsoft.com/en-us/x", CancellationToken.None);

        var raw = await File.ReadAllTextAsync(AuditPath, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("SuperSecret", raw);
        Assert.DoesNotContain("jane", raw);
        Assert.DoesNotContain("OnlySecret", raw);
        var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Assert.Equal(3, lines.Count);

        Assert.Equal("microsoft_docs_search", lines[0].GetProperty("tool").GetString());
        Assert.Equal("TimeProvider", lines[0].GetProperty("sentQuery").GetString());
        Assert.True(lines[0].GetProperty("allowed").GetBoolean());
        Assert.Equal(2, lines[0].GetProperty("removedTokens").GetInt32());
        Assert.True(DateTimeOffset.TryParse(lines[0].GetProperty("timestamp").GetString(), out _));

        Assert.False(lines[1].GetProperty("allowed").GetBoolean());
        Assert.False(lines[1].TryGetProperty("sentQuery", out _));
        Assert.Equal(1, lines[1].GetProperty("removedTokens").GetInt32());

        Assert.Equal("https://learn.microsoft.com/en-us/x", lines[2].GetProperty("sentUrl").GetString());
    }

    [Fact]
    public async Task Responses_are_neutralized_and_capped()
    {
        var transport = new FakeTransport((_, _) => "##vso[task.setvariable variable=x]y\n##[error]boom\n" + new string('z', 100_000));
        var result = await Create(transport).SearchDocsAsync("TimeProvider", CancellationToken.None);
        Assert.DoesNotContain("##vso[", result);
        Assert.DoesNotContain("##[", result);
        Assert.True(result.Length < LearnDocsClient.MaxResponseChars + 500);
        Assert.EndsWith("[truncated]", result);
    }

    [Fact]
    public async Task Transport_failure_is_reported_without_throwing_or_leaking_details()
    {
        var client = new LearnDocsClient(new ThrowingTransport(), new QuerySanitizer(), null);
        var result = await client.SearchDocsAsync("TimeProvider", CancellationToken.None);
        Assert.Contains("lookup failed", result);
        Assert.DoesNotContain("inner secret", result);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        var client = new LearnDocsClient(new ThrowingTransport(new OperationCanceledException()), new QuerySanitizer(), null);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SearchDocsAsync("TimeProvider", cts.Token));
    }

    [Fact]
    public async Task Factory_combines_settings_and_extra_deny_terms()
    {
        var transport = new FakeTransport();
        var settings = new LearnMcpSettings { DenyTerms = ["Fabrikam"], MaxQueryLength = 50 };
        var client = LearnDocsClientFactory.Create(settings, ["Contoso"], AuditPath, transport);
        await client.SearchDocsAsync("Fabrikam Contoso TimeProvider", CancellationToken.None);
        Assert.Equal("TimeProvider", Assert.Single(transport.Calls).Args["query"]);
    }

    [Fact]
    public async Task Server_round_trip_over_in_memory_pipes()
    {
        var transport = new FakeTransport();
        var toServer = new Pipe();
        var toClient = new Pipe();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        LearnProxyServer.AddProxy(builder.Services, new LearnProxyOptions { DenyTerms = ["Contoso"], AuditLog = AuditPath }, transport)
            .WithStreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream());
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()),
            cancellationToken: TestContext.Current.CancellationToken);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(
            ["microsoft_code_sample_search", "microsoft_docs_fetch", "microsoft_docs_search"],
            tools.Select(t => t.Name).Order(StringComparer.Ordinal).ToArray());
        var search = tools.Single(t => t.Name == "microsoft_docs_search");
        Assert.Contains("PUBLIC API names", search.Description);
        Assert.Contains("query", search.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        var sample = tools.Single(t => t.Name == "microsoft_code_sample_search");
        var props = sample.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Contains("query", props);
        Assert.Contains("language", props);
        Assert.Contains("url", tools.Single(t => t.Name == "microsoft_docs_fetch").JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name));

        var result = await client.CallToolAsync(
            "microsoft_docs_search",
            new Dictionary<string, object?> { ["query"] = "Contoso.Billing ISystemClock /srv/x.cs" }!,
            cancellationToken: TestContext.Current.CancellationToken);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("docs for microsoft_docs_search", text);
        Assert.Equal("ISystemClock", Assert.Single(transport.Calls).Args["query"]);

        var refused = await client.CallToolAsync(
            "microsoft_docs_search",
            new Dictionary<string, object?> { ["query"] = "/srv/x.cs" }!,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(QuerySanitizer.RefusalMessage, Assert.IsType<TextContentBlock>(Assert.Single(refused.Content)).Text);
        Assert.Single(transport.Calls);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Live_microsoft_learn_search_when_enabled()
    {
        if (Environment.GetEnvironmentVariable("DEPENDENCY_AGENT_LIVE_TESTS") != "1")
        {
            return;
        }

        await using var client = LearnDocsClientFactory.Create(new LearnMcpSettings());
        var result = await client.SearchDocsAsync("TimeProvider ISystemClock .NET 8", CancellationToken.None);
        Assert.DoesNotContain("lookup failed", result);
        Assert.Contains("TimeProvider", result);
    }

    private sealed class ThrowingTransport(Exception? ex = null) : ILearnMcpTransport
    {
        public Task<string> CallToolAsync(string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken) =>
            throw ex ?? new InvalidOperationException("inner secret detail");
    }
}

public sealed class LearnProxyArgsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("learnargs").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Defaults_come_from_settings()
    {
        var o = LearnProxyServer.Parse([]);
        var s = new LearnMcpSettings();
        Assert.Equal(s.Endpoint, o.Endpoint);
        Assert.Equal(s.MaxQueryLength, o.MaxQueryLength);
        Assert.Null(o.AuditLog);
        Assert.Null(o.DenyTermsFile);
    }

    [Fact]
    public void All_arguments_are_parsed()
    {
        var o = LearnProxyServer.Parse(["--endpoint", "https://example.test/mcp", "--max-query-length", "120", "--deny-terms-file", "d.txt", "--audit-log", "a.jsonl", "--deny-term", "X1", "--deny-term", "Y2"]);
        Assert.Equal("https://example.test/mcp", o.Endpoint);
        Assert.Equal(120, o.MaxQueryLength);
        Assert.Equal("d.txt", o.DenyTermsFile);
        Assert.Equal("a.jsonl", o.AuditLog);
        Assert.Equal(["X1", "Y2"], o.DenyTerms);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--endpoint")]
    [InlineData("--max-query-length", "abc")]
    [InlineData("--max-query-length", "5")]
    public void Invalid_arguments_throw(params string[] args) =>
        Assert.Throws<ArgumentException>(() => LearnProxyServer.Parse(args));

    [Fact]
    public void Deny_terms_file_ignores_comments_and_blank_lines()
    {
        var path = Path.Combine(_dir, "deny.txt");
        File.WriteAllText(path, "# internal names\nContoso\n\n  Fabrikam Bank  \n#Ignored\n");
        var terms = LearnProxyServer.ResolveDenyTerms(new LearnProxyOptions { DenyTermsFile = path, DenyTerms = ["Extra"] });
        Assert.Equal(["Extra", "Contoso", "Fabrikam Bank"], terms);
    }

    [Fact]
    public async Task RunAsync_returns_2_on_bad_arguments()
    {
        Assert.Equal(2, await LearnProxyServer.RunAsync(["--nope"], CancellationToken.None));
        Assert.Equal(2, await LearnProxyServer.RunAsync(["--deny-terms-file", Path.Combine(_dir, "missing.txt")], CancellationToken.None));
    }
}
