using System.Text;
using DependencyAgent.Core;
using DependencyAgent.Core.Configuration;
using DependencyAgent.Core.Model;
using DependencyAgent.Core.Processes;
using Microsoft.Extensions.Logging;

namespace DependencyAgent.Fixing;

/// <summary>
/// Orchestrates one AI fix iteration: picks the provider, builds prompts, runs it, then enforces guards
/// (<see cref="IChangeGuard.RevertDisallowedAsync"/>, test-tampering revert) and reports what remains changed.
/// </summary>
public sealed class AiFixer : ICodeFixer
{
    public const int MaxSummaryChars = 4000;
    private const long MaxRestorableFileBytes = 5_000_000;

    private readonly IReadOnlyList<IAiProvider> _providers;
    private readonly IChangeGuard _guard;
    private readonly IProcessRunner _runner;
    private readonly Func<RepoContext, ILearnDocsClient?> _docsFactory;
    private readonly Func<RepoContext, Func<CancellationToken, Task<VerificationResult>>?> _buildToolFactory;

    /// <summary>Optional extra Learn deny terms per repository (e.g. namespaces and project names extracted from the code).</summary>
    private readonly Func<RepoContext, IEnumerable<string>>? _extraDenyTerms;

    public AiFixer(
        IEnumerable<IAiProvider> providers,
        IChangeGuard guard,
        IProcessRunner runner,
        Func<RepoContext, ILearnDocsClient?> docsClientFactory,
        Func<RepoContext, Func<CancellationToken, Task<VerificationResult>>?> buildToolFactory,
        Func<RepoContext, IEnumerable<string>>? extraDenyTermsFactory = null)
    {
        _extraDenyTerms = extraDenyTermsFactory;
        _providers = providers.ToList();
        _guard = guard;
        _runner = runner;
        _docsFactory = docsClientFactory;
        _buildToolFactory = buildToolFactory;
    }

    public string Name => "ai";

    /// <summary>Resolves the provider name: the run option unless null/blank/"config", else the configured one.</summary>
    public static string ResolveProviderName(RepoContext context)
    {
        var over = context.Options.AiProviderOverride;
        return !string.IsNullOrWhiteSpace(over) && !over.Equals("config", StringComparison.OrdinalIgnoreCase)
            ? over.Trim()
            : (context.Settings.Ai.Provider ?? "none").Trim();
    }

