using DependencyAgent.Fixing.OpenAi;

namespace DependencyAgent.Fixing.Tests;

public class RepoFileToolsTests : IDisposable
{
    private readonly TempDir _repo = new();
    private readonly TempDir _outside = new();
    private readonly RepoFileTools _tools;

    public RepoFileToolsTests()
    {
        _repo.Write("src/A.cs", "class A\n{\n    void M() { }\n}\n");
        _repo.Write("src/Security/Secret.cs", "class Secret { }\n");
        _repo.Write("appsettings.json", "{}");
        _repo.Write(".git/config", "[core]");
        _repo.Write("src/obj/Gen.cs", "generated");
        _outside.Write("outside.cs", "class Outside { }");
        _tools = new RepoFileTools(_repo.Path, ["**/*.cs", "**/*.csproj"], ["**/Security/**", ".git/**"]);
    }

    public void Dispose()
    {
        _repo.Dispose();
        _outside.Dispose();
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("src/../../outside.cs")]
    [InlineData("..\\outside.cs")]
    [InlineData("")]
    public void Read_RejectsTraversalAndEmpty(string path) => Assert.Throws<ToolException>(() => _tools.ReadFile(path));

    [Fact]
    public void Read_RejectsAbsolutePathOutsideRepo() =>
        Assert.Throws<ToolException>(() => _tools.ReadFile(_outside.Combine("outside.cs")));

    [Fact]
    public void Read_AllowsAbsolutePathInsideRepo() =>
        Assert.Contains("class A", _tools.ReadFile(_repo.Combine("src/A.cs")));

    [Fact]
    public void Read_RejectsGitDirectory() => Assert.Throws<ToolException>(() => _tools.ReadFile(".git/config"));

    [Fact]
    public void Read_ReturnsNumberedLinesAndSupportsRanges()
    {
        var text = _tools.ReadFile("src/A.cs", 2, 3);
        Assert.Contains("2| {", text);
        Assert.Contains("3|     void M() { }", text);
        Assert.DoesNotContain("1| class A", text);
    }

    [Fact]
    public void Read_CapsOutputSize()
    {
        _repo.Write("src/Big.cs", string.Join('\n', Enumerable.Range(0, 20000).Select(i => "// line " + i)));
        var tools = new RepoFileTools(_repo.Path, ["**/*.cs"], [], maxReadBytes: 5000);
        var text = tools.ReadFile("src/Big.cs");
        Assert.True(text.Length < 6000);
        Assert.Contains("output truncated", text);
    }

    [Fact]
    public void Symlink_ToOutside_IsRejected()
    {
        if (!TryLink(_repo.Combine("src/link.cs"), _outside.Combine("outside.cs"), false))
        {
            return;
        }

        Assert.Throws<ToolException>(() => _tools.ReadFile("src/link.cs"));
        Assert.Throws<ToolException>(() => _tools.ReplaceInFile("src/link.cs", "Outside", "Changed"));
        Assert.Contains("class Outside", File.ReadAllText(_outside.Combine("outside.cs")));
    }

    [Fact]
    public void SymlinkedDirectory_ToOutside_IsRejected_AndNotListed()
    {
        if (!TryLink(_repo.Combine("src/escape"), _outside.Path, true))
        {
            return;
        }

        Assert.Throws<ToolException>(() => _tools.ReadFile("src/escape/outside.cs"));
        Assert.Throws<ToolException>(() => _tools.WriteFile("src/escape/new.cs", "x"));
        Assert.False(File.Exists(_outside.Combine("new.cs")));
        Assert.DoesNotContain("outside.cs", _tools.ListFiles("**/*.cs"));
    }

    [Fact]
    public void Symlink_InsideRepo_IsAllowed()
    {
        if (!TryLink(_repo.Combine("src/alias.cs"), _repo.Combine("src/A.cs"), false))
        {
            return;
        }

        Assert.Contains("class A", _tools.ReadFile("src/alias.cs"));
    }

    [Fact]
    public void ListFiles_FiltersByGlob_AndSkipsGitAndObj()
    {
        var all = _tools.ListFiles(null);
        Assert.Contains("src/A.cs", all);
        Assert.DoesNotContain(".git/config", all);
        Assert.DoesNotContain("src/obj/Gen.cs", all);
        Assert.DoesNotContain("appsettings.json", _tools.ListFiles("**/*.cs"));
        Assert.Contains("(no files matched)", _tools.ListFiles("**/*.vb"));
    }

