using DependencyAgent.Core.Model;
using DependencyAgent.Verification;

namespace DependencyAgent.Verification.Tests;

public class MsBuildDiagnosticParserTests
{
    private const string Root = "/work/repo";

    private static IReadOnlyList<BuildDiagnostic> Parse(params string[] lines) => MsBuildDiagnosticParser.Parse(lines, Root);

    [Fact]
    public void Cs0618_WarningWithProjectAndTfmDecoration()
    {
        var d = Assert.Single(Parse("/work/repo/src/App/Program.cs(12,34): warning CS0618: 'Foo.Old()' is obsolete: 'Use New' [/work/repo/src/App/App.csproj::TargetFramework=net10.0]"));
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Equal("CS0618", d.Code);
        Assert.Equal("'Foo.Old()' is obsolete: 'Use New'", d.Message);
        Assert.Equal("src/App/Program.cs", d.File);
        Assert.Equal(12, d.Line);
        Assert.Equal(34, d.Column);
        Assert.Equal("src/App/App.csproj", d.Project);
    }

    [Fact]
    public void Syslib0060_Warning()
    {
        var d = Assert.Single(Parse("/work/repo/Crypto.cs(5,9): warning SYSLIB0060: 'Rfc2898DeriveBytes.Rfc2898DeriveBytes(string, byte[], int)' is obsolete: 'The default hash algorithm and iteration counts in Rfc2898DeriveBytes constructors are outdated and insecure. Use a constructor that accepts the hash algorithm and the number of iterations.' (https://aka.ms/dotnet-warnings/SYSLIB0060) [/work/repo/Crypto.csproj]"));
        Assert.Equal("SYSLIB0060", d.Code);
        Assert.EndsWith("(https://aka.ms/dotnet-warnings/SYSLIB0060)", d.Message);
        Assert.Equal("Crypto.cs", d.File);
        Assert.Equal("Crypto.csproj", d.Project);
    }

    [Fact]
    public void Aspdepr004_Warning()
    {
        var d = Assert.Single(Parse("/work/repo/Web/Program.cs(8,1): warning ASPDEPR004: WebHost is obsolete. Use HostBuilder instead. [/work/repo/Web/Web.csproj]"));
        Assert.Equal("ASPDEPR004", d.Code);
        Assert.Equal("Web/Program.cs", d.File);
    }

    [Fact]
    public void Nu1903_ProjectLevelWarning_FileIsProject()
    {
        var d = Assert.Single(Parse("/work/repo/src/App/App.csproj : warning NU1903: Package 'System.Text.Json' 8.0.0 has a known high severity vulnerability, https://github.com/advisories/GHSA-8g4q-xg66-9fp4 [/work/repo/src/App/App.csproj]"));
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Equal("NU1903", d.Code);
        Assert.Equal("src/App/App.csproj", d.File);
        Assert.Equal("src/App/App.csproj", d.Project);
        Assert.Null(d.Line);
        Assert.StartsWith("Package 'System.Text.Json' 8.0.0", d.Message);
    }

    [Fact]
    public void Nu1605_ErrorWithoutProjectSuffix_UsesFileAsProject()
    {
        var d = Assert.Single(Parse("/work/repo/App.csproj : error NU1605: Warning As Error: Detected package downgrade: X from 2.0.0 to 1.0.0."));
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Equal("App.csproj", d.File);
        Assert.Equal("App.csproj", d.Project);
    }

    [Fact]
    public void Msb3277_FromSdkTargets_KeepsAbsoluteOutsidePath()
    {
        var d = Assert.Single(Parse("/usr/share/dotnet/sdk/10.0.115/Microsoft.Common.CurrentVersion.targets(2413,5): warning MSB3277: Found conflicts between different versions of \"X\" that could not be resolved. [/work/repo/App.csproj]"));
        Assert.Equal("MSB3277", d.Code);
        Assert.Equal("/usr/share/dotnet/sdk/10.0.115/Microsoft.Common.CurrentVersion.targets", d.File);
        Assert.Equal(2413, d.Line);
        Assert.Equal("App.csproj", d.Project);
    }

    [Fact]
    public void Cs0246_Error()
    {
        var d = Assert.Single(Parse("/work/repo/Lib/Class1.cs(3,7): error CS0246: The type or namespace name 'Foo' could not be found (are you missing a using directive or an assembly reference?) [/work/repo/Lib/Lib.csproj::TargetFramework=net10.0]"));
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Equal("CS0246", d.Code);
        Assert.Equal("Lib/Class1.cs", d.File);
        Assert.Equal("Lib/Lib.csproj", d.Project);
    }

    [Fact]
    public void ToolOrigins_CscAndMsbuild_HaveNoFile()
    {
        var all = Parse("CSC : error CS2001: Source file '/work/repo/Missing.cs' could not be found. [/work/repo/A.csproj]", "MSBUILD : error MSB1009: Project file does not exist.");
        Assert.Equal(2, all.Count);
        Assert.Null(all[0].File);
        Assert.Equal("A.csproj", all[0].Project);
        Assert.Equal("CS2001", all[0].Code);
        Assert.Null(all[1].File);
        Assert.Null(all[1].Project);
        Assert.Equal("MSB1009", all[1].Code);
    }

