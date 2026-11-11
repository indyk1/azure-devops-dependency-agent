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

    public const string TeamProjectVariable = "SYSTEM_TEAMPROJECT";

    /// <summary>
    /// Loads <c>repositories.json</c>. Every repository is in one Azure DevOps project: the top-level <c>project</c>,
    /// or (when omitted) the pipeline's own project from <c>SYSTEM_TEAMPROJECT</c>.
    /// </summary>
    public static RepositoryInventory LoadInventory(string configDirectory, Func<string, string?>? getVariable = null)
    {
        getVariable ??= Environment.GetEnvironmentVariable;
        var path = Path.Combine(configDirectory, "repositories.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Repository inventory not found: {path}");
        }

        var node = ParseObject(File.ReadAllText(path), path);
        node.Remove("$schema");
        var inventory = node.Deserialize<RepositoryInventory>(JsonOptions) ?? new RepositoryInventory();
        var project = string.IsNullOrWhiteSpace(inventory.Project) ? getVariable(TeamProjectVariable) : inventory.Project.Trim();
        if (inventory.Repositories.Count > 0 && string.IsNullOrWhiteSpace(project))
        {
            throw new InvalidOperationException(
                $"{path}: set the top-level 'project' (the Azure DevOps project that contains all target repositories), or run inside a pipeline where {TeamProjectVariable} is set.");
        }

        inventory.Project = project;
        foreach (var repo in inventory.Repositories)
        {
            if (string.IsNullOrWhiteSpace(repo.Repository))
            {
                throw new InvalidOperationException($"Every entry in {path} needs 'repository'.");
            }

            if (!string.IsNullOrWhiteSpace(repo.Project) && !string.Equals(repo.Project.Trim(), project, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{path}: repository '{repo.Repository}' names project '{repo.Project}', but all repositories must be in project '{project}'.");
            }

            repo.Project = project!;
        }

        var duplicates = inventory.Repositories.GroupBy(r => r.Repository, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException($"Duplicate repositories in {path}: {string.Join(", ", duplicates)}");
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
        var defaultsNode = Clean(defaults);
        var defaultSettings = Deserialize(defaultsNode);

        var centralNode = Clean(defaults);
        if (centralOverrides is not null)
        {
            DeepMerge(centralNode, Clean(centralOverrides));
        }

        var central = Deserialize(centralNode);

        // Lists that may only grow: central overrides add to the defaults, never replace them.
        central.Guards.ProtectedPatterns = Union(defaultSettings.Guards.ProtectedPatterns, central.Guards.ProtectedPatterns);
        central.Ai.LearnMcp.DenyTerms = Union(defaultSettings.Ai.LearnMcp.DenyTerms, central.Ai.LearnMcp.DenyTerms);

        if (string.IsNullOrWhiteSpace(repoOverrideJson))
        {
            return central;
        }

        var repoNode = ParseObject(repoOverrideJson, RepoOverrideFileName);
        RemoveKey(repoNode, "$schema");
        StripRepoForbiddenKeys(repoNode);

        // Ordinary settings: defaults → repo file → central overrides (central wins).
        var mergedNode = Clean(defaults);
        DeepMerge(mergedNode, (JsonObject)repoNode.DeepClone());
        if (centralOverrides is not null)
        {
            DeepMerge(mergedNode, Clean(centralOverrides));
        }

        var effective = Deserialize(mergedNode);

        // Safety settings: computed from the central value and the repo file explicitly, so the repo can only tighten.
        effective.Guards.ProtectedPatterns = Union(central.Guards.ProtectedPatterns, GetStrings(repoNode, "guards", "protectedPatterns"));
        effective.Guards.MaxChangedFiles = Math.Min(central.Guards.MaxChangedFiles, GetInt(repoNode, "guards", "maxChangedFiles") ?? int.MaxValue);
        effective.Guards.MaxChangedLines = Math.Min(central.Guards.MaxChangedLines, GetInt(repoNode, "guards", "maxChangedLines") ?? int.MaxValue);
        effective.Ai.Enabled = central.Ai.Enabled && (GetBool(repoNode, "ai", "enabled") ?? true);
        effective.Ai.LearnMcp.Enabled = central.Ai.LearnMcp.Enabled && (GetBool(repoNode, "ai", "learnMcp", "enabled") ?? true);
        effective.Ai.LearnMcp.DenyTerms = Union(central.Ai.LearnMcp.DenyTerms, GetStrings(repoNode, "ai", "learnMcp", "denyTerms"));
        effective.Ai.Roslynk = central.Ai.Roslynk;
        effective.Ai.Roslynk.Enabled = central.Ai.Roslynk.Enabled && (GetBool(repoNode, "ai", "roslynk", "enabled") ?? true);

        // Central-only settings.
        effective.Ai.Provider = central.Ai.Provider;
        effective.Ai.OpenAi = central.Ai.OpenAi;
        effective.Ai.Copilot = central.Ai.Copilot;
        effective.Ai.LearnMcp.Endpoint = central.Ai.LearnMcp.Endpoint;
        effective.GitIdentity = central.GitIdentity;
        effective.BranchName = central.BranchName;
        return effective;
    }

    private static JsonObject Clean(JsonObject node)
    {
        var clone = (JsonObject)node.DeepClone();
        RemoveKey(clone, "$schema");
        return clone;
    }

    private static AgentSettings Deserialize(JsonObject node) => node.Deserialize<AgentSettings>(JsonOptions) ?? new AgentSettings();

    private static List<string> Union(IEnumerable<string> first, IEnumerable<string> second) =>
        first.Concat(second).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static void StripRepoForbiddenKeys(JsonObject repoNode)
    {
        if (GetNode(repoNode, "ai") is JsonObject ai)
        {
            RemoveKey(ai, "provider");
            RemoveKey(ai, "openAi");
            RemoveKey(ai, "copilot");
            if (GetNode(ai, "learnMcp") is JsonObject learn)
            {
                RemoveKey(learn, "endpoint");
            }

            // Repositories may only switch Roslynk off; command, arguments and permissions are central.
            if (GetNode(ai, "roslynk") is JsonObject roslynk)
            {
                foreach (var key in roslynk.Select(p => p.Key).Where(k => !string.Equals(k, "enabled", StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    roslynk.Remove(key);
                }
            }
        }

        RemoveKey(repoNode, "gitIdentity");
        RemoveKey(repoNode, "branchName");
    }

    /// <summary>Case-insensitive property lookup (binding is case-insensitive, so every check must be too).</summary>
    private static JsonNode? GetNode(JsonObject obj, params string[] path)
    {
        JsonNode? current = obj;
        foreach (var segment in path)
        {
            if (current is not JsonObject o)
            {
                return null;
            }

            current = o.FirstOrDefault(p => string.Equals(p.Key, segment, StringComparison.OrdinalIgnoreCase)).Value;
        }

        return current;
    }

    private static void RemoveKey(JsonObject obj, string key)
    {
        foreach (var k in obj.Select(p => p.Key).Where(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            obj.Remove(k);
        }
    }

    private static IEnumerable<string> GetStrings(JsonObject obj, params string[] path) =>
        GetNode(obj, path) is JsonArray arr ? arr.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>() : [];

    private static int? GetInt(JsonObject obj, params string[] path) =>
        GetNode(obj, path) is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private static bool? GetBool(JsonObject obj, params string[] path) =>
        GetNode(obj, path) is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

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
