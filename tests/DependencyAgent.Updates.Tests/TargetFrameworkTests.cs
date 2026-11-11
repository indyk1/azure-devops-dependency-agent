using DependencyAgent.Core.Model;
using DependencyAgent.Updates.Steps;

namespace DependencyAgent.Updates.Tests;

public class TargetFrameworkMapperTests
{
    [Theory]
    [InlineData("net5.0", "net10.0")]
    [InlineData("net6.0", "net10.0")]
    [InlineData("net7.0", "net10.0")]
    [InlineData("net8.0", "net10.0")]
    [InlineData("net9.0", "net10.0")]
    [InlineData("netcoreapp3.1", "net10.0")]
    [InlineData("netcoreapp3.0", "net10.0")]
    [InlineData("netcoreapp2.1", "net10.0")]
    [InlineData("netcoreapp2.2", "net10.0")]
    [InlineData("net8.0-windows", "net10.0-windows")]
    [InlineData("net6.0-android", "net10.0-android")]
    [InlineData("net8.0-android34.0", "net10.0-android")]
    [InlineData("net8.0-windows10.0.19041.0", "net10.0-windows")]
    [InlineData("NET8.0", "net10.0")]
    public void Maps_modern_monikers(string input, string expected)
    {
        var r = TargetFrameworkMapper.Map(input, 10, 0);
        Assert.True(r.Changed);
        Assert.Equal(expected, r.Value);
    }

    [Theory]
    [InlineData("net10.0")]
    [InlineData("net10.0-windows")]
    [InlineData("net11.0")]
    [InlineData("net12.0-android")]
    [InlineData("netstandard2.0")]
    [InlineData("netstandard2.1")]
    [InlineData("net48")]
    [InlineData("net472")]
    [InlineData("net462")]
    [InlineData("net35")]
    [InlineData("uap10.0")]
    public void Leaves_other_monikers_alone(string input)
    {
        var r = TargetFrameworkMapper.Map(input, 10, 0);
        Assert.False(r.Changed);
        Assert.Equal(input, r.Value);
    }

    [Fact]
    public void Platform_version_drop_adds_note()
    {
        Assert.Contains("platform version", TargetFrameworkMapper.Map("net8.0-android34.0", 10, 0).Note);
        Assert.Null(TargetFrameworkMapper.Map("net8.0-windows", 10, 0).Note);
    }

    [Fact]
    public void Old_netcoreapp_adds_note()
    {
        Assert.NotNull(TargetFrameworkMapper.Map("netcoreapp2.1", 10, 0).Note);
        Assert.Null(TargetFrameworkMapper.Map("netcoreapp3.1", 10, 0).Note);
    }

    [Fact]
    public void Targets_other_majors()
    {
        Assert.Equal("net12.0", TargetFrameworkMapper.Map("net8.0", 12, 0).Value);
        Assert.False(TargetFrameworkMapper.Map("net12.0", 12, 0).Changed);
    }
}

public class TargetFrameworkUpdateStepTests : IDisposable
{
    private readonly TestRepo _repo = new();
    private readonly TargetFrameworkUpdateStep _step = new();

    public void Dispose() => _repo.Dispose();

    private const string SdkProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>{0}</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    private Task<UpdateStepResult> Run() => _step.ApplyAsync(_repo.Context(), CancellationToken.None);

    [Fact]
    public async Task Updates_single_target_and_records_change()
    {
        _repo.Write("src/App/App.csproj", string.Format(SdkProject, "net8.0"));
        var result = await Run();

        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", _repo.Read("src/App/App.csproj"));
        var change = Assert.Single(result.Changes);
        Assert.Equal(new ChangeRecord(ChangeKind.TargetFramework, "src/App/App.csproj", "App.csproj", "net8.0", "net10.0"), change);
    }

    [Fact]
    public async Task Is_idempotent()
    {
        _repo.Write("App.csproj", string.Format(SdkProject, "net6.0"));
        await Run();
        var after = _repo.Read("App.csproj");
        var second = await Run();

        Assert.Empty(second.Changes);
        Assert.Equal(after, _repo.Read("App.csproj"));
    }

    [Fact]
    public async Task Preserves_os_suffix()
    {
        _repo.Write("App.csproj", string.Format(SdkProject, "net8.0-windows"));
        await Run();
        Assert.Contains(">net10.0-windows<", _repo.Read("App.csproj"));
    }

    [Fact]
    public async Task Drops_platform_version_with_note()
    {
        _repo.Write("App.csproj", string.Format(SdkProject, "net8.0-android34.0"));
        var result = await Run();
        Assert.Contains(">net10.0-android<", _repo.Read("App.csproj"));
        Assert.Contains(result.Notes, n => n.Contains("platform version"));
    }

    [Theory]
    [InlineData("net11.0")]
    [InlineData("net10.0")]
    [InlineData("netstandard2.0")]
    public async Task Never_touches_newer_or_netstandard(string tfm)
    {
        _repo.Write("App.csproj", string.Format(SdkProject, tfm));
        var before = _repo.Read("App.csproj");
        var result = await Run();
        Assert.Empty(result.Changes);
        Assert.Equal(before, _repo.Read("App.csproj"));
    }

