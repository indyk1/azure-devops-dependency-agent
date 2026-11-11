using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DependencyAgent.Fixing.OpenAi;

/// <summary>A tool offered by an MCP server.</summary>
public sealed record McpToolInfo(string Name, string Description, string InputSchemaJson);

/// <summary>Seam over an MCP client so the OpenAI tool loop can be tested without starting a server.</summary>
public interface IMcpToolBridge : IAsyncDisposable
{
    Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken);

    /// <summary>Calls a server tool; returns its text output. Tool-reported errors are returned as text prefixed with <c>ERROR:</c>.</summary>
    Task<string> CallToolAsync(string name, string argumentsJson, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IMcpToolBridge"/> over <c>ModelContextProtocol.Core</c> with a stdio server.
/// <para>
/// Environment scrubbing: <see cref="StdioClientTransportOptions.InheritEnvironmentVariables"/> is false, so the server starts with an EMPTY
/// environment plus only <see cref="BuildScrubbedEnvironment"/> (MCP's curated PATH/HOME/USER/... list, a few non-secret .NET variables and
/// telemetry opt-outs). No token, API key, proxy credential or OTEL_* variable can reach the Roslyn server, so no <c>env -i</c> wrapper is needed.
/// </para>
/// </summary>
public sealed class McpStdioToolBridge : IMcpToolBridge
{
    private readonly McpClient _client;

    private McpStdioToolBridge(McpClient client) => _client = client;

    public static async Task<McpStdioToolBridge> StartAsync(string serverName, IReadOnlyList<string> command, string workingDirectory, CancellationToken cancellationToken)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = serverName,
            Command = command[0],
            Arguments = command.Skip(1).ToList(),
            WorkingDirectory = workingDirectory,
            InheritEnvironmentVariables = false,
            EnvironmentVariables = BuildScrubbedEnvironment(),
        });
        var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
        return new McpStdioToolBridge(client);
    }

    /// <summary>The complete environment of the MCP server process (public for tests). Contains no secrets by construction.</summary>
    public static Dictionary<string, string?> BuildScrubbedEnvironment()
    {
        var env = new Dictionary<string, string?>(StdioClientTransportOptions.GetDefaultEnvironmentVariables().ToDictionary(kv => kv.Key, kv => (string?)kv.Value), StringComparer.Ordinal);
        foreach (var name in new[] { "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64" })
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                env[name] = value;
            }
        }

        env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        env["DOTNET_NOLOGO"] = "1";
        env["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        return env;
    }

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken)
    {
        var tools = await _client.ListToolsAsync(cancellationToken: cancellationToken);
        return tools.Select(t => new McpToolInfo(t.Name, t.Description ?? "", t.JsonSchema.GetRawText())).ToList();
    }

    public async Task<string> CallToolAsync(string name, string argumentsJson, CancellationToken cancellationToken)
    {
        var arguments = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    arguments[p.Name] = p.Value.Clone();
                }
            }
        }

        var result = await _client.CallToolAsync(name, arguments, cancellationToken: cancellationToken);
        var sb = new StringBuilder();
        foreach (var block in result.Content)
        {
            if (block is TextContentBlock text)
            {
                sb.AppendLine(text.Text);
            }
        }

        var body = sb.ToString().TrimEnd();
        return result.IsError == true ? "ERROR: " + body : body;
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
