using System.Text.Json.Nodes;
using DependencyAgent.Core.Configuration;
using DependencyAgent.Core.Model;
using DependencyAgent.Core.Processes;

namespace DependencyAgent.Fixing.Tests;

public class CopilotCliProviderTests : IDisposable
{
    private const string TokenVariable = "DEPAGENT_TEST_COPILOT_TOKEN";
    private const string Token = "ghu_supersecret123";
    private readonly TempDir _repo = new();

    public CopilotCliProviderTests() => Environment.SetEnvironmentVariable(TokenVariable, Token);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(TokenVariable, null);
        _repo.Dispose();
    }

    private static AgentSettings Settings(string executable = "copilot")
    {
        var s = new AgentSettings();
        s.Ai.Copilot.TokenVariable = TokenVariable;
        s.Ai.Copilot.Executable = executable;
        return s;
    }

    private AiRunRequest Request(IReadOnlyList<string>? proxy = null, string? model = null) => new()
    {
        WorkingDirectory = _repo.Path,
        SystemPrompt = "SYSTEM",
        TaskPrompt = "TASK",
        AllowedEditPatterns = ["**/*.cs"],
        ProtectedPatterns = [],
        Timeout = TimeSpan.FromMinutes(7),
        LearnProxyCommand = proxy,
        Model = model,
    };

    private static readonly string[] Proxy = ["dotnet", "/agent/agent.dll", "learn-proxy", "--endpoint", "https://learn.microsoft.com/api/mcp"];

    [Fact]
    public void VerifiedVersionAndPackage_AreExposed()
    {
        Assert.Equal("@github/copilot", CopilotCliProvider.NpmPackage);
        Assert.Matches(@"^\d+\.\d+\.\d+$", CopilotCliProvider.VerifiedVersion);
    }

    [Fact]
    public void Arguments_NeverAllowShellUrlsOrEverything()
    {
        var spec = CopilotCliProvider.BuildProcessSpec(Request(Proxy), Settings(), Token, "/tmp/cfg");
        var args = spec.Arguments;

        Assert.Equal("copilot", spec.FileName);
        Assert.Equal(_repo.Path, spec.WorkingDirectory);
        Assert.Equal(TimeSpan.FromMinutes(7), spec.Timeout);
        Assert.Equal("-p", args[0]);
        Assert.StartsWith("SYSTEM", args[1]);
        Assert.Contains("TASK", args[1]);

        foreach (var forbidden in new[] { "--allow-all", "--yolo", "--allow-all-tools", "--allow-all-urls", "--allow-all-paths", "--trust-folder", "--autopilot", "--enable-all-github-mcp-tools" })
        {
            Assert.DoesNotContain(forbidden, args);
        }

        Assert.DoesNotContain(args, a => a.StartsWith("--allow-url", StringComparison.Ordinal));
        Assert.DoesNotContain(args, a => a.StartsWith("--allow-tool=shell", StringComparison.Ordinal));
        Assert.Contains("--deny-tool=shell", args);
        Assert.Contains("--deny-tool=url", args);
        Assert.Contains("--disable-builtin-mcps", args);
        Assert.Contains("--no-auto-update", args);
        Assert.Contains("--disable-client-telemetry", args);
        Assert.Contains("--no-custom-instructions", args);
        Assert.Contains("--no-ask-user", args);
        Assert.Contains("--no-color", args);
        Assert.Contains("--disallow-temp-dir", args);
        Assert.Contains("-s", args);
        Assert.Contains("--log-dir=" + Path.Combine("/tmp/cfg", "logs"), args);

        var available = args.Where(a => a.StartsWith("--available-tools=", StringComparison.Ordinal)).Select(a => a["--available-tools=".Length..]).ToList();
        Assert.Equal(["view", "create", "edit", "grep", "glob", "learn"], available);
        Assert.DoesNotContain("bash", available);
        Assert.DoesNotContain(available, t => t.Contains("fetch") || t.Contains("web"));

        var allowed = args.Where(a => a.StartsWith("--allow-tool=", StringComparison.Ordinal)).Select(a => a["--allow-tool=".Length..]).ToList();
        Assert.Equal(["read", "write", "learn"], allowed);
    }

    [Fact]
    public void ModelFlag_DefaultsToProviderModel_AndRequestOverrides()
    {
        var def = CopilotCliProvider.BuildProcessSpec(Request(), Settings(), Token, "/c").Arguments;
        Assert.Equal("claude-sonnet-5.5", def[def.ToList().IndexOf("--model") + 1]);
        var args = CopilotCliProvider.BuildProcessSpec(Request(model: "gpt-5"), Settings(), Token, "/c").Arguments;
        Assert.Equal("gpt-5", args[args.ToList().IndexOf("--model") + 1]);
    }

    [Fact]
    public void McpConfig_ContainsOnlyTheLearnProxy()
    {
        var spec = CopilotCliProvider.BuildProcessSpec(Request(Proxy), Settings(), Token, "/c");
        var index = spec.Arguments.ToList().IndexOf("--additional-mcp-config");
        Assert.True(index > 0);
        var json = JsonNode.Parse(spec.Arguments[index + 1])!.AsObject();

        var servers = json["mcpServers"]!.AsObject();
        Assert.Equal(["learn"], servers.Select(s => s.Key).ToArray());
        var learn = servers["learn"]!.AsObject();
        Assert.Equal("local", (string?)learn["type"]);
        Assert.Equal("dotnet", (string?)learn["command"]);
        Assert.Equal(Proxy.Skip(1).ToArray(), learn["args"]!.AsArray().Select(a => (string)a!).ToArray());
        Assert.Empty(learn["env"]!.AsObject());
    }

    [Fact]
    public void WithoutProxy_NoMcpConfigAndNoLearnTools()
    {
        var args = CopilotCliProvider.BuildProcessSpec(Request(), Settings(), Token, "/c").Arguments;
        Assert.DoesNotContain("--additional-mcp-config", args);
        Assert.DoesNotContain("--available-tools=learn", args);
        Assert.DoesNotContain("--allow-tool=learn", args);
    }

    [Fact]
    public void Environment_IsScopedToAiProviderWithOnlyTheTokenVariable()
    {
        var spec = CopilotCliProvider.BuildProcessSpec(Request(Proxy), Settings(), Token, "/tmp/cfg");

        Assert.Equal(SecretScope.AiProvider, spec.SecretScope);
        Assert.Equal([TokenVariable], spec.AllowedSecretVariables);
        Assert.DoesNotContain("SYSTEM_ACCESSTOKEN", spec.AllowedSecretVariables);
        Assert.Equal([Token], spec.MaskValues);

        // Simulate a parent environment full of credentials and run it through the real scrubbing.
        var env = new Dictionary<string, string?>
        {
            ["PATH"] = "/usr/bin",
            ["SYSTEM_ACCESSTOKEN"] = "ado",
            ["AZURE_DEVOPS_EXT_PAT"] = "ado2",
            ["VSS_NUGET_ACCESSTOKEN"] = "nuget",
            ["NUGET_AUTH_TOKEN"] = "nuget2",
            ["GH_TOKEN"] = "other-gh",
            ["GITHUB_TOKEN"] = "other-gh2",
            ["OPENAI_API_KEY"] = "sk-other",
            ["COPILOT_ALLOW_ALL"] = "true",
            ["COPILOT_PROVIDER_BASE_URL"] = "http://evil",
            ["COPILOT_GITHUB_TOKEN"] = "parent-token",
            [TokenVariable] = Token,
            ["MY_DB_PASSWORD"] = "pw",
            ["HOME"] = "/home/real",
        };
        ProcessRunner.ApplyEnvironment(env, spec);

        Assert.False(env.ContainsKey("SYSTEM_ACCESSTOKEN"));
        Assert.False(env.ContainsKey("AZURE_DEVOPS_EXT_PAT"));
        Assert.False(env.ContainsKey("VSS_NUGET_ACCESSTOKEN"));
        Assert.False(env.ContainsKey("NUGET_AUTH_TOKEN"));
        Assert.False(env.ContainsKey("GH_TOKEN"));
        Assert.False(env.ContainsKey("GITHUB_TOKEN"));
        Assert.False(env.ContainsKey("OPENAI_API_KEY"));
        Assert.False(env.ContainsKey("MY_DB_PASSWORD"));
        Assert.False(env.ContainsKey("COPILOT_ALLOW_ALL"));
        Assert.False(env.ContainsKey("COPILOT_PROVIDER_BASE_URL"));
        Assert.Equal(Token, env["COPILOT_GITHUB_TOKEN"]);
        Assert.Equal("/tmp/cfg", env["HOME"]);
        Assert.Equal(Path.Combine("/tmp/cfg", "copilot"), env["COPILOT_HOME"]);
        Assert.Equal(Path.Combine("/tmp/cfg", "config"), env["XDG_CONFIG_HOME"]);
        Assert.Equal("false", env["COPILOT_AUTO_UPDATE"]);
        Assert.Equal("/usr/bin", env["PATH"]);
    }

    [Fact]
    public void Environment_TokenFromCustomVariableIsMappedToCopilotVariable()
    {
        var settings = Settings();
        settings.Ai.Copilot.TokenVariable = "MY_COPILOT_PAT";
        var spec = CopilotCliProvider.BuildProcessSpec(Request(), settings, "custom-token", "/c");
        Assert.Equal("custom-token", spec.Environment["COPILOT_GITHUB_TOKEN"]);
        Assert.Equal(["MY_COPILOT_PAT"], spec.AllowedSecretVariables);
        Assert.Contains("--secret-env-vars=COPILOT_GITHUB_TOKEN,MY_COPILOT_PAT", spec.Arguments);
    }

    [Fact]
    public void CheckAvailability_ReportsMissingTokenAndMissingExecutable()
    {
        var provider = new CopilotCliProvider(new FakeProcessRunner());
        var exe = OperatingSystem.IsWindows() ? "cmd" : "sh";
        Assert.Null(provider.CheckAvailability(Settings(exe)));

        Assert.Contains("not found", provider.CheckAvailability(Settings("definitely-not-installed-copilot-xyz")));

        Environment.SetEnvironmentVariable(TokenVariable, null);
        Assert.Contains(TokenVariable, provider.CheckAvailability(Settings(exe)));
        Environment.SetEnvironmentVariable(TokenVariable, "$(COPILOT_GITHUB_TOKEN)");
        Assert.NotNull(provider.CheckAvailability(Settings(exe)));
    }

    [Fact]
    public async Task Run_ReturnsNeutralizedTailOfStdoutAndMasksToken()
    {
        var runner = new FakeProcessRunner
        {
            Handler = _ => new ProcessResult(0, "intro\n" + new string('z', 5000) + $"\n##vso[task.setvariable variable=x]y token={Token}\nFixed it.", "", false, TimeSpan.Zero),
        };
        var provider = new CopilotCliProvider(runner);

        var result = await provider.RunAsync(Request(Proxy), Settings(), TestContext.Current.CancellationToken);

        Assert.True(result.Completed);
        Assert.Null(result.Error);
        Assert.DoesNotContain("intro", result.Summary);
        Assert.DoesNotContain("##vso[", result.Summary);
        Assert.DoesNotContain(Token, result.Summary);
        Assert.EndsWith("Fixed it.", result.Summary);
        Assert.True(result.Summary.Length <= 3100);
        Assert.Single(runner.Specs);
    }

    [Fact]
    public async Task Run_ConfigDirectoryIsOutsideRepoAndCleanedUp()
    {
        var runner = new FakeProcessRunner();
        string? home = null;
        runner.Handler = spec =>
        {
            home = spec.Environment["HOME"];
            Assert.True(Directory.Exists(home));
            Assert.DoesNotContain(_repo.Path, home!);
            return new ProcessResult(0, "ok", "", false, TimeSpan.Zero);
        };

        await new CopilotCliProvider(runner).RunAsync(Request(), Settings(), TestContext.Current.CancellationToken);

        Assert.NotNull(home);
        Assert.False(Directory.Exists(home));
    }

    [Fact]
    public async Task Run_ReportsTimeoutAndNonZeroExit()
    {
        var timeout = new CopilotCliProvider(new FakeProcessRunner { Handler = _ => new ProcessResult(-1, "partial", "", true, TimeSpan.Zero) });
        var r1 = await timeout.RunAsync(Request(), Settings(), TestContext.Current.CancellationToken);
        Assert.False(r1.Completed);
        Assert.Contains("timed out", r1.Error);

        var failing = new CopilotCliProvider(new FakeProcessRunner { Handler = _ => new ProcessResult(2, "", $"auth failed for {Token}", false, TimeSpan.Zero) });
        var r2 = await failing.RunAsync(Request(), Settings(), TestContext.Current.CancellationToken);
        Assert.False(r2.Completed);
        Assert.Contains("code 2", r2.Error);
        Assert.DoesNotContain(Token, r2.Error);
    }

    [Fact]
    public async Task Run_WithoutToken_DoesNotLaunchProcess()
    {
        Environment.SetEnvironmentVariable(TokenVariable, null);
        var runner = new FakeProcessRunner();
        var result = await new CopilotCliProvider(runner).RunAsync(Request(), Settings(), TestContext.Current.CancellationToken);
        Assert.False(result.Completed);
        Assert.Empty(runner.Specs);
    }

    [Fact]
    public void LearnProxyCommand_FromAiFixer_PrefixesDotnetForDll()
    {
        var learn = new LearnMcpSettings { MaxQueryLength = 150 };
        var cmd = AiFixer.BuildLearnProxyCommand("/agent/agent.dll", learn, "/tmp/deny.txt", "/out/a.jsonl");
        Assert.Equal(["dotnet", "/agent/agent.dll", "learn-proxy", "--endpoint", "https://learn.microsoft.com/api/mcp", "--max-query-length", "150", "--deny-terms-file", "/tmp/deny.txt", "--audit-log", "/out/a.jsonl"], cmd);
        var exe = AiFixer.BuildLearnProxyCommand("/agent/dependency-agent", learn, "d", "a");
        Assert.Equal("/agent/dependency-agent", exe[0]);
        Assert.Equal("learn-proxy", exe[1]);
    }
}
