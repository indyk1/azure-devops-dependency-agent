using DependencyAgent.Core.Model;
using DependencyAgent.Core.Processes;
using DependencyAgent.Verification;

namespace DependencyAgent.Verification.Tests;

/// <summary>Argument / secret-scope / parsing behaviour with a fake process runner (no dotnet is launched).</summary>
public class DotnetVerifierUnitTests : IDisposable
{
    private readonly TempDir _repo = new();

    public void Dispose() => _repo.Dispose();

    private const string SdkProject = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";

    private static string ArgValue(ProcessSpec spec, string name) => spec.Arguments[spec.Arguments.ToList().IndexOf(name) + 1];

    [Fact]
    public async Task Build_RestoreThenBuild_WithScopesAndArguments()
    {
        _repo.Write("App.sln", "");
        _repo.Write("App/App.csproj", SdkProject);
        var fake = new FakeProcessRunner(s => FakeProcessRunner.Ok(
            s.Arguments[0] == "build" ? "/r/App/A.cs(1,1): warning CS0618: 'X' is obsolete [/r/App/App.csproj]" : ""));
        var ctx = TestContext.Create(_repo.Path, s => s.Verification.BuildArguments = ["-p:ContinuousIntegrationBuild=true"]);

        var result = await new DotnetVerifier(fake).BuildAsync(ctx, CancellationToken.None);

        Assert.Equal(2, fake.Specs.Count);
        var restore = fake.Specs[0];
        Assert.Equal("dotnet", restore.FileName);
        Assert.Equal(["restore", "App.sln", "-nologo", "-tl:off"], restore.Arguments);
        Assert.Equal(SecretScope.NuGetFeeds, restore.SecretScope);
        Assert.Equal(_repo.Path, restore.WorkingDirectory);
        Assert.Equal(TimeSpan.FromMinutes(ctx.Settings.Verification.BuildTimeoutMinutes), restore.Timeout);

        var build = fake.Specs[1];
        Assert.Equal(SecretScope.None, build.SecretScope);
        Assert.Contains("--no-restore", build.Arguments);
        Assert.Contains("-tl:off", build.Arguments);
        Assert.Contains("-clp:NoSummary;ForceNoAlign", build.Arguments);
        Assert.Equal("-p:ContinuousIntegrationBuild=true", build.Arguments[^1]);

        Assert.True(result.Succeeded);
        Assert.Equal(VerificationStage.Build, result.Stage);
        var w = Assert.Single(result.Warnings);
        Assert.Equal("CS0618", w.Code);
    }

    [Fact]
    public async Task Restore_Failure_ReturnsRestoreStage_AndSkipsBuild()
    {
        _repo.Write("App/App.csproj", SdkProject);
        var fake = new FakeProcessRunner(_ => FakeProcessRunner.Fail("/r/App/App.csproj : error NU1101: Unable to find package Foo. No packages exist with this id in source(s): nuget.org [/r/App/App.csproj]"));
        var result = await new DotnetVerifier(fake).BuildAsync(TestContext.Create(_repo.Path), CancellationToken.None);

        Assert.Single(fake.Specs);
        Assert.False(result.Succeeded);
        Assert.Equal(VerificationStage.Restore, result.Stage);
        Assert.Equal("NU1101", Assert.Single(result.Errors).Code);
        Assert.Contains("NU1101", result.OutputTail);
    }

