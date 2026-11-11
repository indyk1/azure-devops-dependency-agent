using DependencyAgent.Core.Configuration;
using DependencyAgent.Updates.Packages;
using NuGet.Versioning;

namespace DependencyAgent.Updates.Tests;

public class PackagePlannerTests
{
    private static PackagePolicySettings Policy() => new();

    private static PackageUpdateDecision Plan(string id, string current, string[] available, PackagePolicySettings? policy = null, int target = 10) =>
        PackageUpdatePlanner.Plan(id, NuGetVersion.Parse(current), Versions.Of(available), policy ?? Policy(), target);

    [Fact]
    public void Picks_highest_stable_for_ordinary_packages()
    {
        var d = Plan("Newtonsoft.Json", "12.0.1", ["12.0.1", "13.0.3", "13.0.4-beta1", "13.0.2"]);
        Assert.Equal(PackageDecisionKind.Update, d.Kind);
        Assert.Equal("13.0.3", d.NewVersion!.ToString());
    }

    [Fact]
    public void Tracked_package_takes_highest_of_target_major_even_when_newer_major_exists()
    {
        var d = Plan("Microsoft.Extensions.Logging", "8.0.0", ["8.0.1", "9.0.5", "10.0.0", "10.0.3", "11.0.0"]);
        Assert.Equal("10.0.3", d.NewVersion!.ToString());
        Assert.Equal("tracks target major", d.Reason);
    }

    [Fact]
    public void Tracked_package_without_target_major_falls_back_to_normal_rules()
    {
        var d = Plan("System.IdentityModel.Tokens.Jwt", "7.0.0", ["7.0.3", "8.15.0", "8.16.0-preview"]);
        Assert.Equal("8.15.0", d.NewVersion!.ToString());
    }

    [Fact]
    public void Tracked_package_ignores_prerelease_of_target_major_when_not_allowed()
    {
        var d = Plan("Microsoft.AspNetCore.Mvc.Testing", "8.0.0", ["9.0.1", "10.0.0-rc.2.25502.107"]);
        Assert.Equal("9.0.1", d.NewVersion!.ToString());
    }

    [Fact]
    public void Tracked_package_accepts_prerelease_of_target_major_when_allowed()
    {
        var policy = Policy();
        policy.AllowPrerelease = true;
        var d = Plan("Microsoft.Extensions.Hosting", "9.0.0", ["9.0.1", "10.0.0-rc.2"], policy);
        Assert.Equal("10.0.0-rc.2", d.NewVersion!.ToString());
    }

    [Fact]
    public void Untracked_package_is_not_pulled_to_target_major()
    {
        var policy = Policy();
        policy.TrackTargetMajorFor = ["Microsoft.Extensions.*"];
        var d = Plan("Serilog", "3.0.0", ["3.1.1", "4.2.0", "10.0.0"], policy);
        Assert.Equal("10.0.0", d.NewVersion!.ToString());
    }

    [Fact]
    public void Max_major_caps_the_update_and_adds_note()
    {
        var policy = Policy();
        policy.MaxMajor["AutoMapper"] = 14;
        var d = Plan("AutoMapper", "13.0.1", ["13.0.1", "14.0.0", "15.1.0"], policy);
        Assert.Equal("14.0.0", d.NewVersion!.ToString());
        Assert.Contains("max major 14", d.Note);
    }

    [Fact]
    public void Max_major_supports_globs_and_takes_lowest_cap()
    {
        var policy = Policy();
        policy.MaxMajor["Foo.*"] = 5;
        policy.MaxMajor["Foo.Bar"] = 3;
        var d = Plan("Foo.Bar", "2.0.0", ["3.2.0", "4.0.0", "5.0.0"], policy);
        Assert.Equal("3.2.0", d.NewVersion!.ToString());
    }

    [Fact]
    public void Max_major_below_current_never_downgrades()
    {
        var policy = Policy();
        policy.MaxMajor["Foo"] = 2;
        var d = Plan("Foo", "3.1.0", ["2.9.0", "3.1.0", "4.0.0"], policy);
        Assert.Equal(PackageDecisionKind.UpToDate, d.Kind);
        Assert.Null(d.NewVersion);
        Assert.NotNull(d.Note);
    }

