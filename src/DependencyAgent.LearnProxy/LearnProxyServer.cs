using DependencyAgent.Core;
using System.ComponentModel;
using DependencyAgent.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DependencyAgent.LearnProxy;

public sealed record LearnProxyOptions
{
    public string Endpoint { get; init; } = new LearnMcpSettings().Endpoint;

    public int MaxQueryLength { get; init; } = new LearnMcpSettings().MaxQueryLength;

    public string? DenyTermsFile { get; init; }

    public IReadOnlyList<string> DenyTerms { get; init; } = [];

    public string? AuditLog { get; init; }
}

/// <summary>MCP tools mirroring the Microsoft Learn server's names and parameters, backed by the sanitizing client.</summary>
[McpServerToolType]
public sealed class LearnProxyTools(ILearnDocsClient docs)
{
    [McpServerTool(Name = "microsoft_docs_search", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Search official Microsoft Learn documentation. Search with PUBLIC API names, type names, diagnostic codes (e.g. SYSLIB0060, CS0618) and .NET versions only. "
        + "Queries are sanitized; file paths, URLs, secrets, internal namespaces and repository-specific names are removed or the query is refused.")]
    public Task<string> SearchDocs(
        [Description("Short search query made of public API names, error codes and version numbers, e.g. 'ISystemClock obsolete TimeProvider .NET 8'.")] string query,
        CancellationToken cancellationToken) =>
        docs.SearchDocsAsync(query, cancellationToken);

    [McpServerTool(Name = "microsoft_docs_fetch", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Fetch a Microsoft Learn documentation page as markdown. Only https://learn.microsoft.com/... URLs are accepted; other URLs are refused.")]
    public Task<string> FetchDoc(
        [Description("A https://learn.microsoft.com/... URL returned by microsoft_docs_search.")] string url,
        CancellationToken cancellationToken) =>
        docs.FetchDocAsync(url, cancellationToken);

    [McpServerTool(Name = "microsoft_code_sample_search", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Search official Microsoft code samples. Use PUBLIC API names only (no repository-specific names, paths or secrets); queries are sanitized.")]
    public Task<string> SearchCodeSamples(
        [Description("Short query made of public API names, e.g. 'Rfc2898DeriveBytes Pbkdf2'.")] string query,
        [Description("Optional programming language, e.g. 'csharp'.")] string? language,
        CancellationToken cancellationToken) =>
        docs.SearchCodeSamplesAsync(query, language, cancellationToken);
}

/// <summary>Local stdio MCP server that proxies Microsoft Learn after sanitizing every outbound request.</summary>
public static class LearnProxyServer
{
    public const string Usage = "learn-proxy [--endpoint <url>] [--max-query-length <n>] [--deny-terms-file <path>] [--deny-term <term>]... [--audit-log <path>]";

    public static LearnProxyOptions Parse(string[] args)
    {
        var options = new LearnProxyOptions();
        var denyTerms = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string Value()
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"Missing value for {arg}. Usage: {Usage}");
                }

                return args[++i];
            }

            switch (arg)
            {
                case "--endpoint":
                    options = options with { Endpoint = Value() };
                    break;
                case "--max-query-length":
                    if (!int.TryParse(Value(), out var max) || max < 20 || max > 2000)
                    {
                        throw new ArgumentException("--max-query-length must be an integer between 20 and 2000.");
                    }

                    options = options with { MaxQueryLength = max };
                    break;
                case "--deny-terms-file":
                    options = options with { DenyTermsFile = Value() };
                    break;
                case "--deny-term":
                    denyTerms.Add(Value());
                    break;
                case "--audit-log":
                    options = options with { AuditLog = Value() };
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{arg}'. Usage: {Usage}");
            }
        }

        return options with { DenyTerms = denyTerms };
    }

    /// <summary>Reads a deny-terms file: one term per line, blank lines and <c>#</c> comments ignored.</summary>
    public static IReadOnlyList<string> LoadDenyTerms(string path) =>
        File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();

    public static IReadOnlyList<string> ResolveDenyTerms(LearnProxyOptions options) =>
        options.DenyTerms.Concat(string.IsNullOrWhiteSpace(options.DenyTermsFile) ? [] : LoadDenyTerms(options.DenyTermsFile)).ToList();

    /// <summary>Registers the docs client and tool class; the caller adds the MCP transport (stdio or in-memory).</summary>
    public static IMcpServerBuilder AddProxy(IServiceCollection services, LearnProxyOptions options, ILearnMcpTransport? transport = null)
    {
        var denyTerms = ResolveDenyTerms(options);
        services.AddSingleton<ILearnDocsClient>(_ =>
            new LearnDocsClient(
                transport ?? new LearnMcpRemoteClient(options.Endpoint),
                new QuerySanitizer(new QuerySanitizerOptions { MaxLength = options.MaxQueryLength, DenyTerms = denyTerms }),
                options.AuditLog));
        return services.AddMcpServer().WithTools<LearnProxyTools>();
    }

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        LearnProxyOptions options;
        try
        {
            options = Parse(args);
            _ = ResolveDenyTerms(options);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync("learn-proxy: " + ex.Message).ConfigureAwait(false);
            return 2;
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        AddProxy(builder.Services, options).WithStdioServerTransport();
        using var host = builder.Build();
        await host.RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }
}

/// <summary>Creates an in-process sanitized Learn client (used when the AI provider calls tools directly, e.g. OpenAI).</summary>
public static class LearnDocsClientFactory
{
    public static LearnDocsClient Create(LearnMcpSettings settings, IEnumerable<string>? denyTerms = null, string? auditLogPath = null, ILearnMcpTransport? transport = null)
    {
        var terms = settings.DenyTerms.Concat(denyTerms ?? []).ToList();
        return new LearnDocsClient(
            transport ?? new LearnMcpRemoteClient(settings.Endpoint),
            new QuerySanitizer(new QuerySanitizerOptions { MaxLength = settings.MaxQueryLength, DenyTerms = terms }),
            auditLogPath);
    }
}
