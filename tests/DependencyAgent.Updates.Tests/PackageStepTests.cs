using DependencyAgent.Core.Model;
using DependencyAgent.Updates.Steps;

namespace DependencyAgent.Updates.Tests;

public class PackageStepTests : IDisposable
{
    private readonly TestRepo _repo = new();
    private readonly FakeVersionSource _source = new();

    public void Dispose() => _repo.Dispose();

    private Task<UpdateStepResult> Run() =>
        new NuGetPackageUpdateStep(_ => _source).ApplyAsync(_repo.Context(), CancellationToken.None);

    private static string Project(string items) => $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n{items}\n  </ItemGroup>\n</Project>\n";

    [Fact]
    public async Task Updates_version_attribute_and_records_change()
    {
        _source.Add("Newtonsoft.Json", "12.0.1", "13.0.3");
        _repo.Write("src/App.csproj", Project("    <PackageReference Include=\"Newtonsoft.Json\" Version=\"12.0.1\" />"));
        var result = await Run();

        Assert.Contains("Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />", _repo.Read("src/App.csproj"));
        Assert.Equal(new ChangeRecord(ChangeKind.Package, "src/App.csproj", "Newtonsoft.Json", "12.0.1", "13.0.3", "latest"), Assert.Single(result.Changes));
    }

    [Fact]
    public async Task Updates_child_element_version_and_version_override()
    {
        _source.Add("A", "1.0.0", "2.0.0").Add("B", "1.0.0", "3.0.0");
        _repo.Write("App.csproj", Project("""
                <PackageReference Include="A"><Version>1.0.0</Version></PackageReference>
                <PackageReference Include="B" VersionOverride="1.0.0" />
            """));
        await Run();
        var text = _repo.Read("App.csproj");
        Assert.Contains("<Version>2.0.0</Version>", text);
        Assert.Contains("VersionOverride=\"3.0.0\"", text);
    }