    [Fact]
    public async Task Multi_targeting_replaces_modern_dedupes_and_keeps_order()
    {
        _repo.Write("Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>netstandard2.0;net6.0;net8.0;net48</TargetFrameworks>
              </PropertyGroup>
            </Project>
            """);
        var result = await Run();
        Assert.Contains("<TargetFrameworks>netstandard2.0;net10.0;net48</TargetFrameworks>", _repo.Read("Lib.csproj"));
        Assert.Equal("netstandard2.0;net6.0;net8.0;net48", Assert.Single(result.Changes).From);
    }

    [Fact]
    public async Task Multi_targeting_netframework_and_modern_is_updated_without_skip_note()
    {
        _repo.Write("Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net48;net8.0-windows</TargetFrameworks>
              </PropertyGroup>
            </Project>
            """);
        var result = await Run();
        Assert.Contains(">net48;net10.0-windows<", _repo.Read("Lib.csproj"));
        Assert.DoesNotContain(result.Notes, n => n.Contains("Skipped .NET Framework"));
    }

    [Fact]
    public async Task Property_references_are_not_edited_and_noted()
    {
        _repo.Write("App.csproj", string.Format(SdkProject, "$(MyTfm)"));
        var before = _repo.Read("App.csproj");
        var result = await Run();
        Assert.Empty(result.Changes);
        Assert.Equal(before, _repo.Read("App.csproj"));
        Assert.Contains(result.Notes, n => n.Contains("property reference"));
    }

    [Fact]
    public async Task Directory_build_props_is_edited_once_and_projects_inheriting_are_left_alone()
    {
        _repo.Write("Directory.Build.props", """
            <Project>
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        _repo.Write("a/A.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        _repo.Write("b/B.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var result = await Run();

        Assert.Contains(">net10.0<", _repo.Read("Directory.Build.props"));
        var change = Assert.Single(result.Changes);
        Assert.Equal("Directory.Build.props", change.File);
    }

    [Fact]
    public async Task Legacy_non_sdk_project_is_skipped_with_note()
    {
        const string legacy = """
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
              </PropertyGroup>
            </Project>
            """;
        _repo.Write("Old/Old.csproj", legacy);
        var result = await Run();
        Assert.Empty(result.Changes);
        Assert.Equal(legacy, _repo.Read("Old/Old.csproj"));
        Assert.Contains("Skipped .NET Framework project Old/Old.csproj (report only; migration is out of scope)", result.Notes);
    }

    [Fact]
    public async Task Sdk_style_netframework_only_project_is_skipped_with_note()
    {
        _repo.Write("Fx/Fx.csproj", string.Format(SdkProject, "net472"));
        var result = await Run();
        Assert.Empty(result.Changes);
        Assert.Contains(result.Notes, n => n.StartsWith("Skipped .NET Framework project Fx/Fx.csproj", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sdk_detected_through_import_and_sdk_element()
    {
        _repo.Write("A.csproj", """
            <Project>
              <Sdk Name="Microsoft.NET.Sdk" />
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        _repo.Write("B.csproj", """
            <Project>
              <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
              <PropertyGroup><TargetFramework>net7.0</TargetFramework></PropertyGroup>
              <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
            </Project>
            """);
        var result = await Run();
        Assert.Equal(2, result.Changes.Count);
    }

    [Fact]
    public async Task Old_runtime_framework_version_is_noted_not_removed()
    {
        _repo.Write("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net6.0</TargetFramework>
                <RuntimeFrameworkVersion>6.0.5</RuntimeFrameworkVersion>
              </PropertyGroup>
            </Project>
            """);
        var result = await Run();
        Assert.Contains("<RuntimeFrameworkVersion>6.0.5</RuntimeFrameworkVersion>", _repo.Read("App.csproj"));
        Assert.Contains(result.Notes, n => n.Contains("RuntimeFrameworkVersion 6.0.5"));
    }

    [Fact]
    public async Task Preserves_crlf_bom_declaration_and_other_formatting()
    {
        var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Project Sdk=\"Microsoft.NET.Sdk\">\r\n\r\n  <!-- keep -->\r\n  <PropertyGroup Label=\"x\">\r\n    <OutputType>Exe</OutputType>\r\n    <TargetFramework>net8.0</TargetFramework>\r\n    <Empty/>\r\n  </PropertyGroup>\r\n\r\n</Project>\r\n";
        _repo.Write("App.csproj", xml, bom: true);
        await Run();

        var bytes = _repo.ReadBytes("App.csproj");
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal(xml.Replace("net8.0", "net10.0", StringComparison.Ordinal), _repo.Read("App.csproj"));
    }

    [Fact]
    public async Task Preserves_lf_without_bom_and_handles_conditions_and_multiple_groups()
    {
        var xml = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup Condition=\"'$(A)'=='1'\">\n    <TargetFramework>net6.0</TargetFramework>\n  </PropertyGroup>\n  <PropertyGroup Condition=\"'$(A)'!='1'\">\n    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n";
        _repo.Write("App.csproj", xml);
        var result = await Run();
        Assert.Equal(2, result.Changes.Count);
        var text = _repo.Read("App.csproj");
        Assert.DoesNotContain('\r', text);
        Assert.Equal(xml.Replace("net6.0", "net10.0", StringComparison.Ordinal).Replace("net8.0", "net10.0", StringComparison.Ordinal), text);
    }

    [Fact]
    public async Task Skips_bin_obj_and_node_modules()
    {
        _repo.Write("bin/X/X.csproj", string.Format(SdkProject, "net8.0"));
        _repo.Write("node_modules/p/Y.csproj", string.Format(SdkProject, "net8.0"));
        var result = await Run();
        Assert.Empty(result.Changes);
    }

    [Fact]
    public async Task Honors_other_target_framework_setting()
    {
        _repo.Settings.TargetFramework = "net12.0";
        _repo.Write("App.csproj", string.Format(SdkProject, "net10.0"));
        var result = await Run();
        Assert.Contains(">net12.0<", _repo.Read("App.csproj"));
        Assert.Single(result.Changes);
    }
}
