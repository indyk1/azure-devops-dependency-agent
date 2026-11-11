using System.Collections.Concurrent;
using DependencyAgent.Core;
using DependencyAgent.Core.Configuration;
using DependencyAgent.Core.Model;
using DependencyAgent.Core.Processes;

namespace DependencyAgent.AzureDevOps;

/// <summary>
/// <see cref="IGitWorkspace"/> backed by the git CLI. The token is only ever passed to network commands via
/// <c>GIT_CONFIG_*</c> environment variables (an <c>http.extraheader</c>), never in arguments, remote URLs or <c>.git/config</c>.
/// </summary>
public sealed class GitWorkspace : IGitWorkspace
{
    private static readonly TimeSpan CloneTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LocalTimeout = TimeSpan.FromMinutes(5);

    private readonly IProcessRunner _runner;
    private readonly AzureDevOpsConnection _connection;

    // Remote sha of the agent branch as seen by ls-remote (null = branch absent), per working directory. Used as push lease.
    private readonly ConcurrentDictionary<string, string?> _seenRemoteShas = new(StringComparer.Ordinal);

    public GitWorkspace(IProcessRunner runner, AzureDevOpsConnection connection)
    {
        _runner = runner;
        _connection = connection;
    }

    public async Task CloneAsync(RepositoryEntry repository, string destination, CancellationToken cancellationToken)
    {
        var full = Path.GetFullPath(destination);
        var parent = Path.GetDirectoryName(full) ?? ".";
        Directory.CreateDirectory(parent);
        var url = _connection.GetGitUrl(repository.Project, repository.Repository);
        await GitAsync(parent, ["clone", "--no-tags", "--single-branch", "--branch", repository.BaseBranch, url, full], network: true, CloneTimeout, cancellationToken);
    }

    public async Task CheckoutAgentBranchAsync(RepoContext context, CancellationToken cancellationToken)
    {
        var dir = context.WorkingDirectory;
        var hooks = Directory.CreateTempSubdirectory("dependency-agent-hooks-").FullName;
        var identity = context.Settings.GitIdentity;
        await GitAsync(dir, ["config", "user.name", identity.Name], false, LocalTimeout, cancellationToken);
        await GitAsync(dir, ["config", "user.email", identity.Email], false, LocalTimeout, cancellationToken);
        await GitAsync(dir, ["config", "core.hooksPath", hooks], false, LocalTimeout, cancellationToken);
        await GitAsync(dir, ["config", "commit.gpgsign", "false"], false, LocalTimeout, cancellationToken);
        await GitAsync(dir, ["checkout", "-B", context.Settings.BranchName, $"origin/{context.Entry.BaseBranch}"], false, LocalTimeout, cancellationToken);
    }

    public async Task<string> GetHeadAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        var result = await GitAsync(workingDirectory, ["rev-parse", "HEAD"], false, LocalTimeout, cancellationToken);
        return result.StandardOutput.Trim();
    }

    public async Task<string?> CommitAllAsync(RepoContext context, string message, CancellationToken cancellationToken)
    {
        var dir = context.WorkingDirectory;
        await GitAsync(dir, ["add", "-A"], false, LocalTimeout, cancellationToken);
        var staged = await GitAsync(dir, ["diff", "--cached", "--quiet"], false, LocalTimeout, cancellationToken, allowedExitCodes: [1]);
        if (staged.ExitCode == 0)
        {
            return null;
        }

        await GitAsync(dir, ["commit", "--no-verify", "-m", message], false, LocalTimeout, cancellationToken);
        return await GetHeadAsync(dir, cancellationToken);
    }

    public async Task<bool> RemoteBranchHasForeignCommitsAsync(RepoContext context, CancellationToken cancellationToken)
    {
        var dir = context.WorkingDirectory;
        var branch = context.Settings.BranchName;
        var remoteSha = await LsRemoteAsync(dir, branch, cancellationToken);
        _seenRemoteShas[Path.GetFullPath(dir)] = remoteSha;
        if (remoteSha is null)
        {
            return false;
        }

        await GitAsync(dir, ["fetch", "--no-tags", "origin", $"+refs/heads/{branch}:refs/remotes/origin/{branch}"], network: true, NetworkTimeout, cancellationToken);
        var log = await GitAsync(dir, ["log", "--format=%ae%n%ce", $"origin/{context.Entry.BaseBranch}..origin/{branch}"], false, LocalTimeout, cancellationToken);
        var agentEmail = context.Settings.GitIdentity.Email.Trim();
        return log.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(email => !string.Equals(email, agentEmail, StringComparison.OrdinalIgnoreCase));
    }

    public async Task PushAsync(RepoContext context, CancellationToken cancellationToken)
    {
        var dir = context.WorkingDirectory;
        var key = Path.GetFullPath(dir);
        var branch = context.Settings.BranchName;
        if (!_seenRemoteShas.TryGetValue(key, out var expected))
        {
            expected = await LsRemoteAsync(dir, branch, cancellationToken);
        }

        await GitAsync(
            dir,
            ["push", $"--force-with-lease=refs/heads/{branch}:{expected ?? string.Empty}", "origin", $"HEAD:refs/heads/{branch}"],
            network: true,
            NetworkTimeout,
            cancellationToken);
        _seenRemoteShas.TryRemove(key, out _);
    }

    private async Task<string?> LsRemoteAsync(string dir, string branch, CancellationToken cancellationToken)
    {
        var result = await GitAsync(dir, ["ls-remote", "--heads", "origin", branch], network: true, NetworkTimeout, cancellationToken);
        var wanted = $"refs/heads/{branch}";
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(['\t', ' '], 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[1] == wanted)
            {
                return parts[0];
            }
        }

        return null;
    }

    private async Task<ProcessResult> GitAsync(
        string workingDirectory,
        IReadOnlyList<string> args,
        bool network,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        int[]? allowedExitCodes = null)
    {
        var env = new Dictionary<string, string?>();
        if (network)
        {
            env["GIT_CONFIG_COUNT"] = "1";
            env["GIT_CONFIG_KEY_0"] = "http.extraheader";
            env["GIT_CONFIG_VALUE_0"] = $"AUTHORIZATION: bearer {_connection.Token}";
        }
        else
        {
            // Local-only commands never get credentials, even if the host environment carried a GIT_CONFIG_* override.
            env["GIT_CONFIG_COUNT"] = null;
            env["GIT_CONFIG_KEY_0"] = null;
            env["GIT_CONFIG_VALUE_0"] = null;
        }

        var result = await _runner.RunAsync(
            new ProcessSpec
            {
                FileName = "git",
                Arguments = args,
                WorkingDirectory = workingDirectory,
                SecretScope = SecretScope.None,
                Environment = env,
                MaskValues = [_connection.Token],
                Timeout = timeout,
            },
            cancellationToken);

        if (result.Succeeded || (!result.TimedOut && allowedExitCodes is not null && allowedExitCodes.Contains(result.ExitCode)))
        {
            return result;
        }

        var detail = Mask((string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError).Trim());
        throw new InvalidOperationException(result.TimedOut
            ? $"git {args[0]} timed out after {timeout.TotalMinutes:0} minutes. {detail}"
            : $"git {args[0]} failed with exit code {result.ExitCode}: {detail}");
    }

    private string Mask(string text) => text.Replace(_connection.Token, "***", StringComparison.Ordinal);
}
