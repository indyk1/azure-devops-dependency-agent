using System.Text.Json;
using System.Text.Json.Nodes;
using DependencyAgent.Core;
using DependencyAgent.Core.Configuration;
using DependencyAgent.Core.Model;
using DependencyAgent.Core.Processes;

namespace DependencyAgent.Fixing;

/// <summary>
/// Runs the GitHub Copilot CLI non-interactively (<c>copilot -p</c>) with a locked-down tool set.
/// <para>
/// Flags verified against <c>@github/copilot</c> <see cref="VerifiedVersion"/> (<c>copilot --help</c>, <c>copilot help permissions|environment|mcp</c>,
/// and by capturing the model request of a real CLI run against a local fake BYOK endpoint to list the tool names).
/// </para>
/// <para>
/// Security design: the model can only see the file tools <c>view</c>, <c>create</c>, <c>edit</c>, <c>grep</c>, <c>glob</c> plus the tools of the
/// <c>learn</c> MCP server (<c>--available-tools</c>); shell, url/web fetch, built-in MCP servers, memory, custom instructions, ask-user and
/// auto-update are all off. Shell and url permissions are additionally denied (<c>--deny-tool</c>, deny wins over every allow).
/// In <c>-p</c> mode anything not explicitly allowed is denied, and the working folder is not trusted (no <c>--trust-folder</c>), so the
/// repository's own <c>.mcp.json</c>, hooks, skills and plugins are never loaded. Config/state/logs live in a throwaway directory outside the repo.
/// </para>
/// </summary>
public sealed class CopilotCliProvider : IAiProvider
{
    /// <summary>npm package that provides the <c>copilot</c> executable.</summary>
    public const string NpmPackage = "@github/copilot";

    /// <summary>Copilot CLI version this invocation was verified against; pin the pipeline's <c>npm install</c> to it.</summary>
    public const string VerifiedVersion = "1.0.109";

    /// <summary>Name of the MCP server entry that points at the sanitizing Learn proxy.</summary>
    public const string LearnServerName = "learn";

    /// <summary>The environment variable Copilot CLI reads first for authentication.</summary>
    public const string CopilotTokenEnvironmentVariable = "COPILOT_GITHUB_TOKEN";

    public const int MaxPromptChars = 100_000;
    private const int SummaryChars = 3000;

    /// <summary>Tools the model may see (besides the learn MCP server).</summary>
    public static readonly IReadOnlyList<string> AvailableFileTools = ["view", "create", "edit", "grep", "glob"];

    // Variables that could redirect, widen or re-authenticate the CLI; removed from the child environment.
    private static readonly string[] BlockedVariables =
    [
        "GH_TOKEN", "GITHUB_TOKEN", "OPENAI_API_KEY", "AZURE_OPENAI_API_KEY",
        "COPILOT_ALLOW_ALL", "COPILOT_OFFLINE", "COPILOT_CUSTOM_INSTRUCTIONS_DIRS", "COPILOT_SKILLS_DIRS",
        "COPILOT_PROVIDER_BASE_URL", "COPILOT_PROVIDER_TYPE", "COPILOT_PROVIDER_API_KEY", "COPILOT_PROVIDER_API_KEY_COMMAND",
        "COPILOT_PROVIDER_BEARER_TOKEN", "COPILOT_PROVIDER_WIRE_API", "COPILOT_PROVIDER_HEADERS", "COPILOT_PROVIDERS_CONFIG",
        "COPILOT_ASSISTED_APPROVAL", "COPILOT_BASH_PATH", "BASH_ENV",
    ];

    private static readonly string[] OtelVariables =
    [
        "OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT",
        "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT", "OTEL_EXPORTER_OTLP_HEADERS", "OTEL_EXPORTER_OTLP_PROTOCOL",
        "OTEL_TRACES_EXPORTER", "OTEL_METRICS_EXPORTER", "OTEL_LOGS_EXPORTER", "OTEL_SDK_DISABLED",
    ];

    private readonly IProcessRunner _runner;

    public CopilotCliProvider(IProcessRunner runner) => _runner = runner;

    public string Name => "copilot";

