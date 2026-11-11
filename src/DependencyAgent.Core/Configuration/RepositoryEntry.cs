using System.Text.Json.Nodes;

namespace DependencyAgent.Core.Configuration;

/// <summary>One entry in <c>config/repositories.json</c>.</summary>
public sealed class RepositoryEntry
{
    /// <summary>Azure DevOps project name.</summary>
    public string Project { get; set; } = "";

    /// <summary>Azure DevOps repository name.</summary>
    public string Repository { get; set; } = "";

    /// <summary>Base branch PRs target. Default <c>main</c>.</summary>
    public string BaseBranch { get; set; } = "main";

    /// <summary>Kill switch for this repository.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Rollout ring label (informational, also usable as a run filter).</summary>
    public string? Ring { get; set; }

    public List<string> Owners { get; set; } = [];

    /// <summary>Partial <see cref="AgentSettings"/> JSON applied on top of defaults and the repo's own override file.</summary>
    public JsonObject? Overrides { get; set; }

    /// <summary><c>Project/Repository</c>.</summary>
    public string Key => $"{Project}/{Repository}";

    public override string ToString() => Key;
}

public sealed class RepositoryInventory
{
    public List<RepositoryEntry> Repositories { get; set; } = [];
}
