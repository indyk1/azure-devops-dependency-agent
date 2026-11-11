namespace DependencyAgent.Core.Configuration;

/// <summary>
/// Effective settings for one repository run. Produced by merging
/// <c>config/defaults.json</c>, the optional in-repo <c>.dependency-agent.json</c>
/// and the central per-repository <c>overrides</c> (see <see cref="ConfigLoader"/>).
/// </summary>
public sealed class AgentSettings
{
    /// <summary>Target framework moniker for modern .NET projects, e.g. <c>net10.0</c>.</summary>
    public string TargetFramework { get; set; } = "net10.0";

    /// <summary>Branch the agent owns in each target repository.</summary>
    public string BranchName { get; set; } = "dependency-agent/net10-lts";

    /// <summary>Git author/committer identity used for agent commits.</summary>
    public GitIdentitySettings GitIdentity { get; set; } = new();

    public PackagePolicySettings Packages { get; set; } = new();

    public DockerSettings Docker { get; set; } = new();

    public VerificationSettings Verification { get; set; } = new();

    public GuardSettings Guards { get; set; } = new();

    public RuleFixSettings RuleFixes { get; set; } = new();

    public AiSettings Ai { get; set; } = new();

    public PullRequestSettings PullRequest { get; set; } = new();
}

public sealed class GitIdentitySettings
{
    public string Name { get; set; } = "Dependency Agent";
    public string Email { get; set; } = "dependency-agent@noreply.local";
}

public sealed class PackagePolicySettings
{
    /// <summary>Update NuGet packages at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Package id glob patterns (case-insensitive, <c>*</c> wildcard) whose major version must
    /// track the target framework major (e.g. <c>Microsoft.Extensions.*</c> → 10.x for net10.0).
    /// Only applied when the package actually publishes a version with that major.
    /// </summary>
    public List<string> TrackTargetMajorFor { get; set; } =
    [
        "Microsoft.AspNetCore.*",
        "Microsoft.EntityFrameworkCore*",
        "Microsoft.Extensions.*",
        "System.*",
        "Microsoft.Data.Sqlite*"
    ];

    /// <summary>Allow prerelease versions. Default false: latest stable only.</summary>
    public bool AllowPrerelease { get; set; }

    /// <summary>Exact version pins: package id → version. Pinned packages are set to that version and never moved.</summary>
    public Dictionary<string, string> Pins { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Maximum allowed major version per package id glob pattern, e.g. <c>{"AutoMapper": 14}</c>.</summary>
    public Dictionary<string, int> MaxMajor { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Package id glob patterns never touched.</summary>
    public List<string> Ignore { get; set; } = [];
}

public sealed class DockerSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>File glob patterns treated as Dockerfiles.</summary>
    public List<string> FilePatterns { get; set; } = ["**/Dockerfile", "**/Dockerfile.*", "**/*.Dockerfile", "**/*.dockerfile"];
}

public sealed class VerificationSettings
{
    /// <summary>Solution or project to build. Null = auto-detect (single .sln/.slnx at root, else all projects).</summary>
    public string? Solution { get; set; }

    /// <summary>Run <c>dotnet test</c> after a successful build.</summary>
    public bool RunTests { get; set; } = true;

    /// <summary>Fail the run (draft PR) if the upgraded build has warnings not present in the baseline build.</summary>
    public bool FailOnNewWarnings { get; set; } = true;

    /// <summary>Extra arguments appended to dotnet build, e.g. <c>["-p:ContinuousIntegrationBuild=true"]</c>.</summary>
    public List<string> BuildArguments { get; set; } = [];

    /// <summary>Extra arguments appended to dotnet test, e.g. <c>["--filter", "Category!=Integration"]</c>.</summary>
    public List<string> TestArguments { get; set; } = [];

    public int BuildTimeoutMinutes { get; set; } = 20;

    public int TestTimeoutMinutes { get; set; } = 30;
}

public sealed class GuardSettings
{
    /// <summary>Max files changed by code fixes (rule-based + AI), excluding <see cref="BudgetExcludePatterns"/>.</summary>
    public int MaxChangedFiles { get; set; } = 40;

    /// <summary>Max added+removed lines from code fixes, excluding <see cref="BudgetExcludePatterns"/>.</summary>
    public int MaxChangedLines { get; set; } = 1500;

    /// <summary>Files excluded from the change budget (manifest updates made by the deterministic updater).</summary>
    public List<string> BudgetExcludePatterns { get; set; } =
    [
        "**/*.csproj", "**/*.fsproj", "**/*.vbproj", "**/Directory.Packages.props", "**/Directory.Build.props",
        "**/global.json", "**/packages.lock.json", "**/Dockerfile", "**/Dockerfile.*", "**/*.Dockerfile", "**/*.dockerfile"
    ];

    /// <summary>Glob patterns code fixers (rule-based and AI) may edit. Anything else is reverted.</summary>
    public List<string> AllowedEditPatterns { get; set; } =
    [
        "**/*.cs", "**/*.csproj", "**/*.props", "**/*.targets", "**/*.razor", "**/*.cshtml"
    ];

