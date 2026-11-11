using DependencyAgent.Core.Configuration;
using DependencyAgent.Core.Processes;

namespace DependencyAgent.Core.Tests;

public class BuildTargetResolverTests
{
    private static string? Resolve(TempDir dir, string? configured = null) =>
        BuildTargetResolver.Resolve(dir.Path, new VerificationSettings { Solution = configured });

    [Fact]
    public void Configured_solution_wins_and_is_normalised()
    {
        using var dir = new TempDir();
        dir.Touch("a.sln");
        Assert.Equal("src/My.slnx", Resolve(dir, ".\\src\\My.slnx"));
    }

    [Fact]
    public void Single_root_solution()
    {
        using var dir = new TempDir();
        dir.Touch("App.sln");
        dir.Touch("src/App/App.csproj");
        Assert.Equal("App.sln", Resolve(dir));
    }

    [Fact]
    public void Sln_and_slnx_with_same_name_prefers_slnx()
    {
        using var dir = new TempDir();
        dir.Touch("App.sln");
        dir.Touch("App.slnx");
        Assert.Equal("App.slnx", Resolve(dir));
    }

    [Fact]
    public void Two_differently_named_root_solutions_are_ambiguous()
    {
        using var dir = new TempDir();
        dir.Touch("A.sln");
        dir.Touch("B.sln");
        dir.Touch("src/x/x.csproj");
        Assert.Null(Resolve(dir));
    }

    [Fact]
    public void Single_nested_solution_is_found()
    {
        using var dir = new TempDir();
        dir.Touch("backend/App.sln");
        dir.Touch("backend/src/App.csproj");
        Assert.Equal("backend/App.sln", Resolve(dir));
    }

    [Fact]
    public void Two_nested_solutions_are_ambiguous()
    {
        using var dir = new TempDir();
        dir.Touch("a/One.sln");
        dir.Touch("b/Two.sln");
        Assert.Null(Resolve(dir));
    }

    [Fact]
    public void Single_project_without_solution()
    {
        using var dir = new TempDir();
        dir.Touch("src/Tool/Tool.csproj");
        Assert.Equal("src/Tool/Tool.csproj", Resolve(dir));
    }

    [Fact]
    public void Several_projects_without_solution_are_ambiguous()
    {
        using var dir = new TempDir();
        dir.Touch("src/A/A.csproj");
        dir.Touch("src/B/B.csproj");
        Assert.Null(Resolve(dir));
    }

    [Fact]
    public void Nothing_to_build_returns_null()
    {
        using var dir = new TempDir();
        dir.Touch("README.md");
        Assert.Null(Resolve(dir));
    }

    [Fact]
    public void Ignores_bin_obj_git_and_node_modules()
    {
        using var dir = new TempDir();
        dir.Touch("obj/Stale.sln");
        dir.Touch("bin/Debug/Stale.csproj");
        dir.Touch("node_modules/pkg/Other.sln");
        dir.Touch(".git/Hidden.sln");
        dir.Touch("src/Real/Real.csproj");
        Assert.Equal("src/Real/Real.csproj", Resolve(dir));
    }

    [Fact]
    public void FindProjects_lists_all_project_files_sorted_and_skips_build_output()
    {
        using var dir = new TempDir();
        dir.Touch("src/B/B.csproj");
        dir.Touch("src/A/A.fsproj");
        dir.Touch("obj/Junk.csproj");
        dir.Touch("tests/T/T.vbproj");
        Assert.Equal(["src/A/A.fsproj", "src/B/B.csproj", "tests/T/T.vbproj"], BuildTargetResolver.FindProjects(dir.Path));
    }
}
