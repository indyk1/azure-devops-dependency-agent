using DependencyAgent.Core.Model;
using DependencyAgent.Core.Processes;
using DependencyAgent.Verification;

namespace DependencyAgent.Verification.Tests;

public class GitChangeGuardTests : IAsyncLifetime, IDisposable
{
    private readonly TempDir _repo = new();
    private readonly GitChangeGuard _guard = new(new ProcessRunner());

    public async ValueTask InitializeAsync()
    {
        await Git.InitAsync(_repo.Path);
        _repo.Write("src/A.cs", "class A\n{\n}\n");
        _repo.Write("src/B.cs", "class B\n{\n}\n");
        _repo.Write("src/Authentication/Auth.cs", "class Auth\n{\n}\n");
        _repo.Write("src/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repo.Write("README.md", "hello\n");
        _repo.Write("appsettings.json", "{}\n");
        await Git.CommitAllAsync(_repo.Path, "base");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _repo.Dispose();

    private RepoContext Ctx(Action<DependencyAgent.Core.Configuration.AgentSettings>? configure = null) => TestContext.Create(_repo.Path, configure);

    private Task<GuardResult> EvalAsync(GuardScope scope = GuardScope.CodeFix, Action<DependencyAgent.Core.Configuration.AgentSettings>? configure = null) =>
        _guard.EvaluateAsync(Ctx(configure), "HEAD", scope, CancellationToken.None);

    [Fact]
    public async Task NoChanges_Passes()
    {
        var r = await EvalAsync();
        Assert.True(r.Passed);
        Assert.Empty(r.Files);
        Assert.Equal(0, r.ChangedLines);
    }

    [Fact]
    public async Task ModifiedAllowedFile_CountsLines()
    {
        _repo.Write("src/A.cs", "class A\n{\n    int x;\n}\n");
        var r = await EvalAsync();
        Assert.True(r.Passed);
        Assert.Equal(1, r.ChangedFiles);
        Assert.Equal(1, r.ChangedLines);
        Assert.Equal(["src/A.cs"], r.Files);
    }

    [Fact]
    public async Task StagedAndUnstagedChangesBothCounted_AndCommittedSinceRef()
    {
        var baseSha = (await Git.RunAsync(_repo.Path, "rev-parse", "HEAD")).Trim();
        _repo.Write("src/A.cs", "class A { }\n");
        await Git.CommitAllAsync(_repo.Path, "committed change");
        _repo.Write("src/B.cs", "class B { }\n");
        await Git.RunAsync(_repo.Path, "add", "src/B.cs");
        _repo.Write("src/C.cs", "class C { }\n"); // untracked

        var r = await _guard.EvaluateAsync(Ctx(), baseSha, GuardScope.CodeFix, CancellationToken.None);
        Assert.Equal(["src/A.cs", "src/B.cs", "src/C.cs"], r.Files);
        Assert.True(r.Passed);
        Assert.Equal(4 + 4 + 1, r.ChangedLines); // A and B: 3 removed + 1 added each, C: 1 added
    }

    [Fact]
    public async Task UntrackedFile_LinesCounted_BinaryIsZero_IgnoredFilesSkipped()
    {
        _repo.Write(".gitignore", "bin/\n");
        _repo.Write("bin/out.dll", "x");
        _repo.Write("src/New.cs", "a\nb\nc");
        File.WriteAllBytes(_repo.Combine("src/blob.bin"), [1, 2, 0, 3, 4]);

        var r = await EvalAsync(GuardScope.Budget);
        Assert.Contains("src/New.cs", r.Files);
        Assert.Contains("src/blob.bin", r.Files);
        Assert.DoesNotContain("bin/out.dll", r.Files);
        // .gitignore (1 line) + New.cs (3 lines, no trailing newline) + binary (0)
        Assert.Equal(4, r.ChangedLines);
    }

    [Fact]
    public async Task TrackedBinaryChange_CountsAsZeroLines()
    {
        File.WriteAllBytes(_repo.Combine("data.bin"), [0, 1, 2]);
        await Git.CommitAllAsync(_repo.Path);
        File.WriteAllBytes(_repo.Combine("data.bin"), [0, 9, 9, 9]);
        var r = await EvalAsync(GuardScope.Budget);
        Assert.Equal(["data.bin"], r.Files);
        Assert.Equal(0, r.ChangedLines);
    }

    [Fact]
    public async Task Rename_IsOneFile_AndReportsNewPath()
    {
        await Git.RunAsync(_repo.Path, "mv", "src/A.cs", "src/Renamed.cs");
        var r = await EvalAsync();
        Assert.Equal(["src/Renamed.cs"], r.Files);
        Assert.Equal(1, r.ChangedFiles);
        Assert.Equal(0, r.ChangedLines);
        Assert.True(r.Passed);
    }

    [Fact]
    public async Task RenameOutOfProtectedPath_IsViolation()
    {
        Directory.CreateDirectory(_repo.Combine("src/Other"));
        await Git.RunAsync(_repo.Path, "mv", "src/Authentication/Auth.cs", "src/Other/Auth.cs");
        var r = await EvalAsync();
        Assert.False(r.Passed);
        Assert.Contains(r.Violations, v => v.Contains("src/Authentication/Auth.cs") && v.Contains("protected"));
    }

    [Fact]
    public async Task PathsWithSpacesAndUnicode()
    {
        _repo.Write("src/Ünïcode dir/File ü.cs", "class X {}\n");
        _repo.Write("src/A.cs", "class A { int ü; }\n");
        var r = await EvalAsync();
        Assert.Contains("src/Ünïcode dir/File ü.cs", r.Files);
        Assert.Contains("src/A.cs", r.Files);
        Assert.True(r.Passed);
    }

    [Fact]
    public async Task DeletedFile_Counted()
    {
        File.Delete(_repo.Combine("src/B.cs"));
        var r = await EvalAsync();
        Assert.Equal(["src/B.cs"], r.Files);
        Assert.Equal(3, r.ChangedLines);
    }

    [Fact]
    public async Task BudgetExcludePatterns_AreNotCounted_ButListed()
    {
        _repo.Write("src/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n<PropertyGroup/>\n</Project>\n");
        _repo.Write("global.json", "{ }\n");
        var r = await EvalAsync(GuardScope.Budget, s => { s.Guards.MaxChangedFiles = 0; s.Guards.MaxChangedLines = 0; });
        Assert.True(r.Passed);
        Assert.Equal(0, r.ChangedFiles);
        Assert.Equal(0, r.ChangedLines);
        Assert.Contains("src/App.csproj", r.Files);
        Assert.Contains("global.json", r.Files);
    }

    [Fact]
    public async Task FileBudgetExceeded()
    {
        _repo.Write("src/A.cs", "x\n");
        _repo.Write("src/B.cs", "x\n");
        var r = await EvalAsync(GuardScope.Budget, s => s.Guards.MaxChangedFiles = 1);
        Assert.False(r.Passed);
        Assert.Contains(r.Violations, v => v.Contains("Changed files (2)") && v.Contains("1"));
    }

    [Fact]
    public async Task LineBudgetExceeded()
    {
        _repo.Write("src/A.cs", string.Concat(Enumerable.Repeat("line\n", 20)));
        var r = await EvalAsync(GuardScope.Budget, s => s.Guards.MaxChangedLines = 10);
        Assert.False(r.Passed);
        Assert.Contains(r.Violations, v => v.StartsWith("Changed lines"));
    }

    [Fact]
    public async Task BudgetScope_DoesNotCheckPatterns()
    {
        _repo.Write("README.md", "changed\n");
        _repo.Write("appsettings.json", "{ \"a\": 1 }\n");
        var r = await EvalAsync(GuardScope.Budget);
        Assert.True(r.Passed);
    }

    [Fact]
    public async Task CodeFix_DisallowedAndProtectedFiles_AreViolationsNamingTheRule()
    {
        _repo.Write("README.md", "changed\n");
        _repo.Write("appsettings.json", "{ \"a\": 1 }\n");
        _repo.Write("src/Authentication/Auth.cs", "class Auth { }\n");
        var r = await EvalAsync();
        Assert.False(r.Passed);
        Assert.Contains(r.Violations, v => v.Contains("README.md") && v.Contains("allowed edit patterns"));
        Assert.Contains(r.Violations, v => v.Contains("appsettings.json") && v.Contains("protected pattern"));
        Assert.Contains(r.Violations, v => v.Contains("src/Authentication/Auth.cs") && v.Contains("**/Authentication/**"));
        Assert.Equal(3, r.Files.Count);
    }

    [Fact]
    public async Task InvalidRef_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _guard.EvaluateAsync(Ctx(), "no-such-ref", GuardScope.Budget, CancellationToken.None));
    }