    public async Task<FixResult> FixAsync(RepoContext context, FixRequest request, CancellationToken cancellationToken)
    {
        var settings = context.Settings;
        var ai = settings.Ai;
        if (!ai.Enabled)
        {
            return FixResult.None("AI fixing is disabled (ai.enabled = false).");
        }

        var providerName = ResolveProviderName(context);
        if (providerName.Length == 0 || providerName.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return FixResult.None("AI provider is 'none'.");
        }

        var provider = _providers.FirstOrDefault(p => p.Name.Equals(providerName, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            return FixResult.None($"Unknown AI provider '{providerName}'.");
        }

        if (provider.CheckAvailability(settings) is { } unavailable)
        {
            return FixResult.None($"AI provider '{provider.Name}' is unavailable: {unavailable}");
        }

        var root = context.WorkingDirectory;
        var before = await SnapshotAsync(context, cancellationToken);

        var notes = new List<string>();
        string? tempDir = null;
        AiRunResult? run = null;
        try
        {
            var learnProxy = BuildLearnProxyForRun(context, ref tempDir);
            var runRequest = new AiRunRequest
            {
                WorkingDirectory = root,
                SystemPrompt = PromptBuilder.BuildSystemPrompt(
                    settings.TargetFramework, settings.Guards.AllowedEditPatterns, settings.Guards.ProtectedPatterns,
                    docsToolsAvailable: learnProxy is not null || (ai.LearnMcp.Enabled && provider.Name.Equals("openai", StringComparison.OrdinalIgnoreCase))),
                TaskPrompt = PromptBuilder.BuildTaskPrompt(settings.TargetFramework, request, ai.MaxIterations),
                AllowedEditPatterns = settings.Guards.AllowedEditPatterns,
                ProtectedPatterns = settings.Guards.ProtectedPatterns,
                Model = ai.Model,
                Timeout = TimeSpan.FromMinutes(Math.Max(1, ai.TimeoutMinutes)),
                LearnProxyCommand = learnProxy,
                LearnDocs = ai.LearnMcp.Enabled ? _docsFactory(context) : null,
                BuildTool = _buildToolFactory(context),
            };

            try
            {
                run = await provider.RunAsync(runRequest, settings, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                context.Logger.LogWarning(ex, "AI provider {Provider} threw", provider.Name);
                run = new AiRunResult(false, "", $"{ex.GetType().Name}: {ex.Message}");
            }
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }

        // Guard: revert disallowed/protected files regardless of how the provider ended.
        var reverted = new List<string>(await _guard.RevertDisallowedAsync(context, cancellationToken));

        // Which files did this run change (vs. the state before the AI ran)?
        var after = await SnapshotAsync(context, cancellationToken);
        var changed = after.Where(kv => !before.TryGetValue(kv.Key, out var old) || old.Hash != kv.Value.Hash)
            .Select(kv => kv.Key).OrderBy(f => f, StringComparer.Ordinal).ToList();

        // Test tampering + suppression detection.
        var tamperNotes = new List<string>();
        var suppressionNotes = new List<string>();
        foreach (var file in changed.ToList())
        {
            var (diff, isNew) = await GetDiffAsync(context, file, after[file].Status, cancellationToken);
            var findings = isNew
                ? TestTamperingDetector.AnalyzeNewFile(file, diff)
                : TestTamperingDetector.Analyze(file, diff);
            if (findings.Count > 0)
            {
                await RevertFileAsync(context, file, before, cancellationToken);
                reverted.Add(file);
                changed.Remove(file);
                tamperNotes.AddRange(findings.Select(f => $"AI test tampering detected, file reverted: {f.File}: {f.Reason}"));
                continue;
            }

            var added = isNew
                ? TestTamperingDetector.FindAddedSuppressions(file, string.Join('\n', diff.Split('\n').Select(l => "+" + l.TrimEnd('\r'))))
                : TestTamperingDetector.FindAddedSuppressions(file, diff);
            suppressionNotes.AddRange(added.Select(s => $"AI warning: added warning suppression (not reverted) in {s}"));
        }

        var summary = run?.Summary ?? "";
        var firstLine = FirstLine(summary);
        var changes = changed
            .Select(f => new ChangeRecord(ChangeKind.AiFix, f, provider.Name, Detail: string.IsNullOrEmpty(firstLine) ? null : GitCli.Head(firstLine, 300)))
            .ToList();

        if (run is not null)
        {
            if (!string.IsNullOrWhiteSpace(summary))
            {
                notes.Add("AI summary: " + PipelineCommandNeutralizer.Neutralize(GitCli.Head(summary.Trim(), MaxSummaryChars)));
            }

            if (!run.Completed || run.Error is not null)
            {
                notes.Add($"AI provider '{provider.Name}' did not complete cleanly: {PipelineCommandNeutralizer.Neutralize(GitCli.Head(run.Error ?? "unknown error", 500))}");
            }

            if (run.ToolCalls > 0)
            {
                notes.Add($"AI provider '{provider.Name}' made {run.ToolCalls} tool call(s).");
            }
        }

        if (reverted.Count > 0)
        {
            notes.Add("AI reverted: " + string.Join(", ", reverted.Distinct(StringComparer.Ordinal).OrderBy(r => r, StringComparer.Ordinal)));
        }

        notes.AddRange(tamperNotes);
        notes.AddRange(suppressionNotes);
        if (changes.Count == 0)
        {
            notes.Add($"AI provider '{provider.Name}' left no changes.");
        }

        return new FixResult(changes.Count > 0, changes, notes);
    }

    /// <summary>
    /// <c>[agent, learn-proxy, --endpoint, E, --max-query-length, N, --deny-terms-file, F, --audit-log, L]</c>; a <c>.dll</c> agent is launched via <c>dotnet</c>.
    /// </summary>
    public static IReadOnlyList<string> BuildLearnProxyCommand(string agentExecutablePath, LearnMcpSettings learn, string denyTermsFile, string auditLogPath)
    {
        var command = new List<string>();
        if (agentExecutablePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            command.Add("dotnet");
        }

        command.Add(agentExecutablePath);
        command.AddRange([
            "learn-proxy",
            "--endpoint", learn.Endpoint,
            "--max-query-length", learn.MaxQueryLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--deny-terms-file", denyTermsFile,
            "--audit-log", auditLogPath,
        ]);
        return command;
    }

    /// <summary>Terms the proxy must never forward: repository name, project name and configured extras.</summary>
    public static IReadOnlyList<string> BuildDenyTerms(RepoContext context) =>
        new[] { context.Entry.Repository, context.Entry.Project }
            .Concat(context.Settings.Ai.LearnMcp.DenyTerms)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static string AuditLogPath(RepoContext context) =>
        Path.GetFullPath(Path.Combine(context.Options.OutputDirectory, $"{SafeName(context.Entry.Project)}__{SafeName(context.Entry.Repository)}.learn-audit.jsonl"));

    private static string SafeName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        }

        return sb.Length == 0 ? "_" : sb.ToString();
    }

