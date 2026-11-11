using DependencyAgent.Core.Configuration;
using DependencyAgent.Core.Model;
using DependencyAgent.Core.Processes;
using static DependencyAgent.AzureDevOps.Tests.TestSupport;

namespace DependencyAgent.AzureDevOps.Tests;

public sealed class GitWorkspaceTests : IDisposable
{
    private const string Token = "s3cret-token-value";
    private const string Branch = "dependency-agent/net10-lts";

    private readonly TempDir _tmp = new();
    private readonly RecordingRunner _runner = new();
    private readonly GitWorkspace _git;
    private readonly string _origin;
    private readonly RepositoryEntry _entry = new() { Project = "P", Repository = "R", BaseBranch = "main" };

    public GitWorkspaceTests()
    {
        _origin = Path.Combine(_tmp.Path, "origin.git");
        Directory.CreateDirectory(_origin);
        Git(_origin, "init", "--bare", "-b", "main");

        var seed = Path.Combine(_tmp.Path, "seed");
        Directory.CreateDirectory(seed);
        Git(seed, "init", "-b", "main");
        File.WriteAllText(Path.Combine(seed, "README.md"), "hello\n");
        Git(seed, "add", "-A");
        Git(seed, "commit", "-m", "initial");
        Git(seed, "push", _origin, "main");

        var connection = new AzureDevOpsConnection("https://dev.azure.com/org/", Token, (_, _) => "file://" + _origin);
        _git = new GitWorkspace(_runner, connection);
    }

    public void Dispose() => _tmp.Dispose();

    private async Task<(RepoContext Ctx, string Dir)> CloneAndCheckoutAsync(string name = "clone")
    {
        var dir = Path.Combine(_tmp.Path, name);
        await _git.CloneAsync(_entry, dir, TestContext.Current.CancellationToken);
        var ctx = Context(dir);
        await _git.CheckoutAgentBranchAsync(ctx, TestContext.Current.CancellationToken);
        return (ctx, dir);
    }

    [Fact]
    public async Task Clone_and_checkout_configure_identity_hooks_and_never_store_the_token()
    {
        var (_, dir) = await CloneAndCheckoutAsync();

        Assert.Equal(Branch, Git(dir, "rev-parse", "--abbrev-ref", "HEAD"));
        Assert.Equal("Dependency Agent", Git(dir, "config", "user.name"));
        Assert.Equal("dependency-agent@noreply.local", Git(dir, "config", "user.email"));
        Assert.Equal("false", Git(dir, "config", "commit.gpgsign"));
        var hooks = Git(dir, "config", "core.hooksPath");
        Assert.True(Directory.Exists(hooks));
        Assert.Empty(Directory.GetFileSystemEntries(hooks));

        var config = File.ReadAllText(Path.Combine(dir, ".git", "config"));
        Assert.DoesNotContain(Token, config);
        Assert.DoesNotContain("extraheader", config);
        Assert.Contains("--no-tags", _runner.Specs.First(s => s.Arguments[0] == "clone").Arguments);
        Assert.Contains("--single-branch", _runner.Specs.First(s => s.Arguments[0] == "clone").Arguments);
    }

    [Fact]
    public async Task Token_is_only_passed_via_env_on_network_commands()
    {
        var (ctx, dir) = await CloneAndCheckoutAsync();
        File.WriteAllText(Path.Combine(dir, "a.txt"), "x");
        await _git.CommitAllAsync(ctx, "msg", TestContext.Current.CancellationToken);
        await _git.RemoteBranchHasForeignCommitsAsync(ctx, TestContext.Current.CancellationToken);
        await _git.PushAsync(ctx, TestContext.Current.CancellationToken);

        Assert.All(_runner.Specs, s =>
        {
            Assert.DoesNotContain(s.Arguments, a => a.Contains(Token));
            Assert.Equal(SecretScope.None, s.SecretScope);
            Assert.Contains(Token, s.MaskValues);
            var hasToken = s.Environment.GetValueOrDefault("GIT_CONFIG_VALUE_0")?.Contains(Token) == true;
            var network = s.Arguments[0] is "clone" or "fetch" or "ls-remote" or "push";
            Assert.Equal(network, hasToken);
            if (network)
            {
                Assert.Equal("1", s.Environment["GIT_CONFIG_COUNT"]);
                Assert.Equal("http.extraheader", s.Environment["GIT_CONFIG_KEY_0"]);
                Assert.Equal($"AUTHORIZATION: bearer {Token}", s.Environment["GIT_CONFIG_VALUE_0"]);
            }
        });
        Assert.DoesNotContain(Token, File.ReadAllText(Path.Combine(dir, ".git", "config")));
    }

