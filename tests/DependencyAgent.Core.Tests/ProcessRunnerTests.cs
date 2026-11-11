using DependencyAgent.Core.Processes;

namespace DependencyAgent.Core.Tests;

public class ProcessRunnerTests
{
    private static ProcessSpec Sh(string script, Action<ProcessSpecBuilder>? configure = null)
    {
        var b = new ProcessSpecBuilder { FileName = "/bin/sh", Arguments = ["-c", script] };
        configure?.Invoke(b);
        return b.Build();
    }

    // ---- ApplyEnvironment ----

    private static Dictionary<string, string?> SampleEnvironment() => new()
    {
        ["PATH"] = "/usr/bin",
        ["SYSTEM_ACCESSTOKEN"] = "ado",
        ["VSS_NUGET_ACCESSTOKEN"] = "nuget",
        ["COPILOT_GITHUB_TOKEN"] = "copilot",
        ["OPENAI_API_KEY"] = "openai",
        ["SOME_RANDOM_SECRET"] = "x",
    };

    private static ProcessSpec Spec(SecretScope scope, IReadOnlyList<string>? allowed = null, IReadOnlyDictionary<string, string?>? env = null) => new()
    {
        FileName = "x",
        WorkingDirectory = ".",
        SecretScope = scope,
        AllowedSecretVariables = allowed ?? [],
        Environment = env ?? new Dictionary<string, string?>(),
    };

    [Theory]
    [InlineData(SecretScope.None, new string[] { })]
    [InlineData(SecretScope.NuGetFeeds, new[] { "VSS_NUGET_ACCESSTOKEN" })]
    [InlineData(SecretScope.AzureDevOps, new[] { "SYSTEM_ACCESSTOKEN" })]
    [InlineData(SecretScope.AiProvider, new[] { "COPILOT_GITHUB_TOKEN", "OPENAI_API_KEY" })]
    public void ApplyEnvironment_keeps_only_secrets_allowed_by_scope(SecretScope scope, string[] expectedSecrets)
    {
        var env = SampleEnvironment();
        ProcessRunner.ApplyEnvironment(env, Spec(scope));

        Assert.True(env.ContainsKey("PATH"));
        var remaining = env.Keys.Where(SecretEnvironment.IsSecret).OrderBy(k => k).ToArray();
        Assert.Equal(expectedSecrets.OrderBy(k => k).ToArray(), remaining);
    }

    [Fact]
    public void ApplyEnvironment_allows_extra_named_secret_and_sets_defaults()
    {
        var env = SampleEnvironment();
        env["MY_COPILOT_TOKEN"] = "custom";
        ProcessRunner.ApplyEnvironment(env, Spec(SecretScope.None, allowed: ["MY_COPILOT_TOKEN"]));

        Assert.Equal("custom", env["MY_COPILOT_TOKEN"]);
        Assert.False(env.ContainsKey("SYSTEM_ACCESSTOKEN"));
        Assert.Equal("1", env["DOTNET_CLI_TELEMETRY_OPTOUT"]);
        Assert.Equal("1", env["DOTNET_NOLOGO"]);
        Assert.Equal("0", env["GIT_TERMINAL_PROMPT"]);
    }

    [Fact]
    public void ApplyEnvironment_explicit_values_override_and_null_removes()
    {
        var env = SampleEnvironment();
        var overrides = new Dictionary<string, string?> { ["PATH"] = null, ["FOO"] = "bar", ["GIT_TERMINAL_PROMPT"] = "1" };
        ProcessRunner.ApplyEnvironment(env, Spec(SecretScope.None, env: overrides));

        Assert.False(env.ContainsKey("PATH"));
        Assert.Equal("bar", env["FOO"]);
        Assert.Equal("1", env["GIT_TERMINAL_PROMPT"]);
    }

    // ---- RunAsync ----

