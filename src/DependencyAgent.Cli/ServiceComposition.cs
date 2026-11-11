using System.Collections.Concurrent;
using DependencyAgent.AzureDevOps;
using DependencyAgent.Core;
using DependencyAgent.Core.Model;
using DependencyAgent.Core.Processes;
using DependencyAgent.Fixing;
using DependencyAgent.LearnProxy;
using DependencyAgent.Updates;
using DependencyAgent.Updates.Packages;
using DependencyAgent.Verification;
using Microsoft.Extensions.Logging;

namespace DependencyAgent.Cli;

/// <summary>Builds the production object graph for <c>run</c>. Owns per-repository Learn clients.</summary>
public sealed class ServiceComposition : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, LearnDocsClient> learnClients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> denyTerms = new(StringComparer.Ordinal);

    public ServiceComposition(AzureDevOpsConnection connection, HttpClient http, ILoggerFactory loggerFactory, bool dryRun)
    {
        var runner = new ProcessRunner();
        var verifier = new DotnetVerifier(runner);
        var guard = new GitChangeGuard(runner);
        var renderer = new MarkdownReportRenderer();

        var aiFixer = new AiFixer(
            AiProviders.CreateDefault(runner),
            guard,
            runner,
            CreateLearnClient,
            context => ct => verifier.BuildAsync(context, ct),
            context => GetDenyTerms(context, connection));

        Services = new OrchestratorServices(
            Git: new GitWorkspace(runner, connection),
            PullRequests: dryRun ? null : new AzureDevOpsPullRequestClient(http, connection, renderer),
            UpdateStepsFactory: _ => UpdatePipeline.CreateDefault(root => new NuGetPackageVersionSource(root, loggerFactory.CreateLogger<NuGetPackageVersionSource>())),
            Verifier: verifier,
            Guard: guard,
            RuleFixer: new RuleBasedFixer(runner),
            AiFixer: aiFixer,
            Renderer: renderer,
            NewWarnings: (baseline, current) => WarningBaseline.NewWarnings(baseline, current),
            LoggerFactory: loggerFactory);

        ILearnDocsClient? CreateLearnClient(RepoContext context)
        {
            var learn = context.Settings.Ai.LearnMcp;
            if (!learn.Enabled)
            {
                return null;
            }

            return learnClients.GetOrAdd(context.Entry.Key, _ => LearnDocsClientFactory.Create(
                learn,
                GetDenyTerms(context, connection),
                AiFixer.AuditLogPath(context)));
        }
    }

    public OrchestratorServices Services { get; }

    /// <summary>Repository-specific words (repo/project/org names, namespaces, project and assembly names) that must never be sent to Learn.</summary>
    private IReadOnlyList<string> GetDenyTerms(RepoContext context, AzureDevOpsConnection connection) =>
        denyTerms.GetOrAdd(context.Entry.Key, _ =>
        {
            var extra = new[] { context.Entry.Repository, context.Entry.Project, connection.Organization ?? "" }
                .Concat(context.Settings.Ai.LearnMcp.DenyTerms)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToArray();
            return RepoTermExtractor.Extract(context.WorkingDirectory, extra);
        });

    public async ValueTask DisposeAsync()
    {
        foreach (var client in learnClients.Values)
        {
            await client.DisposeAsync();
        }

        learnClients.Clear();
    }
}
