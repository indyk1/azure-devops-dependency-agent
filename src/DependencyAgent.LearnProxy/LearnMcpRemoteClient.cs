using System.Text;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DependencyAgent.LearnProxy;

/// <summary>Abstraction over the remote Microsoft Learn MCP server so tests can fake it.</summary>
public interface ILearnMcpTransport
{
    /// <summary>Calls a tool on the remote server and returns its concatenated text content.</summary>
    Task<string> CallToolAsync(string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken);
}

/// <summary>Streamable-HTTP MCP client for the hosted Learn server. No authentication, generic User-Agent, lazy connect.</summary>
public sealed class LearnMcpRemoteClient : ILearnMcpTransport, IAsyncDisposable
{
    public const string UserAgent = "dependency-agent";
    public const int MaxResponseChars = 30_000;

    private readonly Uri _endpoint;
    private readonly TimeSpan _callTimeout;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private HttpClient? _http;
    private McpClient? _client;

    public LearnMcpRemoteClient(string endpoint, TimeSpan? callTimeout = null)
    {
        _endpoint = new Uri(endpoint, UriKind.Absolute);
        _callTimeout = callTimeout ?? TimeSpan.FromSeconds(30);
    }

    public async Task<string> CallToolAsync(string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_callTimeout);
        try
        {
            var client = await GetClientAsync(cts.Token).ConfigureAwait(false);
            var result = await client.CallToolAsync(toolName, arguments!, cancellationToken: cts.Token).ConfigureAwait(false);
            var text = ExtractText(result);
            if (result.IsError == true)
            {
                throw new InvalidOperationException("Learn MCP server returned an error: " + Truncate(text, 500));
            }

            return Truncate(text, MaxResponseChars);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Learn MCP call '{toolName}' timed out after {_callTimeout.TotalSeconds:0}s.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }

        _http?.Dispose();
        _connectLock.Dispose();
    }

    private static string ExtractText(CallToolResult result)
    {
        var sb = new StringBuilder();
        foreach (var block in result.Content)
        {
            if (block is TextContentBlock text)
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }

                sb.Append(text.Text);
            }
        }

        return sb.ToString();
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "\n[truncated]";

    private async Task<McpClient> GetClientAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            return _client;
        }

        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is null)
            {
                _http = new HttpClient();
                _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
                var transport = new HttpClientTransport(
                    new HttpClientTransportOptions
                    {
                        Endpoint = _endpoint,
                        TransportMode = HttpTransportMode.StreamableHttp,
                        Name = "microsoft-learn",
                    },
                    _http,
                    loggerFactory: null,
                    ownsHttpClient: false);
                _client = await McpClient.CreateAsync(
                    transport,
                    new McpClientOptions { ClientInfo = new Implementation { Name = UserAgent, Version = "1.0.0" } },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            return _client;
        }
        finally
        {
            _connectLock.Release();
        }
    }
}
