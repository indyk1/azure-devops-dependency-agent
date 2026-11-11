using DependencyAgent.Updates.Packages;
using NuGet.Versioning;

namespace DependencyAgent.Updates.Tests;

public class PipelineTests
{
    [Fact]
    public void Default_pipeline_has_steps_in_order()
    {
        var steps = UpdatePipeline.CreateDefault(_ => new FakeVersionSource());
        Assert.Equal(["target-framework", "global-json", "nuget-packages", "docker"], steps.Select(s => s.Name).ToArray());
    }

    [Fact]
    public void Convenience_overload_builds_the_same_steps()
    {
        Assert.Equal(4, UpdatePipeline.CreateDefault().Count);
    }

    [Fact]
    public async Task Full_pipeline_is_idempotent_on_a_realistic_repo()
    {
        using var repo = new TestRepo();
        repo.Write("global.json", "{\n  \"sdk\": {\n    \"version\": \"8.0.100\"\n  }\n}\n");
        repo.Write("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n  <ItemGroup>\n    <PackageReference Include=\"Microsoft.Extensions.Hosting\" Version=\"8.0.0\" />\n  </ItemGroup>\n</Project>\n");
        repo.Write("src/App/Dockerfile", "FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim\n");

        var source = new FakeVersionSource().Add("Microsoft.Extensions.Hosting", "8.0.0", "9.0.0", "10.0.1");
        var steps = UpdatePipeline.CreateDefault(_ => source);

        var first = new List<DependencyAgent.Core.Model.ChangeRecord>();
        foreach (var step in steps)
        {
            first.AddRange((await step.ApplyAsync(repo.Context(), CancellationToken.None)).Changes);
        }

        Assert.Equal(
            [DependencyAgent.Core.Model.ChangeKind.TargetFramework, DependencyAgent.Core.Model.ChangeKind.Sdk, DependencyAgent.Core.Model.ChangeKind.Sdk, DependencyAgent.Core.Model.ChangeKind.Package, DependencyAgent.Core.Model.ChangeKind.DockerImage],
            first.Select(c => c.Kind).ToArray());

        foreach (var step in steps)
        {
            Assert.Empty((await step.ApplyAsync(repo.Context(), CancellationToken.None)).Changes);
        }
    }
}

public class LiveNuGetTests
{
    [Fact]
    public async Task Queries_nuget_org_for_listed_versions()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("DEPENDENCY_AGENT_LIVE_TESTS") == "1", "Set DEPENDENCY_AGENT_LIVE_TESTS=1 to run live NuGet tests.");

        using var repo = new TestRepo();
        repo.Write("nuget.config", "<configuration><packageSources><clear /><add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\" /></packageSources></configuration>");
        var source = new NuGetPackageVersionSource(repo.Root);

        var versions = await source.GetVersionsAsync("Newtonsoft.Json", CancellationToken.None);

        Assert.Contains(NuGetVersion.Parse("13.0.3"), versions);
        Assert.Contains(versions, v => v.IsPrerelease);
        Assert.Equal(versions.Order().ToList(), versions);
        Assert.Same(await source.GetVersionsAsync("newtonsoft.json", CancellationToken.None), versions);
    }
}