    [Fact]
    public async Task Build_Failure_WithoutParsedError_AddsSyntheticError()
    {
        _repo.Write("App/App.csproj", SdkProject);
        var fake = new FakeProcessRunner(s => s.Arguments[0] == "restore" ? FakeProcessRunner.Ok() : FakeProcessRunner.Fail("something odd"));
        var result = await new DotnetVerifier(fake).BuildAsync(TestContext.Create(_repo.Path), CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Single(result.Errors);
    }

    [Fact]
    public async Task Build_Timeout_IsFailure()
    {
        _repo.Write("App/App.csproj", SdkProject);
        var fake = new FakeProcessRunner(s => s.Arguments[0] == "restore" ? FakeProcessRunner.Ok() : new ProcessResult(-1, "", "", true, TimeSpan.FromMinutes(20)));
        var result = await new DotnetVerifier(fake).BuildAsync(TestContext.Create(_repo.Path), CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Contains("timed out", result.Errors.Single().Message);
    }

    [Fact]
    public async Task OutputTail_IsLimitedToLastLines()
    {
        _repo.Write("App/App.csproj", SdkProject);
        var big = string.Join('\n', Enumerable.Range(1, 400).Select(i => "line " + i));
        var fake = new FakeProcessRunner(s => FakeProcessRunner.Ok(s.Arguments[0] == "build" ? big : ""));
        var result = await new DotnetVerifier(fake).BuildAsync(TestContext.Create(_repo.Path), CancellationToken.None);
        var lines = result.OutputTail.Split('\n');
        Assert.Equal(DotnetVerifier.OutputTailLines, lines.Length);
        Assert.Equal("line 400", lines[^1]);
    }

    [Fact]
    public async Task AmbiguousSolutions_BuildsTemporarySlnxOutsideRepo_SkippingLegacyProjects()
    {
        _repo.Write("a/A.csproj", SdkProject);
        _repo.Write("b/B.csproj", SdkProject);
        _repo.Write("legacy/Legacy.csproj", "<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"><PropertyGroup/></Project>");
        _repo.Write("c/C.csproj", "<Project><Sdk Name=\"Microsoft.NET.Sdk\" /></Project>");
        string? slnxContent = null;
        string? slnxPath = null;
        var fake = new FakeProcessRunner(s =>
        {
            slnxPath ??= s.Arguments[1];
            if (File.Exists(slnxPath))
            {
                slnxContent ??= File.ReadAllText(slnxPath);
            }

            return FakeProcessRunner.Ok();
        });

        var result = await new DotnetVerifier(fake).BuildAsync(TestContext.Create(_repo.Path), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(slnxPath);
        Assert.EndsWith(".slnx", slnxPath);
        Assert.False(slnxPath!.StartsWith(_repo.Path, StringComparison.Ordinal));
        Assert.Contains(Path.Combine(_repo.Path, "a", "A.csproj"), slnxContent);
        Assert.Contains(Path.Combine(_repo.Path, "b", "B.csproj"), slnxContent);
        Assert.Contains(Path.Combine(_repo.Path, "c", "C.csproj"), slnxContent);
        Assert.DoesNotContain("Legacy", slnxContent);
        Assert.False(File.Exists(slnxPath), "temporary solution must be cleaned up");
        Assert.Empty(Directory.EnumerateFiles(_repo.Path, "*.slnx", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task NoBuildableProjects_FailsWithoutRunningDotnet()
    {
        _repo.Write("README.md", "x");
        var fake = new FakeProcessRunner(_ => FakeProcessRunner.Ok());
        var result = await new DotnetVerifier(fake).BuildAsync(TestContext.Create(_repo.Path), CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Empty(fake.Specs);
    }

    [Fact]
    public async Task Test_Disabled_ReturnsSuccessWithNullTests()
    {
        var fake = new FakeProcessRunner(_ => FakeProcessRunner.Ok());
        var result = await new DotnetVerifier(fake).TestAsync(TestContext.Create(_repo.Path, s => s.Verification.RunTests = false), CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Null(result.Tests);
        Assert.Empty(fake.Specs);
        Assert.Equal(VerificationStage.Test, result.Stage);
    }

    [Fact]
    public async Task Test_VsTest_Arguments_ScopeAndTrxParsing()
    {
        _repo.Write("App.sln", "");
        string? resultsDir = null;
        var fake = new FakeProcessRunner(s =>
        {
            resultsDir = ArgValue(s, "--results-directory");
            File.WriteAllText(Path.Combine(resultsDir, "x.trx"), """
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>
                  <UnitTestResult testName="T1" outcome="Passed" />
                  <UnitTestResult testName="T2" outcome="Failed"><Output><ErrorInfo><Message>boom</Message></ErrorInfo></Output></UnitTestResult>
                </Results></TestRun>
                """);
            return FakeProcessRunner.Fail("Failed!  - Failed: 1, Passed: 1, Skipped: 0, Total: 2");
        });
        var ctx = TestContext.Create(_repo.Path, s => s.Verification.TestArguments = ["--filter", "Category!=Integration"]);

        var result = await new DotnetVerifier(fake).TestAsync(ctx, CancellationToken.None);

        var spec = Assert.Single(fake.Specs);
        Assert.Equal(SecretScope.None, spec.SecretScope);
        Assert.Equal(["test", "App.sln", "--no-build", "-nologo", "-tl:off", "--logger", "trx", "--results-directory"], spec.Arguments.Take(8));
        Assert.Equal(["--filter", "Category!=Integration"], spec.Arguments.Skip(9));
        Assert.Equal(TimeSpan.FromMinutes(ctx.Settings.Verification.TestTimeoutMinutes), spec.Timeout);
        Assert.False(resultsDir!.StartsWith(_repo.Path, StringComparison.Ordinal));
        Assert.False(Directory.Exists(resultsDir), "results directory must be cleaned up");

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.Tests!.Total);
        Assert.Equal("T2", Assert.Single(result.Tests.FailedTests).Name);
        Assert.Equal("boom", result.Tests.FailedTests[0].Message);
    }

    [Fact]
    public async Task Test_NoTrx_FallsBackToConsoleSummary()
    {
        _repo.Write("App.sln", "");
        var fake = new FakeProcessRunner(_ => FakeProcessRunner.Ok("Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3, Duration: 1 s"));
        var result = await new DotnetVerifier(fake).TestAsync(TestContext.Create(_repo.Path), CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal(3, result.Tests!.Passed);
    }

    [Fact]
    public async Task Test_NoTestProjects_SucceedsWithZeroTotal()
    {
        _repo.Write("App.sln", "");
        var fake = new FakeProcessRunner(_ => FakeProcessRunner.Ok("No test is available in /r/App.dll. Make sure that test discoverer & executors are registered."));
        var result = await new DotnetVerifier(fake).TestAsync(TestContext.Create(_repo.Path), CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Tests!.Total);
    }

    [Fact]
    public async Task Test_ExitCodeFailure_WithoutSummary_IsFailure()
    {
        _repo.Write("App.sln", "");
        var fake = new FakeProcessRunner(_ => FakeProcessRunner.Fail("The test host process crashed", 1));
        var result = await new DotnetVerifier(fake).TestAsync(TestContext.Create(_repo.Path), CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Single(result.Errors);
    }

    [Fact]
    public async Task Test_MicrosoftTestingPlatform_AdaptsArguments()
    {
        _repo.Write("App.sln", "");
        _repo.Write("global.json", """
            {
              // comment
              "sdk": { "version": "10.0.100" },
              "test": { "runner": "Microsoft.Testing.Platform" },
            }
            """);
        var fake = new FakeProcessRunner(s => FakeProcessRunner.Ok("Test run summary: Passed!\n  total: 2\n  failed: 0\n  succeeded: 2\n  skipped: 0\n"));
        var result = await new DotnetVerifier(fake).TestAsync(TestContext.Create(_repo.Path), CancellationToken.None);

        var spec = Assert.Single(fake.Specs);
        Assert.Equal(["test", "--solution", "App.sln", "--no-build", "-nologo", "-tl:off", "--report-trx", "--results-directory"], spec.Arguments.Take(8));
        Assert.DoesNotContain("--logger", spec.Arguments);
        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Tests!.Total);
    }

    [Fact]
    public async Task Test_MicrosoftTestingPlatform_ProjectTargetUsesProjectOption()
    {
        _repo.Write("App/App.csproj", SdkProject);
        _repo.Write("global.json", """{ "test": { "runner": "Microsoft.Testing.Platform" } }""");
        var fake = new FakeProcessRunner(_ => FakeProcessRunner.Ok());
        await new DotnetVerifier(fake).TestAsync(TestContext.Create(_repo.Path), CancellationToken.None);
        Assert.Equal(["test", "--project", "App/App.csproj"], fake.Specs[0].Arguments.Take(3));
    }

    [Fact]
    public void GlobalJson_WithoutRunner_IsVsTest()
    {
        _repo.Write("global.json", """{ "sdk": { "version": "10.0.100" } }""");
        Assert.False(DotnetVerifier.UsesMicrosoftTestingPlatform(_repo.Path));
        _repo.Write("global.json", "not json");
        Assert.False(DotnetVerifier.UsesMicrosoftTestingPlatform(_repo.Path));
    }
}

/// <summary>Runs the real dotnet CLI against tiny generated projects (no package downloads needed).</summary>
public class DotnetVerifierIntegrationTests : IDisposable
{
    private readonly TempDir _repo = new();

    public void Dispose() => _repo.Dispose();

    private const string LibProject = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>";

    [Fact]
    public async Task Build_ObsoleteCall_YieldsParsedCs0618Warning()
    {
        _repo.Write("Lib/Lib.csproj", LibProject);
        _repo.Write("Lib/Class1.cs", """
            using System;
            namespace Lib;
            public static class Class1
            {
                [Obsolete("x")] public static void Old() { }
                public static void Caller() { Old(); }
            }
            """);

        var result = await new DotnetVerifier(new ProcessRunner()).BuildAsync(TestContext.Create(_repo.Path), CancellationToken.None);

        Assert.True(result.Succeeded, result.OutputTail);
        var w = Assert.Single(result.Warnings, d => d.Code == "CS0618");
        Assert.Equal("Lib/Class1.cs", w.File);
        Assert.Equal(6, w.Line);
        Assert.Equal("Lib/Lib.csproj", w.Project);
        Assert.Contains("obsolete", w.Message);
        Assert.Empty(Directory.EnumerateFiles(_repo.Path, "*.slnx", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Build_CompileError_YieldsParsedCs0246Error()
    {
        _repo.Write("Lib/Lib.csproj", LibProject);
        _repo.Write("Lib/Class1.cs", "namespace Lib; public class Class1 { public MissingType F; }\n");

        var result = await new DotnetVerifier(new ProcessRunner()).BuildAsync(TestContext.Create(_repo.Path), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(VerificationStage.Build, result.Stage);
        var e = Assert.Single(result.Errors);
        Assert.Equal("CS0246", e.Code);
        Assert.Equal("Lib/Class1.cs", e.File);
        Assert.Equal("Lib/Lib.csproj", e.Project);
    }

    [Fact]
    public async Task Build_TwoProjectsWithoutSolution_BuildsAllThroughTemporarySolution()
    {
        _repo.Write("A/A.csproj", LibProject);
        _repo.Write("A/A.cs", "namespace A; public class C { }\n");
        _repo.Write("B/B.csproj", LibProject);
        _repo.Write("B/B.cs", "namespace B; public class C { int x = \"s\"; }\n");

        var result = await new DotnetVerifier(new ProcessRunner()).BuildAsync(TestContext.Create(_repo.Path), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("CS0029", Assert.Single(result.Errors).Code);
        Assert.Equal("B/B.cs", result.Errors.Single().File);
        Assert.True(File.Exists(_repo.Combine("A/bin/Debug/net10.0/A.dll")));
        Assert.Empty(Directory.EnumerateFiles(_repo.Path, "*.slnx", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Test_ProjectWithoutTests_Succeeds()
    {
        _repo.Write("Lib/Lib.csproj", LibProject);
        _repo.Write("Lib/Class1.cs", "namespace Lib; public class Class1 { }\n");
        var verifier = new DotnetVerifier(new ProcessRunner());
        var ctx = TestContext.Create(_repo.Path);
        Assert.True((await verifier.BuildAsync(ctx, CancellationToken.None)).Succeeded);

        var result = await verifier.TestAsync(ctx, CancellationToken.None);

        Assert.True(result.Succeeded, result.OutputTail);
        Assert.Equal(0, result.Tests!.Total);
        Assert.Empty(Directory.EnumerateFiles(_repo.Path, "*.trx", SearchOption.AllDirectories));
    }
}