    [Fact]
    public async Task RunAsync_captures_stdout_stderr_and_exit_code()
    {
        var result = await new ProcessRunner().RunAsync(Sh("echo out; echo err 1>&2; exit 3"), CancellationToken.None);

        Assert.Equal(3, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.False(result.Succeeded);
        Assert.Contains("out", result.StandardOutput);
        Assert.Contains("err", result.StandardError);
        Assert.Contains("out", result.CombinedOutput);
        Assert.Contains("err", result.CombinedOutput);
    }

    [Fact]
    public async Task RunAsync_success_has_zero_exit_code()
    {
        var result = await new ProcessRunner().RunAsync(Sh("exit 0"), CancellationToken.None);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task RunAsync_feeds_standard_input()
    {
        var result = await new ProcessRunner().RunAsync(Sh("cat", b => b.StandardInput = "hello stdin"), CancellationToken.None);
        Assert.Contains("hello stdin", result.StandardOutput);
    }

    [Fact]
    public async Task RunAsync_kills_process_on_timeout()
    {
        var result = await new ProcessRunner().RunAsync(
            Sh("sleep 30", b => b.Timeout = TimeSpan.FromMilliseconds(500)), CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.False(result.Succeeded);
        Assert.Equal(-1, result.ExitCode);
        Assert.True(result.Duration < TimeSpan.FromSeconds(20), $"took {result.Duration}");
    }

    [Fact]
    public async Task RunAsync_timeout_kills_child_processes_too()
    {
        // The shell starts a grandchild that holds the stdout pipe open; the runner must still return promptly.
        var result = await new ProcessRunner().RunAsync(
            Sh("sleep 30 & wait", b => b.Timeout = TimeSpan.FromMilliseconds(500)), CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.True(result.Duration < TimeSpan.FromSeconds(20), $"took {result.Duration}");
    }

    [Fact]
    public async Task RunAsync_rethrows_when_caller_cancels()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProcessRunner().RunAsync(Sh("sleep 30"), cts.Token));
    }

    [Fact]
    public async Task RunAsync_masks_values_in_captured_output_and_callback()
    {
        var lines = new List<string>();
        var result = await new ProcessRunner().RunAsync(
            Sh("echo token=hunter2; echo hunter2 1>&2", b =>
            {
                b.MaskValues = ["hunter2"];
                b.OnOutputLine = l => { lock (lines) { lines.Add(l); } };
            }), CancellationToken.None);

        Assert.DoesNotContain("hunter2", result.StandardOutput);
        Assert.DoesNotContain("hunter2", result.StandardError);
        Assert.Contains("token=***", result.StandardOutput);
        Assert.All(lines, l => Assert.DoesNotContain("hunter2", l));
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public async Task RunAsync_neutralises_azure_pipelines_logging_commands()
    {
        var result = await new ProcessRunner().RunAsync(
            Sh("echo '##vso[task.setvariable variable=x;issecret=false]owned'; echo '##[error]boom'; echo '##VSO[task.complete result=Succeeded]' 1>&2"), CancellationToken.None);

        Assert.DoesNotContain("##vso[", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("##[", result.StandardOutput);
        Assert.DoesNotContain("##vso[", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("task.setvariable", result.StandardOutput);
    }

    [Fact]
    public void PipelineCommandNeutralizer_breaks_prefixes()
    {
        Assert.DoesNotContain("##vso[", PipelineCommandNeutralizer.Neutralize("a ##vso[x] b"));
        Assert.DoesNotContain("##vso[", PipelineCommandNeutralizer.Neutralize("##VSO[x]"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("##[", PipelineCommandNeutralizer.Neutralize("##[warning]x"));
        Assert.Equal("plain text", PipelineCommandNeutralizer.Neutralize("plain text"));
    }

    [Fact]
    public async Task RunAsync_scrubs_secrets_from_the_child_environment()
    {
        const string leaked = "DA_TEST_LEAK_TOKEN";
        const string ai = "COPILOT_GITHUB_TOKEN";
        var previousAi = Environment.GetEnvironmentVariable(ai);
        Environment.SetEnvironmentVariable(leaked, "leaked-value-1");
        Environment.SetEnvironmentVariable(ai, "ai-value-2");
        try
        {
            var runner = new ProcessRunner();
            var none = await runner.RunAsync(EnvSpec(SecretScope.None), CancellationToken.None);
            Assert.True(none.Succeeded);
            Assert.DoesNotContain(leaked, none.StandardOutput);
            Assert.DoesNotContain(ai, none.StandardOutput);
            Assert.Contains("PATH=", none.StandardOutput);
            Assert.Contains("DOTNET_NOLOGO=1", none.StandardOutput);

            var aiScope = await runner.RunAsync(EnvSpec(SecretScope.AiProvider), CancellationToken.None);
            Assert.Contains($"{ai}=ai-value-2", aiScope.StandardOutput);
            Assert.DoesNotContain(leaked, aiScope.StandardOutput);

            var ado = await runner.RunAsync(EnvSpec(SecretScope.AzureDevOps), CancellationToken.None);
            Assert.DoesNotContain(ai, ado.StandardOutput);

            var explicitSecret = await runner.RunAsync(EnvSpec(SecretScope.None, new Dictionary<string, string?> { ["EXPLICIT_TOKEN"] = "given" }), CancellationToken.None);
            Assert.Contains("EXPLICIT_TOKEN=given", explicitSecret.StandardOutput);
        }
        finally
        {
            Environment.SetEnvironmentVariable(leaked, null);
            Environment.SetEnvironmentVariable(ai, previousAi);
        }
    }

    private static ProcessSpec EnvSpec(SecretScope scope, IReadOnlyDictionary<string, string?>? env = null) => new()
    {
        FileName = "/usr/bin/env",
        WorkingDirectory = Path.GetTempPath(),
        SecretScope = scope,
        Environment = env ?? new Dictionary<string, string?>(),
    };

    [Fact]
    public void CurrentEnvironment_snapshots_process_variables()
    {
        const string name = "DA_TEST_SNAPSHOT";
        Environment.SetEnvironmentVariable(name, "v");
        try
        {
            Assert.Equal("v", ProcessRunner.CurrentEnvironment()[name]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    /// <summary>ProcessSpec uses init-only members; this keeps the test helpers terse.</summary>
    private sealed class ProcessSpecBuilder
    {
        public required string FileName { get; init; }

        public IReadOnlyList<string> Arguments { get; init; } = [];

        public string? StandardInput { get; set; }

        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

        public IReadOnlyList<string> MaskValues { get; set; } = [];

        public Action<string>? OnOutputLine { get; set; }

        public ProcessSpec Build() => new()
        {
            FileName = FileName,
            Arguments = Arguments,
            WorkingDirectory = Path.GetTempPath(),
            StandardInput = StandardInput,
            Timeout = Timeout,
            MaskValues = MaskValues,
            OnOutputLine = OnOutputLine,
        };
    }
}

public sealed class InheritedToolingVariableTests
{
    [Theory]
    [InlineData("MSBuildSDKsPath", true)]
    [InlineData("MSBUILD_EXE_PATH", true)]
    [InlineData("MSBuildExtensionsPath", true)]
    [InlineData("VSTEST_HOST_DEBUG", true)]
    [InlineData("DOTNET_HOST_PATH", true)]
    [InlineData("DOTNET_ROOT", false)]
    [InlineData("PATH", false)]
    public void Classifies(string name, bool expected) =>
        Assert.Equal(expected, DependencyAgent.Core.Processes.ProcessRunner.IsInheritedToolingVariable(name));

    [Fact]
    public void ApplyEnvironment_RemovesParentMsBuildVariables_ButKeepsExplicitOverrides()
    {
        var env = new Dictionary<string, string?> { ["MSBuildSDKsPath"] = "/sdk/10/Sdks", ["PATH"] = "/usr/bin" };
        var spec = new DependencyAgent.Core.Processes.ProcessSpec
        {
            FileName = "dotnet",
            WorkingDirectory = "/",
            Environment = new Dictionary<string, string?> { ["MSBUILDDISABLENODEREUSE"] = "1" },
        };

        DependencyAgent.Core.Processes.ProcessRunner.ApplyEnvironment(env, spec);

        Assert.False(env.ContainsKey("MSBuildSDKsPath"));
        Assert.Equal("/usr/bin", env["PATH"]);
        Assert.Equal("1", env["MSBUILDDISABLENODEREUSE"]);
    }
}
