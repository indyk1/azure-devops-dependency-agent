using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DependencyAgent.AzureDevOps;
using DependencyAgent.Core;
using DependencyAgent.Core.Configuration;
using DependencyAgent.Core.Model;
using DependencyAgent.Core.Processes;
using Microsoft.Extensions.Logging;

namespace DependencyAgent.Cli;

public static partial class Commands
{
    public static readonly IReadOnlySet<string> Flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dry-run", "help" };

    private static readonly string[] KnownProviders = ["copilot", "openai", "none"];

    private static readonly string[] ReasoningEfforts = ["none", "minimal", "low", "medium", "high", "xhigh", "max"];

    private static bool InPipeline => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TF_BUILD"));

    public static int Help(TextWriter output)
    {
        output.WriteLine("""
            dependency-agent — upgrades .NET repositories to the latest LTS and opens pull requests.

            Commands:
              plan             --config <dir> [--filter <filter>]
                               Select repositories and emit the pipeline matrix (output variables 'matrix', 'repoCount').
              run              --config <dir> --repository <Repo|Project/Repo> [--output <dir>] [--work-dir <dir>]
                               [--dry-run] [--ai-provider config|copilot|openai|none] [--collection-uri <url>]
                               Process one repository. Exit 0 = up to date / green / draft / skipped, 1 = failed.
              report           --input <dir> [--output <file.md>]
                               Aggregate per-repository reports into a run summary.
              validate-config  --config <dir>
              learn-proxy      [--endpoint <url>] [--max-query-length <n>] [--deny-terms-file <path>] [--audit-log <path>]
                               Sanitising stdio MCP proxy to Microsoft Learn (used by AI providers).

            All target repositories live in one Azure DevOps project: the top-level 'project' of repositories.json,
            or the pipeline's own project (SYSTEM_TEAMPROJECT). '--repository' takes 'Repo' or 'Project/Repo'
            (the project part must match that project).

            Filter: '*' (default), comma-separated 'Repo', 'Project/Repo', globs, or 'ring:<name>'.
            """);
        return 0;
    }

    public static Task<int> PlanAsync(CommandLine cl, TextWriter output)
    {
        var configDir = cl.Get("config", "config");
        var errors = ValidateConfiguration(configDir);
        if (errors.Count > 0)
        {
            foreach (var error in errors)
            {
                output.WriteLine(InPipeline ? $"##vso[task.logissue type=error]{error}" : $"error: {error}");
            }

            return Task.FromResult(1);
        }

        var inventory = ConfigLoader.LoadInventory(configDir);
        var selected = ConfigLoader.Select(inventory, cl.Get("filter"));
        var matrix = BuildMatrix(selected);
        var json = matrix.ToJsonString();

        output.WriteLine($"Project: {inventory.Project}");
        output.WriteLine($"Selected {selected.Count} of {inventory.Repositories.Count} repositories:");
        foreach (var repo in selected)
        {
            output.WriteLine($"  - {repo.Repository} (base: {repo.BaseBranch}{(repo.Ring is null ? "" : $", ring: {repo.Ring}")})");
        }

        output.WriteLine($"##vso[task.setvariable variable=matrix;isOutput=true]{json}");
        output.WriteLine($"##vso[task.setvariable variable=repoCount;isOutput=true]{selected.Count}");
        return Task.FromResult(0);
    }

    /// <summary>Matrix keys (the sanitized repository name) must be unique and contain only letters, digits and underscores.</summary>
    public static JsonObject BuildMatrix(IReadOnlyList<RepositoryEntry> repositories)
    {
        var matrix = new JsonObject();
        foreach (var repo in repositories)
        {
            var key = NonIdentifierChars().Replace(repo.Repository, "_");
            var unique = key;
            for (var i = 2; matrix.ContainsKey(unique); i++)
            {
                unique = $"{key}_{i}";
            }

            matrix[unique] = new JsonObject { ["targetRepository"] = repo.Repository };
        }

        return matrix;
    }