    [Fact]
    public async Task GitRunsWithoutSecrets()
    {
        var fake = new FakeProcessRunner(_ => FakeProcessRunner.Ok());
        await new GitChangeGuard(fake).EvaluateAsync(Ctx(), "HEAD", GuardScope.Budget, CancellationToken.None);
        Assert.NotEmpty(fake.Specs);
        Assert.All(fake.Specs, s => Assert.Equal(SecretScope.None, s.SecretScope));
    }

    // ---- RevertDisallowedAsync ----

    [Fact]
    public async Task Revert_ModifiedProtectedAndDisallowed_RestoredFromHead_AllowedKept()
    {
        _repo.Write("README.md", "changed\n");
        _repo.Write("appsettings.json", "{ \"a\": 1 }\n");
        await Git.RunAsync(_repo.Path, "add", "appsettings.json"); // staged change must be reverted too
        _repo.Write("src/A.cs", "class A { int x; }\n");

        var reverted = await _guard.RevertDisallowedAsync(Ctx(), CancellationToken.None);

        Assert.Equal(["README.md", "appsettings.json"], reverted);
        Assert.Equal("hello\n", File.ReadAllText(_repo.Combine("README.md")));
        Assert.Equal("{}\n", File.ReadAllText(_repo.Combine("appsettings.json")));
        Assert.Equal("class A { int x; }\n", File.ReadAllText(_repo.Combine("src/A.cs")));
        Assert.Equal("", (await Git.RunAsync(_repo.Path, "diff", "--cached", "--name-only")).Trim());
    }