    public string? CheckAvailability(AgentSettings settings)
    {
        var tokenVariable = settings.Ai.Copilot.TokenVariable;
        if (SecretEnvironment.GetSecret(tokenVariable) is null)
        {
            return $"environment variable '{tokenVariable}' (GitHub token of a Copilot-licensed user) is not set";
        }

        var exe = settings.Ai.Copilot.Executable;
        if (ExecutableLocator.Find(exe) is null)
        {
            return $"Copilot CLI executable '{exe}' was not found (install with: npm install -g {NpmPackage}@{VerifiedVersion})";
        }

        return null;
    }

    public async Task<AiRunResult> RunAsync(AiRunRequest request, AgentSettings settings, CancellationToken cancellationToken)
    {
        var copilot = settings.Ai.Copilot;
        var token = SecretEnvironment.GetSecret(copilot.TokenVariable);
        if (token is null)
        {
            return new AiRunResult(false, "", $"environment variable '{copilot.TokenVariable}' is not set");
        }

        var configDir = Path.Combine(Path.GetTempPath(), "depagent-copilot-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(configDir);
            Directory.CreateDirectory(Path.Combine(configDir, "tmp"));
            var spec = BuildProcessSpec(request, settings, token, configDir);
            var result = await _runner.RunAsync(spec, cancellationToken);

            var stdout = Mask(result.StandardOutput, token).Trim();
            var summary = PipelineCommandNeutralizer.Neutralize(GitCli.Tail(stdout, SummaryChars));
            if (result.TimedOut)
            {
                return new AiRunResult(false, summary, $"Copilot CLI timed out after {request.Timeout.TotalMinutes:0.#} minutes");
            }

            if (result.ExitCode != 0)
            {
                var err = PipelineCommandNeutralizer.Neutralize(GitCli.Tail(Mask(result.StandardError, token).Trim(), 800));
                return new AiRunResult(false, summary, $"Copilot CLI exited with code {result.ExitCode}: {err}");
            }

            return new AiRunResult(true, summary, null);
        }
        finally
        {
            try
            {
                Directory.Delete(configDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // best effort
            }
        }
    }

    /// <summary>Builds the process specification (public for tests). <paramref name="configDir"/> must exist outside the repository.</summary>
    public static ProcessSpec BuildProcessSpec(AiRunRequest request, AgentSettings settings, string token, string configDir)
    {
        var copilot = settings.Ai.Copilot;
        var prompt = request.SystemPrompt + "\n\n---\n\n" + request.TaskPrompt;
        if (prompt.Length > MaxPromptChars)
        {
            prompt = PromptBuilder.Truncate(prompt, MaxPromptChars);
        }

        var args = new List<string>
        {
            "-p", prompt,
            "-s",
            "--no-auto-update",
            "--no-color",
            "--no-ask-user",
            "--no-custom-instructions",
            "--disable-builtin-mcps",
            "--disable-client-telemetry",
            "--disallow-temp-dir",
            "--log-level=error",
            "--log-dir=" + Path.Combine(configDir, "logs"),
        };

        var model = AiRunDefaults.EffectiveModel("copilot", request, settings);
        args.Add("--model");
        args.Add(model);

        // Invalid levels are ignored here; AiFixer reports a note for them.
        var (effort, _) = AiRunDefaults.ValidateEffort("copilot", AiRunDefaults.RequestedEffort("copilot", request, settings));
        if (effort is not null)
        {
            args.Add("--reasoning-effort");
            args.Add(effort);
        }

        foreach (var tool in AvailableFileTools)
        {
            args.Add("--available-tools=" + tool);
        }

        args.Add("--allow-tool=read");
        args.Add("--allow-tool=write");
        args.Add("--deny-tool=shell");
        args.Add("--deny-tool=url");
        args.Add("--deny-tool=memory");

        var hasLearn = request.LearnProxyCommand is { Count: > 0 };
        if (hasLearn)
        {
            args.Add("--available-tools=" + LearnServerName);
            args.Add("--allow-tool=" + LearnServerName);
        }

        var roslynk = settings.Ai.Roslynk;
        var hasRoslynk = roslynk.Enabled && request.RoslynkCommand is { Count: > 0 };
        if (hasRoslynk)
        {
            args.Add("--available-tools=" + roslynk.ServerName);
            args.Add("--allow-tool=" + roslynk.ServerName);
            if (!roslynk.AllowWriteTools)
            {
                // Deny wins over allow: "server(tool)" denies exactly one MCP tool (copilot --help: --deny-tool='MyMCP(denied_tool)').
                foreach (var tool in RoslynkTools.WriteTools)
                {
                    args.Add($"--deny-tool={roslynk.ServerName}({tool})");
                }
            }
        }

        if (hasLearn || hasRoslynk)
        {
            args.Add("--additional-mcp-config");
            args.Add(BuildMcpConfig(
                hasLearn ? request.LearnProxyCommand : null,
                hasRoslynk ? (roslynk.ServerName, request.RoslynkCommand!) : null));
        }

        // Keep the token out of any MCP server process environment too.
        args.Add("--secret-env-vars=" + string.Join(',', new[] { CopilotTokenEnvironmentVariable, copilot.TokenVariable }.Distinct(StringComparer.OrdinalIgnoreCase)));

        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [CopilotTokenEnvironmentVariable] = token,
            ["HOME"] = configDir,
            ["USERPROFILE"] = configDir,
            ["APPDATA"] = Path.Combine(configDir, "appdata"),
            ["LOCALAPPDATA"] = Path.Combine(configDir, "localappdata"),
            ["XDG_CONFIG_HOME"] = Path.Combine(configDir, "config"),
            ["XDG_CACHE_HOME"] = Path.Combine(configDir, "cache"),
            ["XDG_STATE_HOME"] = Path.Combine(configDir, "state"),
            ["XDG_DATA_HOME"] = Path.Combine(configDir, "data"),
            ["COPILOT_HOME"] = Path.Combine(configDir, "copilot"),
            ["COPILOT_CACHE_HOME"] = Path.Combine(configDir, "copilot-cache"),
            ["TMPDIR"] = Path.Combine(configDir, "tmp"),
            ["COPILOT_AUTO_UPDATE"] = "false",
            ["COPILOT_DISABLE_LOGIN_SHELL_ENV"] = "1",
            ["COPILOT_DISABLE_DESKTOP_NOTIFICATIONS"] = "1",
            ["NO_COLOR"] = "1",
        };
        // Roslynk (like any child) must not export telemetry: drop every OpenTelemetry variable inherited from the pipeline.
        foreach (var name in ProcessRunner.CurrentEnvironment().Keys.Where(n => n.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            env[name] = null;
        }

        foreach (var otel in OtelVariables)
        {
            env[otel] = null;
        }

        foreach (var blocked in BlockedVariables)
        {
            if (!blocked.Equals(CopilotTokenEnvironmentVariable, StringComparison.Ordinal))
            {
                env[blocked] = null;
            }
        }

        return new ProcessSpec
        {
            FileName = copilot.Executable,
            Arguments = args,
            WorkingDirectory = request.WorkingDirectory,
            SecretScope = SecretScope.AiProvider,
            AllowedSecretVariables = [copilot.TokenVariable],
            Environment = env,
            Timeout = request.Timeout,
            MaskValues = [token],
        };
    }

    /// <summary>MCP config containing only the Learn proxy as a stdio ("local") server.</summary>
    public static string BuildMcpConfig(IReadOnlyList<string> proxyCommand) => BuildMcpConfig(proxyCommand, null);

    /// <summary>MCP config with the Learn proxy and/or the Roslynk server as stdio ("local") servers.</summary>
    public static string BuildMcpConfig(IReadOnlyList<string>? learnCommand, (string Name, IReadOnlyList<string> Command)? roslynk)
    {
        var servers = new JsonObject();
        if (learnCommand is { Count: > 0 })
        {
            servers[LearnServerName] = LocalServer(learnCommand);
        }

        if (roslynk is { } r)
        {
            servers[r.Name] = LocalServer(r.Command);
        }

        var root = new JsonObject { ["mcpServers"] = servers };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static JsonObject LocalServer(IReadOnlyList<string> command) => new()
    {
        ["type"] = "local",
        ["command"] = command[0],
        ["args"] = new JsonArray(command.Skip(1).Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
        ["env"] = new JsonObject(),
        ["tools"] = new JsonArray("*"),
    };

    private static string Mask(string text, string token) => text.Replace(token, "***", StringComparison.Ordinal);
}
