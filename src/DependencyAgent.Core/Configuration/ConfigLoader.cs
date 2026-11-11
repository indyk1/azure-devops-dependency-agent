using System.Text.Json;
using System.Text.Json.Nodes;

namespace DependencyAgent.Core.Configuration;

/// <summary>
/// Loads central configuration and produces effective settings for a repository.
/// Precedence (lowest → highest): defaults.json → repo <c>.dependency-agent.json</c> → central entry overrides.
/// Safety rules applied after merging, so a target repository can only make things stricter:
/// <list type="bullet">
/// <item><c>guards.protectedPatterns</c> is the union of all layers.</item>
/// <item><c>guards.maxChangedFiles/maxChangedLines</c> from the repo file can only lower the central value.</item>
/// <item><c>ai.enabled</c> from the repo file can only turn AI off; <c>ai.provider</c>, <c>ai.openAi</c>,
/// <c>ai.copilot</c> and <c>ai.learnMcp.endpoint</c> are ignored in the repo file.</item>
/// </list>
/// </summary>
public static class ConfigLoader
{
    public const string RepoOverrideFileName = ".dependency-agent.json";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = false };
    private static readonly JsonDocumentOptions DocOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static JsonObject LoadDefaultsNode(string configDirectory)
    {
        var path = Path.Combine(configDirectory, "defaults.json");
        if (!File.Exists(path))
        {
            return (JsonObject)JsonSerializer.SerializeToNode(new AgentSettings(), JsonOptions)!;
        }

        return ParseObject(File.ReadAllText(path), path);
    }

    public static RepositoryInventory LoadInventory(string configDirectory)
    {
        var path = Path.Combine(configDirectory, "repositories.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Repository inventory not found: {path}");
        }

        var node = ParseObject(File.ReadAllText(path), path);
        node.Remove("$schema");
        var inventory = node.Deserialize<RepositoryInventory>(JsonOptions) ?? new RepositoryInventory();
        var duplicates = inventory.Repositories.GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException($"Duplicate repositories in {path}: {string.Join(", ", duplicates)}");
        }

        foreach (var repo in inventory.Repositories)
        {
            if (string.IsNullOrWhiteSpace(repo.Project) || string.IsNullOrWhiteSpace(repo.Repository))
            {
                throw new InvalidOperationException($"Every entry in {path} needs 'project' and 'repository'.");
            }
        }

        return inventory;
    }

    /// <summary>
    /// Select repositories for a run. <paramref name="filter"/> is <c>*</c>/empty (all enabled),
    /// or a comma/semicolon-separated list of <c>Project/Repo</c>, <c>Repo</c> or <c>ring:NAME</c> tokens (globs allowed).
    /// Disabled entries are never returned.
    /// </summary>
    public static IReadOnlyList<RepositoryEntry> Select(RepositoryInventory inventory, string? filter)
    {
        var enabled = inventory.Repositories.Where(r => r.Enabled).ToList();
        if (string.IsNullOrWhiteSpace(filter) || filter.Trim() == "*")
        {
            return enabled;
        }

        var tokens = filter.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return enabled.Where(r => tokens.Any(t =>
            t.StartsWith("ring:", StringComparison.OrdinalIgnoreCase)
                ? GlobMatcher.IsMatch(t[5..], r.Ring ?? "")
                : t.Contains('/') ? GlobMatcher.IsMatch(t, r.Key) : GlobMatcher.IsMatch(t, r.Repository))).ToList();
    }

    /// <summary>Merge layers into effective settings. <paramref name="repoOverrideJson"/> is the content of the repo's override file, if any.</summary>
    public static AgentSettings BuildEffectiveSettings(JsonObject defaults, string? repoOverrideJson, JsonObject? centralOverrides)
    {
        var central = (JsonObject)defaults.DeepClone();
        central.Remove("$schema");
        if (centralOverrides is not null)
        {
            DeepMerge(central, (JsonObject)centralOverrides.DeepClone());
        }

        var centralSettings = central.Deserialize<AgentSettings>(JsonOptions) ?? new AgentSettings();
        if (string.IsNullOrWhiteSpace(repoOverrideJson))
        {
            return centralSettings;
        }

        var repoNode = ParseObject(repoOverrideJson, RepoOverrideFileName);
        repoNode.Remove("$schema");
        StripRepoForbiddenKeys(repoNode);

        // defaults → repo → central overrides
        var merged = (JsonObject)defaults.DeepClone();
        merged.Remove("$schema");
        DeepMerge(merged, repoNode);
        if (centralOverrides is not null)
        {
            DeepMerge(merged, (JsonObject)centralOverrides.DeepClone());
        }

        var effective = merged.Deserialize<AgentSettings>(JsonOptions) ?? new AgentSettings();

        // Safety rules: repo can only tighten.
        effective.Guards.ProtectedPatterns = centralSettings.Guards.ProtectedPatterns
            .Concat(effective.Guards.ProtectedPatterns)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        effective.Guards.MaxChangedFiles = Math.Min(effective.Guards.MaxChangedFiles, centralSettings.Guards.MaxChangedFiles);
        effective.Guards.MaxChangedLines = Math.Min(effective.Guards.MaxChangedLines, centralSettings.Guards.MaxChangedLines);
        effective.Ai.Enabled = centralSettings.Ai.Enabled && effective.Ai.Enabled;
        effective.Ai.Provider = centralSettings.Ai.Provider;
        effective.Ai.OpenAi = centralSettings.Ai.OpenAi;
        effective.Ai.Copilot = centralSettings.Ai.Copilot;
        effective.Ai.LearnMcp.Endpoint = centralSettings.Ai.LearnMcp.Endpoint;
        effective.Ai.LearnMcp.DenyTerms = centralSettings.Ai.LearnMcp.DenyTerms
            .Concat(effective.Ai.LearnMcp.DenyTerms).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        effective.Ai.LearnMcp.Enabled = centralSettings.Ai.LearnMcp.Enabled && effective.Ai.LearnMcp.Enabled;
        return effective;
    }

    private static void StripRepoForbiddenKeys(JsonObject repoNode)
    {
        if (repoNode["ai"] is JsonObject ai)
        {
            ai.Remove("provider");
            ai.Remove("openAi");
            ai.Remove("copilot");
            if (ai["learnMcp"] is JsonObject learn)
            {
                learn.Remove("endpoint");
            }
        }

        repoNode.Remove("gitIdentity");
        repoNode.Remove("branchName");
    }

    /// <summary>Objects merge recursively; arrays and scalars in <paramref name="overlay"/> replace those in <paramref name="target"/>.</summary>
    public static void DeepMerge(JsonObject target, JsonObject overlay)
    {
        foreach (var (key, value) in overlay.ToList())
        {
            var existingKey = target.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) ?? key;
            if (value is JsonObject overlayObj && target[existingKey] is JsonObject targetObj)
            {
                DeepMerge(targetObj, overlayObj);
            }
            else
            {
                overlay.Remove(key);
                target.Remove(existingKey);
                target[existingKey] = value;
            }
        }
    }

    private static JsonObject ParseObject(string json, string source)
    {
        try
        {
            return JsonNode.Parse(json, NodeOptions, DocOptions) as JsonObject
                ?? throw new InvalidOperationException($"{source}: root must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{source}: invalid JSON ({ex.Message}).", ex);
        }
    }
}