    /// <summary>Glob patterns code fixers may never edit. Union-merged: overrides can add but never remove.</summary>
    public List<string> ProtectedPatterns { get; set; } =
    [
        ".git/**", "**/Authentication/**", "**/Authorization/**", "**/Cryptography/**", "**/Security/**",
        "**/*.pfx", "**/*.snk", "**/appsettings*.json", "**/azure-pipelines*.yml", "**/.dependency-agent.json"
    ];
}

public sealed class RuleFixSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Diagnostic IDs passed to <c>dotnet format analyzers --diagnostics</c> when present in build output.</summary>
    public List<string> FormatDiagnostics { get; set; } = [];

    /// <summary>Path (relative to the agent's config directory) of the declarative rewrite rules file.</summary>
    public string RewriteRulesFile { get; set; } = "rewrite-rules.json";
}

public sealed class AiSettings
{
    /// <summary>Allow AI code fixing for this repository. Effective value = central AND repo (repos can only opt out).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary><c>copilot</c>, <c>openai</c> or <c>none</c>.</summary>
    public string Provider { get; set; } = "copilot";

    /// <summary>Model name for any provider. Null = the provider's default (<c>ai.copilot.model</c> / <c>ai.openAi.defaultModel</c>).</summary>
    public string? Model { get; set; }

    /// <summary>Reasoning effort for any provider (none, minimal, low, medium, high, xhigh, max). Null = the provider's default.</summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>Max build→fix iterations.</summary>
    public int MaxIterations { get; set; } = 3;

    /// <summary>Wall-clock limit for a single AI provider invocation.</summary>
    public int TimeoutMinutes { get; set; } = 20;

    public OpenAiSettings OpenAi { get; set; } = new();

    public CopilotSettings Copilot { get; set; } = new();

    public LearnMcpSettings LearnMcp { get; set; } = new();

    public RoslynkSettings Roslynk { get; set; } = new();
}

public sealed class OpenAiSettings
{
    /// <summary>Optional base URL for OpenAI-compatible endpoints. Null = api.openai.com.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Environment variable holding the API key.</summary>
    public string ApiKeyVariable { get; set; } = "OPENAI_API_KEY";

    public string DefaultModel { get; set; } = "gpt-5.1";

    /// <summary>Default reasoning effort for OpenAI reasoning models (low, medium, high). Null = model default.</summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>Max tool-calling turns per iteration.</summary>
    public int MaxToolTurns { get; set; } = 40;
}

public sealed class CopilotSettings
{
    /// <summary>Copilot CLI executable name/path.</summary>
    public string Executable { get; set; } = "copilot";

    /// <summary>Environment variable holding the GitHub token tied to a Copilot-licensed user.</summary>
    public string TokenVariable { get; set; } = "COPILOT_GITHUB_TOKEN";

    /// <summary>Default Copilot model id (Claude Sonnet 5.5).</summary>
    public string Model { get; set; } = "claude-sonnet-5.5";

    /// <summary>Default reasoning effort passed as <c>--reasoning-effort</c>.</summary>
    public string? ReasoningEffort { get; set; } = "high";
}

/// <summary>
/// Roslynk (https://github.com/mrpmorris/Roslynk, MIT): a local Roslyn MCP server giving the AI compiler diagnostics,
/// symbol navigation, references and Roslyn code fixes/refactorings. It listens on loopback only.
/// </summary>
public sealed class RoslynkSettings
{
    /// <summary>Offer Roslynk tools to the AI. Repository override files can only turn this off.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Executable of the installed .NET tool (pipeline: <c>dotnet tool install --global Roslynk --version 3.0.0</c>).</summary>
    public string Command { get; set; } = "roslynk";

    /// <summary>Arguments; the trailing <c>stdio</c> verb is required (without it the tool starts as an HTTP daemon).</summary>
    public List<string> Arguments { get; set; } = ["stdio"];

    /// <summary>MCP server name exposed to the AI (tool permission prefix).</summary>
    public string ServerName { get; set; } = "roslynk";

    /// <summary>Allow Roslynk write tools (apply_code_fix, apply_code_action, apply_patch, rename_symbol, …). Edits are still subject to the guards.</summary>
    public bool AllowWriteTools { get; set; } = true;
}

public sealed class LearnMcpSettings
{
    public bool Enabled { get; set; } = true;

    public string Endpoint { get; set; } = "https://learn.microsoft.com/api/mcp";

    /// <summary>Maximum length of a sanitized query.</summary>
    public int MaxQueryLength { get; set; } = 200;

    /// <summary>Extra terms (case-insensitive) that must never be sent, e.g. internal product or company names.</summary>
    public List<string> DenyTerms { get; set; } = [];
}

public sealed class PullRequestSettings
{
    public string TitlePrefix { get; set; } = "[dependency-agent]";

    /// <summary>Reviewer identities (Azure DevOps unique names or group descriptors) added as optional reviewers.</summary>
    public List<string> Reviewers { get; set; } = [];

    /// <summary>Work item ids to link to the PR.</summary>
    public List<int> WorkItemIds { get; set; } = [];

    public List<string> Labels { get; set; } = ["dependency-agent"];
}