    /// <summary>Finds an inventory entry by <c>Repo</c> or <c>Project/Repo</c>; the project part must be the inventory's project.</summary>
    public static RepositoryEntry ResolveRepository(RepositoryInventory inventory, string key, string source)
    {
        var name = key.Trim();
        var slash = name.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            var project = name[..slash];
            name = name[(slash + 1)..];
            if (!string.Equals(project, inventory.Project, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Repository '{key}' names project '{project}', but all repositories are in project '{inventory.Project}'.");
            }
        }

        return inventory.Repositories.FirstOrDefault(r => string.Equals(r.Repository, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Repository '{key}' is not in {source}.");
    }

    public static async Task<int> RunAsync(CommandLine cl, TextWriter output, CancellationToken ct)
    {
        var configDir = Path.GetFullPath(cl.Get("config", "config"));
        var key = cl.Require("repository");
        var inventory = ConfigLoader.LoadInventory(configDir);
        var entry = ResolveRepository(inventory, key, Path.Combine(configDir, "repositories.json"));
        if (!entry.Enabled)
        {
            output.WriteLine($"Repository {entry.Key} is disabled in the inventory; nothing to do.");
            return 0;
        }

        var aiProvider = cl.Get("ai-provider", "config").ToLowerInvariant();
        if (aiProvider != "config" && !KnownProviders.Contains(aiProvider))
        {
            throw new ArgumentException($"--ai-provider must be config, {string.Join(", ", KnownProviders)}.");
        }

        var options = new RunOptions
        {
            DryRun = cl.Flag("dry-run"),
            AiProviderOverride = aiProvider == "config" ? null : aiProvider,
            ConfigDirectory = configDir,
            OutputDirectory = Path.GetFullPath(cl.Get("output", "out")),
            AgentExecutablePath = ResolveAgentExecutable(),
        };
        var workRoot = Path.GetFullPath(cl.Get("work-dir", Path.Combine(Environment.GetEnvironmentVariable("AGENT_TEMPDIRECTORY") ?? Path.GetTempPath(), "dependency-agent-work")));

        using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        }).SetMinimumLevel(LogLevel.Information));

        var connection = AzureDevOpsConnection.FromEnvironment(cl.Get("collection-uri"));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
        await using var composition = new ServiceComposition(connection, http, loggerFactory, options.DryRun);

        var orchestrator = new RepoUpgradeOrchestrator(composition.Services);
        var defaults = ConfigLoader.LoadDefaultsNode(configDir);
        var report = await orchestrator.RunAsync(entry, defaults, options, workRoot, ct);
        await RepoUpgradeOrchestrator.WriteReportAsync(report, composition.Services.Renderer, options.OutputDirectory, ct);

        output.WriteLine($"{entry.Key}: {report.Outcome} ({report.Changes.Count} change(s)){(report.PullRequest is { } pr ? $" → {pr.Url}" : "")}");
        foreach (var reason in report.FailureReasons)
        {
            output.WriteLine(InPipeline ? $"##vso[task.logissue type=warning]{PipelineCommandNeutralizer.Neutralize(reason)}" : $"  ! {reason}");
        }

        switch (report.Outcome)
        {
            case RunOutcome.Failed:
                return 1;
            case RunOutcome.Draft or RunOutcome.Skipped when InPipeline:
                output.WriteLine("##vso[task.complete result=SucceededWithIssues;]Draft or skipped - see report");
                break;
        }