    private IReadOnlyList<string>? BuildLearnProxyForRun(RepoContext context, ref string? tempDir)
    {
        var learn = context.Settings.Ai.LearnMcp;
        var exe = context.Options.AgentExecutablePath;
        if (!learn.Enabled || string.IsNullOrWhiteSpace(exe))
        {
            return null;
        }

        // Deny-terms file lives outside the repository so the AI cannot read or modify it through its file tools.
        tempDir = Path.Combine(Path.GetTempPath(), "depagent-learn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var denyFile = Path.Combine(tempDir, "deny-terms.txt");
        var denyTerms = BuildDenyTerms(context)
            .Concat(_extraDenyTerms?.Invoke(context) ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase);
        File.WriteAllLines(denyFile, denyTerms);
        var audit = AuditLogPath(context);
        Directory.CreateDirectory(Path.GetDirectoryName(audit)!);
        return BuildLearnProxyCommand(exe, learn, denyFile, audit);
    }

    private static void TryDeleteDirectory(string? dir)
    {
        if (dir is null)
        {
            return;
        }

        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    private sealed record FileState(string Status, string Hash, byte[]? Content);

    /// <summary>Snapshot of all uncommitted files (path → status + content hash + content for small files).</summary>
    private async Task<Dictionary<string, FileState>> SnapshotAsync(RepoContext context, CancellationToken ct)
    {
        var result = new Dictionary<string, FileState>(StringComparer.Ordinal);
        var status = await GitCli.RunAsync(_runner, context.WorkingDirectory, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], ct);
        if (!status.Succeeded)
        {
            return result;
        }

        foreach (var (st, path) in GitCli.ParseStatus(status.StandardOutput))
        {
            if (path.StartsWith(".git/", StringComparison.Ordinal))
            {
                continue;
            }

            var full = Path.Combine(context.WorkingDirectory, path);
            string hash;
            byte[]? content = null;
            if (File.Exists(full))
            {
                try
                {
                    hash = TextFileIo.Sha256(full);
                    if (new FileInfo(full).Length <= MaxRestorableFileBytes)
                    {
                        content = File.ReadAllBytes(full);
                    }
                }
                catch (IOException)
                {
                    hash = "<unreadable>";
                }
            }
            else
            {
                hash = "<deleted>";
            }

            result[path] = new FileState(st, hash, content);
        }

        return result;
    }

    /// <summary>Returns the diff text for a file (unified diff vs HEAD, or full content for untracked files).</summary>
    private async Task<(string Diff, bool IsNewFile)> GetDiffAsync(RepoContext context, string file, string status, CancellationToken ct)
    {
        if (status == "??")
        {
            var decoded = TextFileIo.TryRead(Path.Combine(context.WorkingDirectory, file));
            return (decoded?.Text ?? "", true);
        }

        var diff = await GitCli.RunAsync(_runner, context.WorkingDirectory, ["diff", "HEAD", "--no-color", "--no-ext-diff", "--no-textconv", "-U0", "--", file], ct);
        return (diff.StandardOutput, false);
    }

    /// <summary>Restores a tampered file to the state before the AI ran (or to HEAD / deleted when that is unknown).</summary>
    private async Task RevertFileAsync(RepoContext context, string file, Dictionary<string, FileState> before, CancellationToken ct)
    {
        var full = Path.Combine(context.WorkingDirectory, file);
        if (before.TryGetValue(file, out var prior) && prior.Content is not null)
        {
            File.WriteAllBytes(full, prior.Content);
            return;
        }

        if (before.TryGetValue(file, out var prior2) && prior2.Status == "??")
        {
            // Untracked before, but too large to snapshot — cannot restore; leave to the guard.
            return;
        }

        var tracked = await GitCli.RunAsync(_runner, context.WorkingDirectory, ["ls-files", "--error-unmatch", "--", file], ct);
        if (tracked.Succeeded)
        {
            await GitCli.RunAsync(_runner, context.WorkingDirectory, ["restore", "--source=HEAD", "--staged", "--worktree", "--", file], ct);
        }
        else if (File.Exists(full))
        {
            File.Delete(full);
        }
    }

    private static string FirstLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 0)
            {
                return PipelineCommandNeutralizer.Neutralize(t);
            }
        }

        return "";
    }
}