    [Fact]
    public void Max_major_caps_tracked_packages_too()
    {
        var policy = Policy();
        policy.MaxMajor["Microsoft.Extensions.*"] = 9;
        var d = Plan("Microsoft.Extensions.Logging", "8.0.0", ["9.0.4", "10.0.1"], policy);
        Assert.Equal("9.0.4", d.NewVersion!.ToString());
    }

    [Fact]
    public void Ignore_wins_over_everything()
    {
        var policy = Policy();
        policy.Ignore = ["Legacy.*"];
        policy.Pins["Legacy.Thing"] = "9.9.9";
        var d = Plan("legacy.thing", "1.0.0", ["2.0.0"], policy);
        Assert.Equal(PackageDecisionKind.Ignored, d.Kind);
        Assert.Null(d.NewVersion);
        Assert.NotNull(d.Note);
    }

    [Fact]
    public void Pin_sets_exact_version_case_insensitively_even_if_not_listed_or_lower()
    {
        var policy = Policy();
        policy.Pins["newtonsoft.json"] = "12.0.3";
        var d = Plan("Newtonsoft.Json", "13.0.1", ["13.0.3"], policy);
        Assert.Equal(PackageDecisionKind.Update, d.Kind);
        Assert.Equal("12.0.3", d.NewVersion!.ToString());
        Assert.Contains("pinned", d.Note);
    }

    [Fact]
    public void Pin_already_satisfied_is_no_change()
    {
        var policy = Policy();
        policy.Pins["Foo"] = "1.2.3";
        var d = Plan("Foo", "1.2.3", ["2.0.0"], policy);
        Assert.Equal(PackageDecisionKind.Pinned, d.Kind);
        Assert.Null(d.NewVersion);
    }

    [Fact]
    public void Prerelease_current_moves_only_to_higher_stable()
    {
        Assert.Equal("2.0.0", Plan("Foo", "2.0.0-rc.1", ["1.9.0", "2.0.0", "2.1.0-beta"]).NewVersion!.ToString());
        Assert.Null(Plan("Foo", "3.0.0-rc.1", ["1.9.0", "2.0.0"]).NewVersion);
    }

    [Fact]
    public void Prerelease_current_with_allow_prerelease_moves_to_newer_prerelease()
    {
        var policy = Policy();
        policy.AllowPrerelease = true;
        Assert.Equal("2.0.0-rc.2", Plan("Foo", "2.0.0-rc.1", ["2.0.0-rc.2"], policy).NewVersion!.ToString());
    }

    [Fact]
    public void Prerelease_never_chosen_by_default()
    {
        Assert.Null(Plan("Foo", "1.0.0", ["1.0.0", "2.0.0-beta.1"]).NewVersion);
    }

    [Fact]
    public void Never_downgrades()
    {
        var d = Plan("Foo", "5.0.0", ["1.0.0", "4.9.0"]);
        Assert.Equal(PackageDecisionKind.UpToDate, d.Kind);
        Assert.Null(d.NewVersion);
    }

    [Fact]
    public void Equal_semantic_version_is_not_an_update()
    {
        Assert.Null(Plan("Foo", "1.2", ["1.2.0"]).NewVersion);
    }

    [Fact]
    public void Tracked_package_already_above_target_major_is_not_downgraded()
    {
        var d = Plan("Microsoft.Extensions.Logging", "11.0.0", ["10.0.5", "11.0.0"]);
        Assert.Null(d.NewVersion);
    }

    [Fact]
    public void No_available_versions_is_reported()
    {
        var d = Plan("Foo", "1.0.0", []);
        Assert.Equal(PackageDecisionKind.NoVersions, d.Kind);
        Assert.NotNull(d.Note);
    }

    [Theory]
    [InlineData("10.0.0", "10.0.0")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("1.2.3.4", "1.2.3.4")]
    [InlineData("2.0.0-rc.1", "2.0.0-rc.1")]
    public void Formats_versions_for_project_files(string input, string expected) =>
        Assert.Equal(expected, PackageUpdatePlanner.Format(NuGetVersion.Parse(input)));
}