        return 0;
    }

    public static async Task<int> ReportAsync(CommandLine cl, TextWriter output, CancellationToken ct)
    {
        var input = Path.GetFullPath(cl.Require("input"));
        var outputFile = Path.GetFullPath(cl.Get("output", Path.Combine(input, "summary.md")));
        var reports = new List<RepoRunReport>();
        if (Directory.Exists(input))
        {
            foreach (var file in Directory.EnumerateFiles(input, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                try
                {
                    var report = JsonSerializer.Deserialize<RepoRunReport>(await File.ReadAllTextAsync(file, ct), ReportJson.Options);
                    if (report is { Repository.Length: > 0 })
                    {
                        reports.Add(report);
                    }
                }
                catch (JsonException ex)
                {
                    output.WriteLine($"Skipping {file}: {ex.Message}");
                }
            }
        }

        var markdown = new MarkdownReportRenderer().RenderRunSummary(reports);
        Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);
        await File.WriteAllTextAsync(outputFile, markdown, ct);
        output.WriteLine($"Summarised {reports.Count} repository report(s) into {outputFile}");
        if (InPipeline)
        {
            output.WriteLine($"##vso[task.uploadsummary]{outputFile}");
        }

        return reports.Any(r => r.Outcome == RunOutcome.Failed) ? 1 : 0;
    }

    public static int ValidateConfig(CommandLine cl, TextWriter output)
    {
        var errors = ValidateConfiguration(cl.Get("config", "config"));
        foreach (var error in errors)
        {
            output.WriteLine($"error: {error}");
        }

        output.WriteLine(errors.Count == 0 ? "Configuration is valid." : $"{errors.Count} configuration error(s).");
        return errors.Count == 0 ? 0 : 1;
    }

    /// <summary>Semantic checks beyond JSON shape (the JSON schemas cover editor-time validation).</summary>
    public static List<string> ValidateConfiguration(string configDir, Func<string, string?>? getVariable = null)
    {
        var errors = new List<string>();
        JsonObject defaults;
        RepositoryInventory inventory;
        try
        {
            defaults = ConfigLoader.LoadDefaultsNode(configDir);
            inventory = ConfigLoader.LoadInventory(configDir, getVariable);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or JsonException)
        {
            return [ex.Message];
        }

        Check("defaults.json", ConfigLoader.BuildEffectiveSettings(defaults, null, null));
        foreach (var repo in inventory.Repositories)
        {
            try
            {
                Check(repo.Key, ConfigLoader.BuildEffectiveSettings(defaults, null, repo.Overrides));
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                errors.Add($"{repo.Key}: invalid overrides ({ex.Message}).");
            }
        }

        return errors;

        void Check(string source, AgentSettings s)
        {
            if (!TargetFrameworkPattern().IsMatch(s.TargetFramework))
            {
                errors.Add($"{source}: targetFramework '{s.TargetFramework}' must look like net10.0.");
            }

            if (!KnownProviders.Contains(s.Ai.Provider, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"{source}: ai.provider must be one of {string.Join(", ", KnownProviders)}.");
            }

            if (s.Ai.MaxIterations is < 0 or > 10)
            {
                errors.Add($"{source}: ai.maxIterations must be between 0 and 10.");
            }

            if (s.Guards.MaxChangedFiles <= 0 || s.Guards.MaxChangedLines <= 0)
            {
                errors.Add($"{source}: guards.maxChangedFiles and maxChangedLines must be positive.");
            }

            if (string.IsNullOrWhiteSpace(s.BranchName) || s.BranchName.Contains(' ') || s.BranchName.StartsWith("refs/", StringComparison.Ordinal))
            {
                errors.Add($"{source}: branchName must be a plain branch name such as dependency-agent/net10-lts.");
            }

            foreach (var (name, value) in new[]
            {
                ("ai.reasoningEffort", s.Ai.ReasoningEffort),
                ("ai.copilot.reasoningEffort", s.Ai.Copilot.ReasoningEffort),
                ("ai.openAi.reasoningEffort", s.Ai.OpenAi.ReasoningEffort),
            })
            {
                if (value is not null && !ReasoningEfforts.Contains(value, StringComparer.Ordinal))
                {
                    errors.Add($"{source}: {name} '{value}' must be one of {string.Join(", ", ReasoningEfforts)}.");
                }
            }

            if (s.Ai.Roslynk.Enabled && !s.Ai.Roslynk.Arguments.Contains("stdio", StringComparer.Ordinal))
            {
                errors.Add($"{source}: ai.roslynk.arguments must contain 'stdio' (without it Roslynk starts as an HTTP daemon, not an MCP stdio server).");
            }

            if (!Uri.TryCreate(s.Ai.LearnMcp.Endpoint, UriKind.Absolute, out var learn) || learn.Scheme != Uri.UriSchemeHttps)
            {
                errors.Add($"{source}: ai.learnMcp.endpoint must be an https URL.");
            }
        }
    }

    /// <summary>The path used to re-launch this tool as the Learn proxy (the .dll when hosted by <c>dotnet</c>).</summary>
    private static string? ResolveAgentExecutable()
    {
        var processPath = Environment.ProcessPath;
        var processName = processPath is null ? "" : Path.GetFileNameWithoutExtension(processPath);
        return string.Equals(processName, "dotnet", StringComparison.OrdinalIgnoreCase) || processPath is null
            ? typeof(Commands).Assembly.Location
            : processPath;
    }

    [GeneratedRegex("[^A-Za-z0-9_]")]
    private static partial Regex NonIdentifierChars();

    [GeneratedRegex(@"^net\d+\.\d+$")]
    private static partial Regex TargetFrameworkPattern();
}