    [Fact]
    public void PathsWithSpaces()
    {
        var d = Assert.Single(Parse("/work/repo/My Project/Some File.cs(10,2): warning CS8618: Non-nullable property 'P' must contain a non-null value. [/work/repo/My Project/My Project.csproj]"));
        Assert.Equal("My Project/Some File.cs", d.File);
        Assert.Equal("My Project/My Project.csproj", d.Project);
    }

    [Fact]
    public void WindowsPaths_AreRelativizedAndNormalized()
    {
        var d = Assert.Single(MsBuildDiagnosticParser.Parse(
            [@"C:\a\1\s\src\Lib\Class1.cs(1,2): error CS1002: ; expected [C:\a\1\s\src\Lib\Lib.csproj::TargetFramework=net10.0]"], @"C:\a\1\s"));
        Assert.Equal("src/Lib/Class1.cs", d.File);
        Assert.Equal("src/Lib/Lib.csproj", d.Project);
        Assert.Equal(1, d.Line);
    }

    [Fact]
    public void WindowsPaths_CaseInsensitiveRoot()
    {
        var d = Assert.Single(MsBuildDiagnosticParser.Parse([@"c:\A\S\x.cs(1,1): warning CS0168: v [C:\a\s\x.csproj]"], @"C:\a\s"));
        Assert.Equal("x.cs", d.File);
    }

    [Fact]
    public void Duplicates_PerTfmAndRepeated_AreCollapsed()
    {
        var line = "/work/repo/A.cs(1,1): warning CS0618: 'X' is obsolete [/work/repo/A.csproj::TargetFramework={0}]";
        var all = Parse(string.Format(line, "net8.0"), string.Format(line, "net10.0"), string.Format(line, "net10.0"));
        Assert.Single(all);
    }

    [Fact]
    public void DifferentLines_AreNotDuplicates()
    {
        var all = Parse("/work/repo/A.cs(1,1): warning CS0618: 'X' is obsolete [/work/repo/A.csproj]", "/work/repo/A.cs(2,1): warning CS0618: 'X' is obsolete [/work/repo/A.csproj]");
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void NonDiagnosticLines_Ignored()
    {
        var all = Parse(
            "Build FAILED.",
            "    0 Warning(s)",
            "    1 Error(s)",
            "  App -> /work/repo/bin/App.dll",
            "/work/repo/A.cs(1,1): info CS8019: Unnecessary using directive.",
            "/work/repo/A.cs(1,1): hidden CS8019: Unnecessary using directive.",
            "Time Elapsed 00:00:01.23",
            "  error message in a test log without code",
            "");
        Assert.Empty(all);
    }

    [Fact]
    public void NodePrefixAndCarriageReturns_Handled()
    {
        var d = Assert.Single(Parse("  1>/work/repo/A.cs(1,1): warning CS0168: msg [/work/repo/A.csproj]\r"));
        Assert.Equal("A.cs", d.File);
    }

    [Fact]
    public void EndLineAndColumn_Range_IsAccepted()
    {
        var d = Assert.Single(Parse("/work/repo/A.cs(3,4,3,9): warning CS0168: msg [/work/repo/A.csproj]"));
        Assert.Equal(3, d.Line);
        Assert.Equal(4, d.Column);
    }

    [Fact]
    public void LineOnlyLocation()
    {
        var d = Assert.Single(Parse("/work/repo/Views/Index.cshtml(7): warning RZ1001: Something [/work/repo/Web.csproj]"));
        Assert.Equal(7, d.Line);
        Assert.Null(d.Column);
    }

    [Fact]
    public void RelativeFile_IsResolvedAgainstProjectDirectory()
    {
        var d = Assert.Single(Parse("Program.cs(1,1): warning CS0168: msg [/work/repo/src/App/App.csproj]"));
        Assert.Equal("src/App/Program.cs", d.File);
    }

    [Fact]
    public void MessageContainingBrackets_NotMistakenForProject()
    {
        var d = Assert.Single(Parse("/work/repo/A.cs(1,1): warning CS0618: 'X' is obsolete: '[Obsolete] attr' [/work/repo/A.csproj]"));
        Assert.Equal("'X' is obsolete: '[Obsolete] attr'", d.Message);
    }

    [Fact]
    public void Fingerprint_IgnoresLineNumbers()
    {
        var a = Parse("/work/repo/A.cs(1,1): warning CS0618: 'X' is obsolete [/work/repo/A.csproj]").Single();
        var b = Parse("/work/repo/A.cs(99,5): warning CS0618: 'X' is obsolete [/work/repo/A.csproj]").Single();
        Assert.Equal(a.Fingerprint, b.Fingerprint);
    }

    [Fact]
    public void MultilineOutput_StringOverload()
    {
        var output = "  Restored /work/repo/A.csproj (in 1 sec).\n/work/repo/A.cs(1,1): error CS1002: ; expected [/work/repo/A.csproj]\nBuild FAILED.\n";
        var all = MsBuildDiagnosticParser.Parse(output, Root);
        Assert.Single(all);
    }
}
