using DependencyAgent.Core.Configuration;

namespace DependencyAgent.Core.Tests;

public class GlobMatcherTests
{
    [Theory]
    [InlineData("**/*.cs", "Program.cs", true)]
    [InlineData("**/*.cs", "src/App/Program.cs", true)]
    [InlineData("**/*.cs", "src/App/Program.csproj", false)]
    [InlineData("*.cs", "Program.cs", true)]
    [InlineData("*.cs", "src/Program.cs", false)]
    [InlineData("src/*.cs", "src/a/b.cs", false)]
    [InlineData("src/**/*.cs", "src/b.cs", true)]
    [InlineData("src/**/*.cs", "src/a/b/c.cs", true)]
    [InlineData("src/**", "src/a/b/c.cs", true)]
    [InlineData("src/**", "other/a.cs", false)]
    [InlineData("**/Security/**", "src/Security/Hasher.cs", true)]
    [InlineData("**/Security/**", "Security/Hasher.cs", true)]
    [InlineData("**/Security/**", "src/Insecurity/Hasher.cs", false)]
    [InlineData("**/Security/**", "src/MySecurity/Hasher.cs", false)]
    [InlineData("**/Dockerfile", "Dockerfile", true)]
    [InlineData("**/Dockerfile", "src/Api/Dockerfile", true)]
    [InlineData("**/Dockerfile.*", "Dockerfile.prod", true)]
    [InlineData("**/appsettings*.json", "src/appsettings.Production.json", true)]
    [InlineData("**/appsettings*.json", "appsettings.json", true)]
    [InlineData(".git/**", ".git/config", true)]
    [InlineData(".git/**", "src/.git/config", false)]
    [InlineData("**/.dependency-agent.json", ".dependency-agent.json", true)]
    public void Matches_double_star_and_single_star(string pattern, string path, bool expected) =>
        Assert.Equal(expected, GlobMatcher.IsMatch(pattern, path));

    [Theory]
    [InlineData("Microsoft.Extensions.*", "Microsoft.Extensions.Hosting", true)]
    [InlineData("Microsoft.Extensions.*", "Microsoft.Extensions", false)]
    [InlineData("Microsoft.EntityFrameworkCore*", "Microsoft.EntityFrameworkCore", true)]
    [InlineData("Microsoft.EntityFrameworkCore*", "Microsoft.EntityFrameworkCore.SqlServer", true)]
    [InlineData("System.*", "System.Text.Json", true)]
    [InlineData("System.*", "Newtonsoft.Json", false)]
    [InlineData("AutoMapper", "AutoMapper", true)]
    [InlineData("AutoMapper", "AutoMapper.Extensions", false)]
    public void Matches_package_ids(string pattern, string id, bool expected) =>
        Assert.Equal(expected, GlobMatcher.IsMatch(pattern, id));

    [Theory]
    [InlineData("file?.cs", "file1.cs", true)]
    [InlineData("file?.cs", "file12.cs", false)]
    [InlineData("file?.cs", "file.cs", false)]
    [InlineData("a?b", "a/b", false)]
    public void Question_mark_matches_exactly_one_non_separator(string pattern, string path, bool expected) =>
        Assert.Equal(expected, GlobMatcher.IsMatch(pattern, path));

    [Theory]
    [InlineData("**/*.CS", "src/Program.cs")]
    [InlineData("**/security/**", "src/SECURITY/x.cs")]
    [InlineData("MICROSOFT.extensions.*", "Microsoft.Extensions.Http")]
    public void Matching_is_case_insensitive(string pattern, string path) =>
        Assert.True(GlobMatcher.IsMatch(pattern, path));

    [Theory]
    [InlineData("**/Security/**", "src\\Security\\Hasher.cs")]
    [InlineData("src/**/*.cs", "src\\a\\b.cs")]
    [InlineData("src\\**\\*.cs", "src/a/b.cs")]
    public void Backslashes_are_normalised(string pattern, string path) =>
        Assert.True(GlobMatcher.IsMatch(pattern, path));

    [Theory]
    [InlineData("**/*.cs", "./src/a.cs")]
    [InlineData("src/*.cs", "./src/a.cs")]
    [InlineData("src/*.cs", "././src/a.cs")]
    [InlineData("./src/*.cs", "src/a.cs")]
    [InlineData("src/*.cs", "/src/a.cs")]
    public void Dot_slash_prefix_and_leading_slash_are_ignored(string pattern, string path) =>
        Assert.True(GlobMatcher.IsMatch(pattern, path));

    [Fact]
    public void Regex_metacharacters_in_patterns_are_literal()
    {
        Assert.True(GlobMatcher.IsMatch("a.b+c(1).cs", "a.b+c(1).cs"));
        Assert.False(GlobMatcher.IsMatch("a.b.cs", "aXb.cs"));
        Assert.False(GlobMatcher.IsMatch("[abc].cs", "a.cs"));
    }

    [Fact]
    public void IsMatchAny_checks_all_patterns()
    {
        Assert.True(GlobMatcher.IsMatchAny(["**/*.pfx", "**/*.snk"], "keys/app.SNK"));
        Assert.False(GlobMatcher.IsMatchAny(["**/*.pfx", "**/*.snk"], "keys/app.cs"));
        Assert.False(GlobMatcher.IsMatchAny([], "anything"));
    }

    [Theory]
    [InlineData(".\\src\\a.cs", "src/a.cs")]
    [InlineData("/src/a.cs", "src/a.cs")]
    [InlineData("src/a.cs", "src/a.cs")]
    public void Normalize_produces_forward_slash_relative_paths(string input, string expected) =>
        Assert.Equal(expected, GlobMatcher.Normalize(input));
}