    [Fact]
    public async Task CommitAll_returns_null_when_clean_and_sha_when_changed_without_running_hooks()
    {
        var (ctx, dir) = await CloneAndCheckoutAsync();
        Assert.Null(await _git.CommitAllAsync(ctx, "nothing", TestContext.Current.CancellationToken));

        var hook = Path.Combine(dir, ".git", "hooks", "pre-commit");
        File.WriteAllText(hook, "#!/bin/sh\nexit 1\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        File.WriteAllText(Path.Combine(dir, "new.txt"), "content");

        var sha = await _git.CommitAllAsync(ctx, "Upgrade", TestContext.Current.CancellationToken);

        Assert.NotNull(sha);
        Assert.Equal(sha, await _git.GetHeadAsync(dir, TestContext.Current.CancellationToken));
        Assert.Equal("dependency-agent@noreply.local", Git(dir, "log", "-1", "--format=%ae"));
        Assert.Equal("Upgrade", Git(dir, "log", "-1", "--format=%s"));
    }

    [Fact]
    public async Task Push_creates_branch_and_agent_only_commits_are_not_foreign()
    {
        var (ctx, dir) = await CloneAndCheckoutAsync();
        Assert.False(await _git.RemoteBranchHasForeignCommitsAsync(ctx, TestContext.Current.CancellationToken));
        File.WriteAllText(Path.Combine(dir, "a.txt"), "1");
        await _git.CommitAllAsync(ctx, "one", TestContext.Current.CancellationToken);
        await _git.PushAsync(ctx, TestContext.Current.CancellationToken);
        Assert.Equal(Git(dir, "rev-parse", "HEAD"), Git(_origin, "rev-parse", Branch));

        // Second night: fresh clone, branch exists with only agent commits -> not foreign, push over it.
        var (ctx2, dir2) = await CloneAndCheckoutAsync("clone2");
        Assert.False(await _git.RemoteBranchHasForeignCommitsAsync(ctx2, TestContext.Current.CancellationToken));
        File.WriteAllText(Path.Combine(dir2, "b.txt"), "2");
        await _git.CommitAllAsync(ctx2, "two", TestContext.Current.CancellationToken);
        await _git.PushAsync(ctx2, TestContext.Current.CancellationToken);
        Assert.Equal(Git(dir2, "rev-parse", "HEAD"), Git(_origin, "rev-parse", Branch));
    }

    [Fact]
    public async Task Foreign_commit_on_remote_branch_is_detected()
    {
        var (ctx, dir) = await CloneAndCheckoutAsync();
        File.WriteAllText(Path.Combine(dir, "a.txt"), "1");
        await _git.CommitAllAsync(ctx, "agent", TestContext.Current.CancellationToken);
        await _git.PushAsync(ctx, TestContext.Current.CancellationToken);

        var human = Path.Combine(_tmp.Path, "human");
        Git(_tmp.Path, "clone", "-b", Branch, _origin, human);
        File.WriteAllText(Path.Combine(human, "h.txt"), "human");
        Git(human, "add", "-A");
        Git(human, "commit", "-m", "human fix");
        Git(human, "push", "origin", Branch);

        var (ctx2, _) = await CloneAndCheckoutAsync("clone2");
        Assert.True(await _git.RemoteBranchHasForeignCommitsAsync(ctx2, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Push_is_rejected_when_remote_branch_moved_after_it_was_inspected()
    {
        var (ctx, dir) = await CloneAndCheckoutAsync();
        Assert.False(await _git.RemoteBranchHasForeignCommitsAsync(ctx, TestContext.Current.CancellationToken)); // branch absent -> lease "must not exist"
        File.WriteAllText(Path.Combine(dir, "a.txt"), "1");
        await _git.CommitAllAsync(ctx, "agent", TestContext.Current.CancellationToken);

        // A human creates the branch between inspection and push.
        var human = Path.Combine(_tmp.Path, "human");
        Git(_tmp.Path, "clone", _origin, human);
        File.WriteAllText(Path.Combine(human, "h.txt"), "human");
        Git(human, "add", "-A");
        Git(human, "commit", "-m", "human");
        Git(human, "push", "origin", $"HEAD:refs/heads/{Branch}");
        var humanSha = Git(_origin, "rev-parse", Branch);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _git.PushAsync(ctx, TestContext.Current.CancellationToken));
        Assert.Contains("git push failed", ex.Message);
        Assert.Equal(humanSha, Git(_origin, "rev-parse", Branch));
    }

    [Fact]
    public async Task Failure_messages_are_masked()
    {
        var connection = new AzureDevOpsConnection("https://dev.azure.com/org/", Token, (_, _) => "file://" + Path.Combine(_tmp.Path, "missing"));
        var git = new GitWorkspace(new LeakyRunner(), connection);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => git.CloneAsync(_entry, Path.Combine(_tmp.Path, "x"), TestContext.Current.CancellationToken));
        Assert.DoesNotContain(Token, ex.Message);
        Assert.Contains("***", ex.Message);
    }

    private sealed class RecordingRunner : IProcessRunner
    {
        private readonly ProcessRunner _inner = new();

        public List<ProcessSpec> Specs { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
        {
            Specs.Add(spec);
            return _inner.RunAsync(spec, cancellationToken);
        }
    }

    private sealed class LeakyRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessResult(128, "", $"fatal: unable to access with header AUTHORIZATION: bearer {Token}", false, TimeSpan.Zero));
    }
}