    [Fact]
    public void ListFiles_CapsResults()
    {
        for (var i = 0; i < 30; i++)
        {
            _repo.Write($"many/F{i}.cs", "x");
        }

        var tools = new RepoFileTools(_repo.Path, ["**/*.cs"], [], maxListResults: 10);
        Assert.Contains("list truncated at 10", tools.ListFiles("many/*.cs"));
    }

    [Fact]
    public void Search_FindsMatches_WithLineNumbers_AndRespectsGlob()
    {
        var result = _tools.SearchText(@"void\s+M", "**/*.cs");
        Assert.Contains("src/A.cs:3:", result);
        Assert.Equal("(no matches)", _tools.SearchText("void", "**/*.csproj"));
    }

    [Fact]
    public void Search_InvalidRegex_IsToolError() => Assert.Throws<ToolException>(() => _tools.SearchText("(unclosed"));

    [Fact]
    public void Search_CatastrophicRegex_TimesOutAsToolError()
    {
        _repo.Write("src/Evil.cs", new string('a', 60) + "!");
        Assert.Throws<ToolException>(() => _tools.SearchText("^(a+)+$", "src/Evil.cs"));
    }

    [Fact]
    public void Replace_RequiresExactlyOneMatch()
    {
        _repo.Write("src/Dup.cs", "foo();\nfoo();\n");
        var ex = Assert.Throws<ToolException>(() => _tools.ReplaceInFile("src/Dup.cs", "foo();", "bar();"));
        Assert.Contains("2 places", ex.Message);
        Assert.Throws<ToolException>(() => _tools.ReplaceInFile("src/Dup.cs", "missing", "x"));
        Assert.Throws<ToolException>(() => _tools.ReplaceInFile("src/Dup.cs", "", "x"));

        _tools.ReplaceInFile("src/A.cs", "void M() { }", "void M() { Run(); }");
        Assert.Contains("void M() { Run(); }", _repo.Read("src/A.cs"));
    }

    [Fact]
    public void Replace_PreservesCrLfAndBom()
    {
        var path = _repo.Combine("src/Crlf.cs");
        File.WriteAllBytes(path, [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. "a\r\nb();\r\nc\r\n"u8.ToArray()]);
        _tools.ReplaceInFile("src/Crlf.cs", "a\nb();", "a\nB();");
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(0xEF, bytes[0]);
        Assert.Equal("a\r\nB();\r\nc\r\n", System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
    }

    [Theory]
    [InlineData("src/Security/Secret.cs")]
    [InlineData("appsettings.json")]
    [InlineData(".git/config")]
    [InlineData("README.md")]
    public void Writes_AreRefusedForProtectedOrDisallowedPaths(string path)
    {
        _repo.Write("README.md", "hello");
        var before = _repo.Read(path);
        Assert.Throws<ToolException>(() => _tools.WriteFile(path, "x"));
        Assert.Throws<ToolException>(() => _tools.ReplaceInFile(path, "e", "x"));
        Assert.Equal(before, _repo.Read(path));
    }

    [Fact]
    public void Write_RefusalMessageExplainsWhy()
    {
        var ex = Assert.Throws<ToolException>(() => _tools.WriteFile("src/Security/Secret.cs", "x"));
        Assert.Contains("protected", ex.Message);
        var ex2 = Assert.Throws<ToolException>(() => _tools.WriteFile("notes.txt", "x"));
        Assert.Contains("allowed edit patterns", ex2.Message);
    }

    [Fact]
    public void Write_CreatesNewAllowedFile_AndOverwritesExisting()
    {
        _tools.WriteFile("src/New/Thing.cs", "class Thing { }");
        Assert.Equal("class Thing { }", _repo.Read("src/New/Thing.cs"));
        _tools.WriteFile("src/A.cs", "class A2 { }");
        Assert.Equal("class A2 { }", _repo.Read("src/A.cs"));
    }

    private static bool TryLink(string link, string target, bool directory)
    {
        try
        {
            if (directory)
            {
                Directory.CreateSymbolicLink(link, target);
            }
            else
            {
                File.CreateSymbolicLink(link, target);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false; // symlinks unavailable (e.g. Windows without privileges)
        }
    }
}
