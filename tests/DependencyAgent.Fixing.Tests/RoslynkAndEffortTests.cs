using System.Text.Json.Nodes;
using DependencyAgent.Core;
using DependencyAgent.Core.Configuration;
using DependencyAgent.Core.Model;
using DependencyAgent.Fixing.OpenAi;

namespace DependencyAgent.Fixing.Tests;

public sealed class FakeMcpBridge : IMcpToolBridge
{
    public List<McpToolInfo> Tools { get; } =
    [
        new("open_solution", "Open a solution", """{"type":"object","properties":{"solutionPath":{"type":"string"}}}"""),
        new("get_diagnostics", "Diagnostics", """{"type":"object"}"""),
        new("apply_patch", "Apply a patch", """{"type":"object"}"""),
        new("rename_symbol", "Rename", """{"type":"object"}"""),
    ];

    public List<(string Name, string Args)> Calls { get; } = [];

    public string Output { get; set; } = "ok";

    public bool Disposed { get; private set; }

    public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<McpToolInfo>>(Tools);

    public Task<string> CallToolAsync(string name, string argumentsJson, CancellationToken cancellationToken)
    {
        Calls.Add((name, argumentsJson));
        return Task.FromResult(Output);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

public class CopilotRoslynkAndEffortTests : IDisposable
{
    private const string TokenVariable = "DEPAGENT_TEST_COPILOT_TOKEN2";
    private const string Token = "ghu_secret_two";

    public CopilotRoslynkAndEffortTests() => Environment.SetEnvironmentVariable(TokenVariable, Token);

    public void Dispose() => Environment.SetEnvironmentVariable(TokenVariable, null);

    private static AgentSettings Settings(Action<AgentSettings>? tweak = null)
    {
        var s = new AgentSettings();
        s.Ai.Copilot.TokenVariable = TokenVariable;
        tweak?.Invoke(s);
        return s;
    }

    private static AiRunRequest Request(string? model = null, string? effort = null, bool learn = true, bool roslynk = true) => new()
    {
        WorkingDirectory = "/repo",
        SystemPrompt = "S",
        TaskPrompt = "T",
        AllowedEditPatterns = ["**/*.cs"],
        ProtectedPatterns = [],
        Timeout = TimeSpan.FromMinutes(1),
        Model = model,
        ReasoningEffort = effort,
        LearnProxyCommand = learn ? ["dotnet", "agent.dll", "learn-proxy"] : null,
        RoslynkCommand = roslynk ? ["roslynk", "stdio"] : null,
        SolutionPath = roslynk ? "/tmp/x.slnx" : null,
    };

    private static string ValueOf(IReadOnlyList<string> args, string flag)
    {
        var i = args.ToList().IndexOf(flag);
        return i < 0 ? "<none>" : args[i + 1];
    }

    [Fact]
    public void Defaults_PassSonnet55AndHighEffort()
    {
        var args = CopilotCliProvider.BuildProcessSpec(Request(), Settings(), Token, "/c").Arguments;
        Assert.Equal("claude-sonnet-5.5", ValueOf(args, "--model"));
        Assert.Equal("high", ValueOf(args, "--reasoning-effort"));
    }

    [Fact]
    public void Precedence_RequestThenAiSettingsThenCopilotDefault()
    {
        var viaAi = CopilotCliProvider.BuildProcessSpec(Request(), Settings(s => { s.Ai.Model = "m-ai"; s.Ai.ReasoningEffort = "low"; }), Token, "/c").Arguments;
        Assert.Equal("m-ai", ValueOf(viaAi, "--model"));
        Assert.Equal("low", ValueOf(viaAi, "--reasoning-effort"));

        var viaRequest = CopilotCliProvider.BuildProcessSpec(Request("m-req", "xhigh"), Settings(s => { s.Ai.Model = "m-ai"; s.Ai.ReasoningEffort = "low"; }), Token, "/c").Arguments;
        Assert.Equal("m-req", ValueOf(viaRequest, "--model"));
        Assert.Equal("xhigh", ValueOf(viaRequest, "--reasoning-effort"));
    }

    [Fact]
    public void InvalidEffort_IsIgnored_AndNull_OmitsFlag()
    {
        var invalid = CopilotCliProvider.BuildProcessSpec(Request(effort: "turbo"), Settings(), Token, "/c").Arguments;
        Assert.DoesNotContain("--reasoning-effort", invalid);

        var none = CopilotCliProvider.BuildProcessSpec(Request(), Settings(s => s.Ai.Copilot.ReasoningEffort = null), Token, "/c").Arguments;
        Assert.DoesNotContain("--reasoning-effort", none);

        var (effort, note) = AiRunDefaults.ValidateEffort("copilot", "turbo");
        Assert.Null(effort);
        Assert.Contains("turbo", note);
        Assert.Equal(("max", null), AiRunDefaults.ValidateEffort("copilot", "max"));
    }

    [Fact]
    public void McpConfig_HasBothServers_WhenRoslynkEnabled()
    {
        var args = CopilotCliProvider.BuildProcessSpec(Request(), Settings(), Token, "/c").Arguments;
        var json = JsonNode.Parse(ValueOf(args, "--additional-mcp-config"))!.AsObject();
        var servers = json["mcpServers"]!.AsObject();
        Assert.Equal(["learn", "roslynk"], servers.Select(s => s.Key).ToArray());
        Assert.Equal("roslynk", (string?)servers["roslynk"]!["command"]);
        Assert.Equal(["stdio"], servers["roslynk"]!["args"]!.AsArray().Select(a => (string)a!).ToArray());
        Assert.Empty(servers["roslynk"]!["env"]!.AsObject());

        Assert.Contains("--available-tools=roslynk", args);
        Assert.Contains("--allow-tool=roslynk", args);
        // The token is hidden from every MCP server's environment.
        Assert.Contains("--secret-env-vars=COPILOT_GITHUB_TOKEN," + TokenVariable, args);
    }

    [Fact]
    public void OnlyLearn_WhenRoslynkDisabled()
    {
        var args = CopilotCliProvider.BuildProcessSpec(Request(), Settings(s => s.Ai.Roslynk.Enabled = false), Token, "/c").Arguments;
        var servers = JsonNode.Parse(ValueOf(args, "--additional-mcp-config"))!["mcpServers"]!.AsObject();
        Assert.Equal(["learn"], servers.Select(s => s.Key).ToArray());
        Assert.DoesNotContain("--allow-tool=roslynk", args);
        Assert.DoesNotContain(args, a => a.Contains("roslynk", StringComparison.Ordinal) && a.StartsWith("--deny-tool", StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyRoslynk_WhenNoLearnProxy()
    {
        var args = CopilotCliProvider.BuildProcessSpec(Request(learn: false), Settings(), Token, "/c").Arguments;
        var servers = JsonNode.Parse(ValueOf(args, "--additional-mcp-config"))!["mcpServers"]!.AsObject();
        Assert.Equal(["roslynk"], servers.Select(s => s.Key).ToArray());
        Assert.DoesNotContain("--available-tools=learn", args);
    }

    [Fact]
    public void WriteTools_AreDeniedIndividually_WhenNotAllowed()
    {
        var denied = CopilotCliProvider.BuildProcessSpec(Request(), Settings(s => s.Ai.Roslynk.AllowWriteTools = false), Token, "/c").Arguments
            .Where(a => a.StartsWith("--deny-tool=roslynk(", StringComparison.Ordinal)).ToList();
        Assert.Equal(RoslynkTools.WriteTools.Count, denied.Count);
        Assert.Contains("--deny-tool=roslynk(apply_patch)", denied);
        Assert.Contains("--deny-tool=roslynk(apply_code_fix)", denied);

        var allowed = CopilotCliProvider.BuildProcessSpec(Request(), Settings(), Token, "/c").Arguments;
        Assert.DoesNotContain(allowed, a => a.StartsWith("--deny-tool=roslynk(", StringComparison.Ordinal));
        Assert.Contains("--deny-tool=shell", allowed);
    }

    [Fact]
    public void OtelVariables_AreRemovedFromTheChildEnvironment()
    {
        Environment.SetEnvironmentVariable("OTEL_CUSTOM_TEST_SETTING", "x");
        try
        {
            var env = CopilotCliProvider.BuildProcessSpec(Request(), Settings(), Token, "/c").Environment;
            foreach (var name in new[] { "OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_EXPORTER_OTLP_HEADERS", "OTEL_TRACES_EXPORTER", "OTEL_CUSTOM_TEST_SETTING" })
            {
                Assert.True(env.ContainsKey(name), name);
                Assert.Null(env[name]);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_CUSTOM_TEST_SETTING", null);
        }
    }
}

public class OpenAiRoslynkAndEffortTests : IDisposable
{
    private const string KeyVariable = "DEPAGENT_TEST_OPENAI_KEY2";
    private readonly TempDir _repo = new();

    public OpenAiRoslynkAndEffortTests() => Environment.SetEnvironmentVariable(KeyVariable, "sk-test");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(KeyVariable, null);
        _repo.Dispose();
    }

    private static AgentSettings Settings(Action<AgentSettings>? tweak = null)
    {
        var s = new AgentSettings();
        s.Ai.OpenAi.ApiKeyVariable = KeyVariable;
        tweak?.Invoke(s);
        return s;
    }

    private AiRunRequest Request(string? effort = null, bool roslynk = true) => new()
    {
        WorkingDirectory = _repo.Path,
        SystemPrompt = "SYSTEM",
        TaskPrompt = "TASK",
        AllowedEditPatterns = ["**/*.cs"],
        ProtectedPatterns = [],
        Timeout = TimeSpan.FromMinutes(1),
        ReasoningEffort = effort,
        RoslynkCommand = roslynk ? ["roslynk", "stdio"] : null,
        SolutionPath = roslynk ? "/tmp/x.slnx" : null,
    };

    private static ScriptedBackend Finish() => new ScriptedBackend().Call("f", "finish", """{"summary":"done"}""");

    [Fact]
    public async Task ReasoningEffort_PassedToBackend_WithPrecedence()
    {
        ChatBackendOptions? seen = null;
        var provider = new OpenAiProvider(o => { seen = o; return Finish(); });

        await provider.RunAsync(Request(), Settings(s => s.Ai.OpenAi.ReasoningEffort = "medium"), TestContext.Current.CancellationToken);
        Assert.Equal("medium", seen!.ReasoningEffort);
        Assert.Equal("gpt-5.1", seen.Model);

        await provider.RunAsync(Request(), Settings(s => { s.Ai.OpenAi.ReasoningEffort = "medium"; s.Ai.ReasoningEffort = "low"; s.Ai.Model = "m1"; }), TestContext.Current.CancellationToken);
        Assert.Equal("low", seen.ReasoningEffort);
        Assert.Equal("m1", seen.Model);

        await provider.RunAsync(Request("HIGH"), Settings(s => s.Ai.ReasoningEffort = "low"), TestContext.Current.CancellationToken);
        Assert.Equal("high", seen.ReasoningEffort);

        await provider.RunAsync(Request(), Settings(), TestContext.Current.CancellationToken);
        Assert.Null(seen.ReasoningEffort);

        // Copilot-only levels are omitted for OpenAI.
        await provider.RunAsync(Request("xhigh"), Settings(), TestContext.Current.CancellationToken);
        Assert.Null(seen.ReasoningEffort);
    }

    [Fact]
    public void ReasoningEffortMapping_CoversSupportedLevels()
    {
        Assert.NotNull(OpenAiChatBackend.MapReasoningEffort("minimal"));
        Assert.NotNull(OpenAiChatBackend.MapReasoningEffort("Low"));
        Assert.NotNull(OpenAiChatBackend.MapReasoningEffort("medium"));
        Assert.NotNull(OpenAiChatBackend.MapReasoningEffort("high"));
        Assert.Null(OpenAiChatBackend.MapReasoningEffort("max"));
        Assert.Null(OpenAiChatBackend.MapReasoningEffort(null));
    }

    [Fact]
    public async Task Bridge_ExposesPrefixedTools_FiltersWrites_RoutesCalls_AndCapsOutput()
    {
        var bridge = new FakeMcpBridge { Output = new string('x', 60_000) };
        var backend = new ScriptedBackend()
            .Call("1", "roslynk__get_diagnostics", "{}")
            .Call("2", "roslynk__apply_patch", "{}")
            .Call("f", "finish", """{"summary":"done"}""");
        var provider = new OpenAiProvider(_ => backend, (_, _, _, _) => Task.FromResult<IMcpToolBridge>(bridge));

        var result = await provider.RunAsync(Request(), Settings(s => s.Ai.Roslynk.AllowWriteTools = false), TestContext.Current.CancellationToken);

        Assert.True(result.Completed);
        var names = backend.ToolSets[0].Select(t => t.Name).ToList();
        Assert.Contains("roslynk__open_solution", names);
        Assert.Contains("roslynk__get_diagnostics", names);
        Assert.DoesNotContain("roslynk__apply_patch", names);
        Assert.DoesNotContain("roslynk__rename_symbol", names);
        Assert.Equal("finish", names[^1]);

        // Only the read tool reached the server; the denied write tool was refused.
        Assert.Equal([("get_diagnostics", "{}")], bridge.Calls);
        var toolMessages = backend.Conversations[^1].Where(m => m.Role == ChatRole.Tool).ToList();
        Assert.Contains("<tool_output name=\"roslynk__get_diagnostics\"", toolMessages[0].Content);
        Assert.InRange(toolMessages[0].Content!.Length, 1, OpenAiProvider.MaxRoslynkOutputChars + 400);
        Assert.StartsWith("ERROR:", toolMessages[1].Content);
        Assert.True(bridge.Disposed);
    }

    [Fact]
    public async Task Bridge_WriteToolsExposed_WhenAllowed()
    {
        var bridge = new FakeMcpBridge();
        var backend = Finish();
        var provider = new OpenAiProvider(_ => backend, (_, _, _, _) => Task.FromResult<IMcpToolBridge>(bridge));
        await provider.RunAsync(Request(), Settings(), TestContext.Current.CancellationToken);
        Assert.Contains(backend.ToolSets[0], t => t.Name == "roslynk__apply_patch");
    }

    [Fact]
    public async Task Bridge_StartFailure_ContinuesWithoutRoslynk()
    {
        var backend = Finish();
        var provider = new OpenAiProvider(_ => backend, (_, _, _, _) => throw new InvalidOperationException("no roslynk"));
        var result = await provider.RunAsync(Request(), Settings(), TestContext.Current.CancellationToken);
        Assert.True(result.Completed);
        Assert.DoesNotContain(backend.ToolSets[0], t => t.Name.StartsWith("roslynk__", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bridge_NotStarted_WhenDisabledOrNoCommand()
    {
        var started = 0;
        var provider = new OpenAiProvider(_ => Finish(), (_, _, _, _) => { started++; return Task.FromResult<IMcpToolBridge>(new FakeMcpBridge()); });
        await provider.RunAsync(Request(), Settings(s => s.Ai.Roslynk.Enabled = false), TestContext.Current.CancellationToken);
        await provider.RunAsync(Request(roslynk: false), Settings(), TestContext.Current.CancellationToken);
        Assert.Equal(0, started);
    }

    [Fact]
    public void ScrubbedEnvironment_ContainsNoSecrets()
    {
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "sk-leak");
        Environment.SetEnvironmentVariable("SYSTEM_ACCESSTOKEN", "ado-leak");
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", "http://x");
        try
        {
            var env = McpStdioToolBridge.BuildScrubbedEnvironment();
            Assert.DoesNotContain("OPENAI_API_KEY", env.Keys);
            Assert.DoesNotContain("SYSTEM_ACCESSTOKEN", env.Keys);
            Assert.DoesNotContain(env.Keys, k => k.StartsWith("OTEL_", StringComparison.Ordinal));
            Assert.DoesNotContain(env.Values, v => v is "sk-leak" or "ado-leak");
            Assert.Equal("1", env["DOTNET_CLI_TELEMETRY_OPTOUT"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
            Environment.SetEnvironmentVariable("SYSTEM_ACCESSTOKEN", null);
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", null);
        }
    }
}

public class SolutionPathResolverTests : IDisposable
{
    private readonly TempDir _repo = new();
    private readonly TempDir _gen = new();

    public void Dispose()
    {
        _repo.Dispose();
        _gen.Dispose();
    }

    private const string SdkProject = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";

    [Fact]
    public void ExistingSolution_IsUsedByAbsolutePath()
    {
        _repo.Write("App.sln", "x");
        _repo.Write("src/A/A.csproj", SdkProject);
        var path = SolutionPathResolver.Resolve(_repo.Path, new VerificationSettings(), Path.Combine(_gen.Path, "g"));
        Assert.Equal(Path.Combine(_repo.Path, "App.sln"), path);
        Assert.False(Directory.Exists(Path.Combine(_gen.Path, "g")));
    }

    [Fact]
    public void SingleProject_GetsGeneratedSlnxOutsideTheRepo()
    {
        _repo.Write("src/A/A.csproj", SdkProject);
        var path = SolutionPathResolver.Resolve(_repo.Path, new VerificationSettings(), Path.Combine(_gen.Path, "g"))!;
        Assert.StartsWith(_gen.Path, path);
        Assert.EndsWith(".slnx", path);
        var text = File.ReadAllText(path);
        Assert.Contains($"<Project Path=\"{Path.Combine(_repo.Path, "src/A/A.csproj")}\" />", text);
        Assert.DoesNotContain(".slnx", Directory.GetFiles(_repo.Path, "*", SearchOption.AllDirectories).Select(Path.GetExtension));
    }

    [Fact]
    public void Ambiguous_ListsAllSdkProjects_AndSkipsLegacy()
    {
        _repo.Write("a.sln", "x");
        _repo.Write("b.sln", "x");
        _repo.Write("src/A/A.csproj", SdkProject);
        _repo.Write("src/B/B.csproj", SdkProject);
        _repo.Write("src/Old/Old.csproj", "<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"></Project>");
        var path = SolutionPathResolver.Resolve(_repo.Path, new VerificationSettings(), Path.Combine(_gen.Path, "g"))!;
        var text = File.ReadAllText(path);
        Assert.Contains("A.csproj", text);
        Assert.Contains("B.csproj", text);
        Assert.DoesNotContain("Old.csproj", text);
    }

    [Fact]
    public void NoSdkProjects_ReturnsNull()
    {
        _repo.Write("src/Old/Old.csproj", "<Project ToolsVersion=\"15.0\"></Project>");
        Assert.Null(SolutionPathResolver.Resolve(_repo.Path, new VerificationSettings(), Path.Combine(_gen.Path, "g")));
    }
}

public class RoslynkPromptTests
{
    private static readonly string[] Allowed = ["**/*.cs"];

    [Fact]
    public void Guidance_OnlyWhenRoslynkAvailable()
    {
        var without = PromptBuilder.BuildSystemPrompt("net10.0", Allowed, []);
        Assert.DoesNotContain("Roslynk", without);

        var with = PromptBuilder.BuildSystemPrompt("net10.0", Allowed, [], docsToolsAvailable: true, roslynkSolutionPath: "/tmp/run/roslynk.slnx");
        Assert.Contains("/tmp/run/roslynk.slnx", with);
        foreach (var term in new[] { "open_solution", "reload_solution", "get_diagnostics", "find_references", "get_callers", "get_code_actions", "apply_code_fix", "checkOnly=true", "no code or data leaves", "EXTERNAL" })
        {
            Assert.Contains(term, with);
        }
    }

    [Fact]
    public void Guidance_AdaptsWhenWriteToolsDisabled()
    {
        var p = PromptBuilder.BuildSystemPrompt("net10.0", Allowed, [], roslynkSolutionPath: "/s.slnx", roslynkWriteTools: false);
        Assert.Contains("write tools are disabled", p);
        Assert.DoesNotContain("checkOnly=true", p);
    }
}