    [Fact]
    public async Task Revert_DeletedProtectedFile_IsRestored()
    {
        File.Delete(_repo.Combine("appsettings.json"));
        var reverted = await _guard.RevertDisallowedAsync(Ctx(), CancellationToken.None);
        Assert.Equal(["appsettings.json"], reverted);
        Assert.True(File.Exists(_repo.Combine("appsettings.json")));
    }

    [Fact]
    public async Task Revert_UntrackedDisallowedFiles_AreDeleted_AndEmptyDirsRemoved()
    {
        _repo.Write("newdir/sub/notes.txt", "x");
        _repo.Write("src/Security/Key.cs", "class K {}\n");
        _repo.Write("src/Fine.cs", "class F {}\n");

        var reverted = await _guard.RevertDisallowedAsync(Ctx(), CancellationToken.None);

        Assert.Equal(["newdir/sub/notes.txt", "src/Security/Key.cs"], reverted);
        Assert.False(Directory.Exists(_repo.Combine("newdir")));
        Assert.False(Directory.Exists(_repo.Combine("src/Security")));
        Assert.True(File.Exists(_repo.Combine("src/Fine.cs")));
        Assert.True(Directory.Exists(_repo.Combine(".git")));
    }

    [Fact]
    public async Task Revert_StagedNewDisallowedFile_IsRemovedFromIndexAndDisk()
    {
        _repo.Write("notes.txt", "x");
        await Git.RunAsync(_repo.Path, "add", "notes.txt");
        var reverted = await _guard.RevertDisallowedAsync(Ctx(), CancellationToken.None);
        Assert.Equal(["notes.txt"], reverted);
        Assert.False(File.Exists(_repo.Combine("notes.txt")));
        Assert.Equal("", (await Git.RunAsync(_repo.Path, "status", "--porcelain")).Trim());
    }

    [Fact]
    public async Task Revert_StagedRenameToDisallowedPath_RestoresOriginalAndRemovesNew()
    {
        await Git.RunAsync(_repo.Path, "mv", "src/A.cs", "docs-A.txt");
        var reverted = await _guard.RevertDisallowedAsync(Ctx(), CancellationToken.None);
        Assert.Contains("src/A.cs", reverted);
        Assert.Contains("docs-A.txt", reverted);
        Assert.True(File.Exists(_repo.Combine("src/A.cs")));
        Assert.False(File.Exists(_repo.Combine("docs-A.txt")));
        Assert.Equal("", (await Git.RunAsync(_repo.Path, "status", "--porcelain")).Trim());
    }

    [Fact]
    public async Task Revert_RenameOfProtectedFileToAllowedPath_RestoresOriginal()
    {
        Directory.CreateDirectory(_repo.Combine("src/Other"));
        await Git.RunAsync(_repo.Path, "mv", "src/Authentication/Auth.cs", "src/Other/Auth.cs");
        await _guard.RevertDisallowedAsync(Ctx(), CancellationToken.None);
        Assert.True(File.Exists(_repo.Combine("src/Authentication/Auth.cs")));
        Assert.False(File.Exists(_repo.Combine("src/Other/Auth.cs")));
    }

    [Fact]
    public async Task Revert_UnstagedRenameAppearsAsDeleteAndUntracked_BothHandled()
    {
        File.Move(_repo.Combine("README.md"), _repo.Combine("README2.md"));
        var reverted = await _guard.RevertDisallowedAsync(Ctx(), CancellationToken.None);
        Assert.Equal(["README.md", "README2.md"], reverted);
        Assert.True(File.Exists(_repo.Combine("README.md")));
        Assert.False(File.Exists(_repo.Combine("README2.md")));
    }

    [Fact]
    public async Task Revert_PathWithSpacesUnicodeAndGlobChars()
    {
        _repo.Write("we ird/[x]*ü.txt", "x");
        _repo.Write("src/Fine.cs", "x");
        var reverted = await _guard.RevertDisallowedAsync(Ctx(), CancellationToken.None);
        Assert.Equal(["we ird/[x]*ü.txt"], reverted);
        Assert.False(File.Exists(_repo.Combine("we ird/[x]*ü.txt")));
    }

    [Fact]
    public async Task Revert_NothingToDo_ReturnsEmpty_AndLeavesGitAlone()
    {
        var before = await Git.RunAsync(_repo.Path, "rev-parse", "HEAD");
        Assert.Empty(await _guard.RevertDisallowedAsync(Ctx(), CancellationToken.None));
        Assert.Equal(before, await Git.RunAsync(_repo.Path, "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task Revert_CommittedChangesAreNotTouched()
    {
        _repo.Write("README.md", "committed change\n");
        await Git.CommitAllAsync(_repo.Path);
        Assert.Empty(await _guard.RevertDisallowedAsync(Ctx(), CancellationToken.None));
        Assert.Equal("committed change\n", File.ReadAllText(_repo.Combine("README.md")));
    }
}