    [Fact]
    public async Task Preserves_attribute_order_quotes_and_line_endings()
    {
        _source.Add("A", "1.0.0", "2.0.0");
        var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Project Sdk='Microsoft.NET.Sdk'>\r\n  <ItemGroup>\r\n    <PackageReference   Version = '1.0.0'  PrivateAssets=\"all\" Include=\"A\"/>\r\n  </ItemGroup>\r\n</Project>\r\n";
        _repo.Write("App.csproj", xml, bom: true);
        await Run();
        Assert.Equal(xml.Replace("'1.0.0'", "'2.0.0'", StringComparison.Ordinal), _repo.Read("App.csproj"));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, _repo.ReadBytes("App.csproj")[..3]);
    }

    [Fact]
    public async Task Cpm_updates_directory_packages_props_not_projects()
    {
        _source.Add("Serilog", "3.0.0", "4.1.0").Add("Microsoft.Extensions.Logging", "8.0.0", "9.0.1", "10.0.2");
        _repo.Write("Directory.Packages.props", """
            <Project>
              <PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="Serilog" Version="3.0.0" />
                <PackageVersion Include="Microsoft.Extensions.Logging" Version="8.0.0" />
              </ItemGroup>
              <ItemGroup>
                <GlobalPackageReference Include="Serilog" Version="3.0.0" />
              </ItemGroup>
            </Project>
            """);
        var project = Project("    <PackageReference Include=\"Serilog\" />\n    <PackageReference Include=\"Microsoft.Extensions.Logging\" />");
        _repo.Write("App/App.csproj", project);
        var result = await Run();

        var props = _repo.Read("Directory.Packages.props");
        Assert.Contains("Include=\"Serilog\" Version=\"4.1.0\"", props);
        Assert.Contains("Include=\"Microsoft.Extensions.Logging\" Version=\"10.0.2\"", props);
        Assert.Equal(project, _repo.Read("App/App.csproj"));
        Assert.Equal(3, result.Changes.Count);
        Assert.All(result.Changes, c => Assert.Equal("Directory.Packages.props", c.File));
    }

    [Fact]
    public async Task Version_override_in_project_is_updated_with_cpm()
    {
        _source.Add("Serilog", "3.0.0", "4.1.0");
        _repo.Write("Directory.Packages.props", "<Project><ItemGroup><PackageVersion Include=\"Serilog\" Version=\"3.0.0\" /></ItemGroup></Project>");
        _repo.Write("App.csproj", Project("    <PackageReference Include=\"Serilog\" VersionOverride=\"3.0.0\" />"));
        await Run();
        Assert.Contains("VersionOverride=\"4.1.0\"", _repo.Read("App.csproj"));
        Assert.Contains("Version=\"4.1.0\"", _repo.Read("Directory.Packages.props"));
    }

    [Fact]
    public async Task Directory_build_props_package_references_are_updated()
    {
        _source.Add("StyleCop.Analyzers", "1.1.118", "1.2.0");
        _repo.Write("Directory.Build.props", "<Project>\n  <ItemGroup>\n    <PackageReference Include=\"StyleCop.Analyzers\" Version=\"1.1.118\" />\n  </ItemGroup>\n</Project>\n");
        var result = await Run();
        Assert.Contains("Version=\"1.2.0\"", _repo.Read("Directory.Build.props"));
        Assert.Single(result.Changes);
    }

    [Theory]
    [InlineData("1.*", "floating")]
    [InlineData("1.0.*-*", "floating")]
    [InlineData("[1.0,2.0)", "range")]
    [InlineData("(1.0,)", "range")]
    [InlineData("[1.0.0]", "range")]
    [InlineData("$(Unknown)", "not defined")]
    [InlineData("$(A)$(B)", "expressions")]
    public async Task Skips_floating_ranges_and_unresolvable_with_note(string version, string noteFragment)
    {
        _source.Add("Foo", "1.0.0", "9.0.0");
        var xml = Project($"    <PackageReference Include=\"Foo\" Version=\"{version}\" />");
        _repo.Write("App.csproj", xml);
        var result = await Run();
        Assert.Empty(result.Changes);
        Assert.Equal(xml, _repo.Read("App.csproj"));
        Assert.Contains(result.Notes, n => n.Contains("Foo") && n.Contains(noteFragment));
    }

    [Fact]
    public async Task Property_version_in_same_file_is_updated_once()
    {
        _source.Add("Microsoft.Extensions.Logging", "8.0.0", "10.0.1").Add("Microsoft.Extensions.Options", "8.0.0", "10.0.1");
        _repo.Write("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <MsExtVersion>8.0.0</MsExtVersion>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.Extensions.Logging" Version="$(MsExtVersion)" />
                <PackageReference Include="Microsoft.Extensions.Options" Version="$(MsExtVersion)" />
              </ItemGroup>
            </Project>
            """);
        var result = await Run();
        var text = _repo.Read("App.csproj");
        Assert.Contains("<MsExtVersion>10.0.1</MsExtVersion>", text);
        Assert.Contains("Version=\"$(MsExtVersion)\"", text);
        Assert.Equal(2, result.Changes.Count);
        Assert.All(result.Changes, c => Assert.Equal("App.csproj", c.File));
    }

    [Fact]
    public async Task Property_defined_in_directory_build_props_is_updated_there()
    {
        _source.Add("Foo", "1.0.0", "2.0.0");
        _repo.Write("Directory.Build.props", "<Project>\n  <PropertyGroup>\n    <FooVersion>1.0.0</FooVersion>\n  </PropertyGroup>\n</Project>\n");
        _repo.Write("src/App.csproj", Project("    <PackageReference Include=\"Foo\" Version=\"$(FooVersion)\" />"));
        var result = await Run();
        Assert.Contains("<FooVersion>2.0.0</FooVersion>", _repo.Read("Directory.Build.props"));
        Assert.Equal("Directory.Build.props", Assert.Single(result.Changes).File);
    }

    [Fact]
    public async Task Property_in_directory_packages_props_is_updated()
    {
        _source.Add("Foo", "1.0.0", "2.0.0");
        _repo.Write("Directory.Packages.props", "<Project>\n  <PropertyGroup><FooV>1.0.0</FooV></PropertyGroup>\n  <ItemGroup>\n    <PackageVersion Include=\"Foo\" Version=\"$(FooV)\" />\n  </ItemGroup>\n</Project>\n");
        await Run();
        Assert.Contains("<FooV>2.0.0</FooV>", _repo.Read("Directory.Packages.props"));
    }

    [Fact]
    public async Task Property_shared_with_ignored_package_is_left_alone()
    {
        _repo.Settings.Packages.Ignore = ["Keep.Me"];
        _source.Add("Foo", "1.0.0", "2.0.0");
        var xml = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><V>1.0.0</V></PropertyGroup>\n  <ItemGroup>\n    <PackageReference Include=\"Foo\" Version=\"$(V)\" />\n    <PackageReference Include=\"Keep.Me\" Version=\"$(V)\" />\n  </ItemGroup>\n</Project>\n";
        _repo.Write("App.csproj", xml);
        var result = await Run();
        Assert.Equal(xml, _repo.Read("App.csproj"));
        Assert.Empty(result.Changes);
        Assert.Contains(result.Notes, n => n.Contains("$(V)") && n.Contains("shared"));
    }

    [Fact]
    public async Task Policy_ignore_pin_and_max_major_apply()
    {
        _repo.Settings.Packages.Ignore = ["Ignored.*"];
        _repo.Settings.Packages.Pins["Pinned.Pkg"] = "2.5.0";
        _repo.Settings.Packages.MaxMajor["Capped"] = 3;
        _source.Add("Ignored.One", "1.0.0", "5.0.0").Add("Capped", "2.0.0", "3.4.0", "4.0.0").Add("Pinned.Pkg", "1.0.0", "9.0.0");
        _repo.Write("App.csproj", Project("""
                <PackageReference Include="Ignored.One" Version="1.0.0" />
                <PackageReference Include="Capped" Version="2.0.0" />
                <PackageReference Include="Pinned.Pkg" Version="1.0.0" />
            """));
        var result = await Run();
        var text = _repo.Read("App.csproj");
        Assert.Contains("Include=\"Ignored.One\" Version=\"1.0.0\"", text);
        Assert.Contains("Include=\"Capped\" Version=\"3.4.0\"", text);
        Assert.Contains("Include=\"Pinned.Pkg\" Version=\"2.5.0\"", text);
        Assert.DoesNotContain("Ignored.One", _source.Requests);
        Assert.DoesNotContain("Pinned.Pkg", _source.Requests);
        Assert.Contains(result.Notes, n => n.Contains("Ignored.One") && n.Contains("ignored"));
        Assert.Contains(result.Notes, n => n.Contains("Capped") && n.Contains("max major 3"));
        Assert.Contains(result.Notes, n => n.Contains("Pinned.Pkg") && n.Contains("pinned"));
    }

    [Fact]
    public async Task Track_target_major_uses_target_framework_setting()
    {
        _repo.Settings.TargetFramework = "net9.0";
        _source.Add("Microsoft.Extensions.Logging", "8.0.0", "9.0.4", "10.0.0");
        _repo.Write("App.csproj", Project("    <PackageReference Include=\"Microsoft.Extensions.Logging\" Version=\"8.0.0\" />"));
        await Run();
        Assert.Contains("Version=\"9.0.4\"", _repo.Read("App.csproj"));
    }

    [Fact]
    public async Task Lookup_failure_keeps_version_and_notes_without_failing()
    {
        _source.Fail("Broken").Add("Good", "1.0.0", "1.1.0");
        _repo.Write("App.csproj", Project("    <PackageReference Include=\"Broken\" Version=\"1.0.0\" />\n    <PackageReference Include=\"Good\" Version=\"1.0.0\" />"));
        var result = await Run();
        var text = _repo.Read("App.csproj");
        Assert.Contains("Include=\"Broken\" Version=\"1.0.0\"", text);
        Assert.Contains("Include=\"Good\" Version=\"1.1.0\"", text);
        Assert.Contains(result.Notes, n => n.Contains("Broken") && n.Contains("boom"));
        Assert.Single(result.Changes);
    }

    [Fact]
    public async Task Package_with_no_versions_is_noted()
    {
        _repo.Write("App.csproj", Project("    <PackageReference Include=\"Private.Pkg\" Version=\"1.0.0\" />"));
        var result = await Run();
        Assert.Empty(result.Changes);
        Assert.Contains(result.Notes, n => n.Contains("Private.Pkg") && n.Contains("No versions"));
    }

    [Fact]
    public async Task Disabled_policy_skips_everything()
    {
        _repo.Settings.Packages.Enabled = false;
        _source.Add("A", "1.0.0", "2.0.0");
        var xml = Project("    <PackageReference Include=\"A\" Version=\"1.0.0\" />");
        _repo.Write("App.csproj", xml);
        var result = await Run();
        Assert.Empty(result.Changes);
        Assert.Empty(_source.Requests);
        Assert.Equal(xml, _repo.Read("App.csproj"));
    }

    [Fact]
    public async Task Same_package_in_many_projects_is_looked_up_once_and_each_file_updated()
    {
        _source.Add("A", "1.0.0", "2.0.0");
        _repo.Write("x/X.csproj", Project("    <PackageReference Include=\"A\" Version=\"1.0.0\" />"));
        _repo.Write("y/Y.csproj", Project("    <PackageReference Include=\"A\" Version=\"1.0.0\" />"));
        var result = await Run();
        Assert.Single(_source.Requests);
        Assert.Equal(2, result.Changes.Count);
    }

    [Fact]
    public async Task Is_idempotent()
    {
        _source.Add("A", "1.0.0", "2.0.0");
        _repo.Write("App.csproj", Project("    <PackageReference Include=\"A\" Version=\"1.0.0\" />"));
        await Run();
        var after = _repo.Read("App.csproj");
        var second = await Run();
        Assert.Empty(second.Changes);
        Assert.Equal(after, _repo.Read("App.csproj"));
    }

    [Fact]
    public async Task Never_downgrades_and_ignores_prereleases_by_default()
    {
        _source.Add("A", "1.0.0", "2.0.0-beta.1").Add("B", "3.0.0", "2.0.0");
        var xml = Project("    <PackageReference Include=\"A\" Version=\"1.0.0\" />\n    <PackageReference Include=\"B\" Version=\"3.0.0\" />");
        _repo.Write("App.csproj", xml);
        var result = await Run();
        Assert.Empty(result.Changes);
        Assert.Equal(xml, _repo.Read("App.csproj"));
    }

    [Fact]
    public async Task Ignores_packages_in_bin_and_obj()
    {
        _source.Add("A", "1.0.0", "2.0.0");
        _repo.Write("obj/Tmp.csproj", Project("    <PackageReference Include=\"A\" Version=\"1.0.0\" />"));
        var result = await Run();
        Assert.Empty(result.Changes);
    }
}
