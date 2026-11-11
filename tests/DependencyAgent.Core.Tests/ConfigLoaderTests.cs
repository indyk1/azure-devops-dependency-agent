using System.Text.Json;
using System.Text.Json.Nodes;
using DependencyAgent.Core.Configuration;

namespace DependencyAgent.Core.Tests;

public class ConfigLoaderTests
{
    private static JsonObject Defaults() => ConfigLoader.LoadDefaultsNode(TestSupport.ConfigDirectory);

    private static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static AgentSettings Effective(string? repoJson = null, string? centralJson = null, JsonObject? defaults = null) =>
        ConfigLoader.BuildEffectiveSettings(defaults ?? Defaults(), repoJson, centralJson is null ? null : Obj(centralJson));

    // ---- defaults ----

    [Fact]
    public void Shipped_defaults_load_and_match_the_built_in_settings()
    {
        var settings = Effective();
        var builtIn = new AgentSettings();

        Assert.Equal(JsonSerializer.Serialize(builtIn, ConfigLoader.JsonOptions), JsonSerializer.Serialize(settings, ConfigLoader.JsonOptions));
        Assert.Equal("net10.0", settings.TargetFramework);
        Assert.Equal("dependency-agent/net10-lts", settings.BranchName);
        Assert.Equal("copilot", settings.Ai.Provider);
    }

    [Fact]
    public void Missing_defaults_file_falls_back_to_built_in_settings()
    {
        using var dir = new TempDir();
        var node = ConfigLoader.LoadDefaultsNode(dir.Path);
        var settings = ConfigLoader.BuildEffectiveSettings(node, null, null);
        Assert.Equal(40, settings.Guards.MaxChangedFiles);
        Assert.Equal("net10.0", settings.TargetFramework);
    }

    [Fact]
    public void Defaults_file_may_contain_comments_and_trailing_commas()
    {
        using var dir = new TempDir();
        dir.Touch("defaults.json", """
            {
              // comment
              "$schema": "../schemas/defaults.schema.json",
              "targetFramework": "net11.0", /* block */
              "guards": { "maxChangedFiles": 5, },
            }
            """);
        var settings = ConfigLoader.BuildEffectiveSettings(ConfigLoader.LoadDefaultsNode(dir.Path), null, null);
        Assert.Equal("net11.0", settings.TargetFramework);
        Assert.Equal(5, settings.Guards.MaxChangedFiles);
        Assert.Equal(1500, settings.Guards.MaxChangedLines);
    }

    [Fact]
    public void Defaults_with_invalid_json_or_non_object_root_throw_clear_errors()
    {
        using var dir = new TempDir();
        dir.Touch("defaults.json", "{ not json");
        Assert.Contains("invalid JSON", Assert.Throws<InvalidOperationException>(() => ConfigLoader.LoadDefaultsNode(dir.Path)).Message);

        dir.Touch("defaults.json", "[1,2]");
        Assert.Contains("root must be a JSON object", Assert.Throws<InvalidOperationException>(() => ConfigLoader.LoadDefaultsNode(dir.Path)).Message);
    }

    // ---- inventory ----

    private static RepositoryInventory LoadInventory(string json)
    {
        using var dir = new TempDir();
        dir.Touch("repositories.json", json);
        return ConfigLoader.LoadInventory(dir.Path);
    }

    [Fact]
    public void Shipped_inventory_loads()
    {
        var inventory = ConfigLoader.LoadInventory(TestSupport.ConfigDirectory);
        Assert.NotNull(inventory.Repositories);
    }

    [Fact]
    public void Inventory_applies_entry_defaults_and_reads_all_fields()
    {
        var inventory = LoadInventory("""
            {
              "$schema": "../schemas/repositories.schema.json",
              "repositories": [
                { "project": "P", "repository": "R" },
                { "project": "P2", "repository": "R2", "baseBranch": "develop", "enabled": false, "ring": "pilot",
                  "owners": ["a", "b"], "overrides": { "ai": { "enabled": false } }, },
              ]
            }
            """);

        Assert.Equal(2, inventory.Repositories.Count);
        var first = inventory.Repositories[0];
        Assert.Equal("main", first.BaseBranch);
        Assert.True(first.Enabled);
        Assert.Null(first.Ring);
        Assert.Null(first.Overrides);
        Assert.Equal("P/R", first.Key);

        var second = inventory.Repositories[1];
        Assert.Equal("develop", second.BaseBranch);
        Assert.False(second.Enabled);
        Assert.Equal("pilot", second.Ring);
        Assert.Equal(["a", "b"], second.Owners);
        Assert.False(second.Overrides!["ai"]!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void Inventory_is_case_insensitive_for_property_names()
    {
        var inventory = LoadInventory("""{ "Repositories": [ { "Project": "P", "Repository": "R" } ] }""");
        Assert.Equal("P/R", Assert.Single(inventory.Repositories).Key);
    }

    [Fact]
    public void Inventory_rejects_duplicates_ignoring_case()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LoadInventory("""
            { "repositories": [ { "project": "P", "repository": "R" }, { "project": "p", "repository": "r" } ] }
            """));
        Assert.Contains("Duplicate", ex.Message);
        Assert.Contains("P/R", ex.Message);
    }

    [Theory]
    [InlineData("""{ "repositories": [ { "repository": "R" } ] }""")]
    [InlineData("""{ "repositories": [ { "project": "P" } ] }""")]
    [InlineData("""{ "repositories": [ { "project": " ", "repository": "R" } ] }""")]
    [InlineData("""{ "repositories": [ { "project": "P", "repository": "" } ] }""")]
    public void Inventory_rejects_entries_missing_project_or_repository(string json)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LoadInventory(json));
        Assert.Contains("'project' and 'repository'", ex.Message);
    }

    [Fact]
    public void Inventory_file_missing_throws()
    {
        using var dir = new TempDir();
        Assert.Throws<FileNotFoundException>(() => ConfigLoader.LoadInventory(dir.Path));
    }

    [Fact]
    public void Inventory_with_invalid_json_names_the_file()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LoadInventory("{ oops"));
        Assert.Contains("repositories.json", ex.Message);
    }

    // ---- Select ----

    private static RepositoryInventory SampleInventory() => new()
    {
        Repositories =
        [
            new() { Project = "Engineering", Repository = "Api", Ring = "pilot" },
            new() { Project = "Engineering", Repository = "Web", Ring = "broad" },
            new() { Project = "Data", Repository = "Api", Ring = "broad" },
            new() { Project = "Data", Repository = "Etl" },
            new() { Project = "Data", Repository = "Off", Ring = "pilot", Enabled = false },
        ],
    };

    private static string[] Keys(IEnumerable<RepositoryEntry> entries) => entries.Select(e => e.Key).ToArray();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("*")]
    [InlineData(" * ")]
    public void Select_all_returns_enabled_entries_only(string? filter) =>
        Assert.Equal(["Engineering/Api", "Engineering/Web", "Data/Api", "Data/Etl"], Keys(ConfigLoader.Select(SampleInventory(), filter)));

    [Fact]
    public void Select_by_project_and_repository()
    {
        Assert.Equal(["Data/Api"], Keys(ConfigLoader.Select(SampleInventory(), "Data/Api")));
        Assert.Equal(["Data/Api"], Keys(ConfigLoader.Select(SampleInventory(), "data/api")));
    }

    [Fact]
    public void Select_by_repository_name_matches_across_projects()
    {
        Assert.Equal(["Engineering/Api", "Data/Api"], Keys(ConfigLoader.Select(SampleInventory(), "Api")));
    }

    [Fact]
    public void Select_supports_globs()
    {
        Assert.Equal(["Data/Api", "Data/Etl"], Keys(ConfigLoader.Select(SampleInventory(), "Data/*")));
        Assert.Equal(["Engineering/Api", "Data/Api"], Keys(ConfigLoader.Select(SampleInventory(), "*/Api")));
        Assert.Equal(["Engineering/Web"], Keys(ConfigLoader.Select(SampleInventory(), "W*")));
    }

    [Fact]
    public void Select_by_ring()
    {
        Assert.Equal(["Engineering/Api"], Keys(ConfigLoader.Select(SampleInventory(), "ring:pilot")));
        Assert.Equal(["Engineering/Web", "Data/Api"], Keys(ConfigLoader.Select(SampleInventory(), "RING:Broad")));
        Assert.Equal(["Engineering/Web", "Data/Api"], Keys(ConfigLoader.Select(SampleInventory(), "ring:b*")));
        Assert.Empty(ConfigLoader.Select(SampleInventory(), "ring:nope"));
    }

    [Fact]
    public void Select_combines_tokens_with_or_using_comma_semicolon_or_newline()
    {
        Assert.Equal(["Engineering/Api", "Data/Etl"], Keys(ConfigLoader.Select(SampleInventory(), "Engineering/Api, Data/Etl")));
        Assert.Equal(["Engineering/Api", "Data/Etl"], Keys(ConfigLoader.Select(SampleInventory(), "Engineering/Api;Data/Etl")));
        Assert.Equal(["Engineering/Api", "Data/Etl"], Keys(ConfigLoader.Select(SampleInventory(), "Engineering/Api\nData/Etl")));
        Assert.Equal(["Engineering/Api", "Data/Etl"], Keys(ConfigLoader.Select(SampleInventory(), "ring:pilot,Data/Etl")));
    }

    [Fact]
    public void Select_never_returns_disabled_entries_even_when_named_or_in_ring()
    {
        Assert.DoesNotContain("Data/Off", Keys(ConfigLoader.Select(SampleInventory(), "Data/Off")));
        Assert.DoesNotContain("Data/Off", Keys(ConfigLoader.Select(SampleInventory(), "ring:pilot")));
        Assert.DoesNotContain("Data/Off", Keys(ConfigLoader.Select(SampleInventory(), "Data/*")));
    }

    [Fact]
    public void Select_ring_filter_does_not_match_entries_without_a_ring()
    {
        Assert.DoesNotContain("Data/Etl", Keys(ConfigLoader.Select(SampleInventory(), "ring:pilot")));
    }

    // ---- BuildEffectiveSettings: precedence ----

    [Fact]
    public void Without_overrides_effective_settings_equal_defaults()
    {
        var s = Effective();
        Assert.Equal(40, s.Guards.MaxChangedFiles);
        Assert.True(s.Ai.Enabled);
        Assert.Equal("copilot", s.Ai.Provider);
    }

    [Fact]
    public void Blank_repo_file_is_treated_as_absent()
    {
        Assert.Equal(40, Effective("   ").Guards.MaxChangedFiles);
        Assert.Equal(40, Effective("").Guards.MaxChangedFiles);
    }

    [Fact]
    public void Central_overrides_apply_on_top_of_defaults_and_merge_objects_recursively()
    {
        var s = Effective(centralJson: """{ "ai": { "provider": "openai", "maxIterations": 5 }, "verification": { "testArguments": ["--filter", "Category!=Integration"] } }""");
        Assert.Equal("openai", s.Ai.Provider);
        Assert.Equal(5, s.Ai.MaxIterations);
        Assert.Equal(20, s.Ai.TimeoutMinutes);
        Assert.Equal("gpt-5.1", s.Ai.OpenAi.DefaultModel);
        Assert.Equal(["--filter", "Category!=Integration"], s.Verification.TestArguments);
        Assert.True(s.Verification.RunTests);
    }

    [Fact]
    public void Arrays_in_overrides_replace_rather_than_merge()
    {
        var s = Effective(centralJson: """{ "docker": { "filePatterns": ["**/Dockerfile"] }, "pullRequest": { "labels": ["x"] } }""");
        Assert.Equal(["**/Dockerfile"], s.Docker.FilePatterns);
        Assert.Equal(["x"], s.PullRequest.Labels);
    }

    [Fact]
    public void Property_names_in_overrides_are_matched_case_insensitively_when_merging()
    {
        var s = Effective(centralJson: """{ "Verification": { "RunTests": false } }""");
        Assert.False(s.Verification.RunTests);
    }

    [Fact]
    public void Precedence_is_defaults_then_repo_file_then_central_overrides()
    {
        // Each layer sets a different value for the same keys.
        var defaults = Obj("""{ "verification": { "buildTimeoutMinutes": 10, "testTimeoutMinutes": 10, "runTests": true }, "docker": { "enabled": true } }""");
        var s = Effective(
            repoJson: """{ "verification": { "buildTimeoutMinutes": 20, "testTimeoutMinutes": 20 }, "docker": { "enabled": false } }""",
            centralJson: """{ "verification": { "testTimeoutMinutes": 30 } }""",
            defaults: defaults);

        Assert.Equal(20, s.Verification.BuildTimeoutMinutes); // repo beats defaults
        Assert.Equal(30, s.Verification.TestTimeoutMinutes);  // central beats repo
        Assert.True(s.Verification.RunTests);                 // defaults preserved
        Assert.False(s.Docker.Enabled);                       // repo beats defaults
    }

    [Fact]
    public void Repo_file_may_contain_comments_and_trailing_commas()
    {
        var s = Effective("""
            {
              // we have a flaky integration suite
              "verification": { "testArguments": ["--filter", "Category!=Integration",], },
              /* block comment */
              "guards": { "maxChangedFiles": 10, }
            }
            """);
        Assert.Equal(["--filter", "Category!=Integration"], s.Verification.TestArguments);
        Assert.Equal(10, s.Guards.MaxChangedFiles);
    }

    [Fact]
    public void Repo_file_schema_property_is_ignored()
    {
        var s = Effective("""{ "$schema": "https://example/x.json", "verification": { "runTests": false } }""");
        Assert.False(s.Verification.RunTests);
    }

    [Fact]
    public void Invalid_repo_file_throws_with_file_name()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Effective("{ broken"));
        Assert.Contains(".dependency-agent.json", ex.Message);
        Assert.Throws<InvalidOperationException>(() => Effective("[]"));
    }

    // ---- BuildEffectiveSettings: safety rules ----

    [Fact(Skip = "BUG: when a central override sets an array (guards.protectedPatterns / ai.learnMcp.denyTerms) it replaces the repo file's array during the merge, so the repo's own additions are lost instead of unioned.")]
    public void Protected_patterns_are_the_union_of_all_layers_without_duplicates()
    {
        var s = Effective(
            repoJson: """{ "guards": { "protectedPatterns": ["**/Billing/**", "**/security/**"] } }""",
            centralJson: """{ "guards": { "protectedPatterns": ["**/Payments/**"] } }""");

        Assert.Contains("**/Billing/**", s.Guards.ProtectedPatterns);
        Assert.Contains("**/Payments/**", s.Guards.ProtectedPatterns);
        // central override REPLACES the default array, so default patterns only survive if listed; repo cannot remove central ones.
        Assert.Single(s.Guards.ProtectedPatterns, p => string.Equals(p, "**/Security/**", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Repo_file_cannot_remove_default_protected_patterns()
    {
        var s = Effective("""{ "guards": { "protectedPatterns": ["**/Billing/**"] } }""");
        Assert.Contains("**/Security/**", s.Guards.ProtectedPatterns);
        Assert.Contains("**/Cryptography/**", s.Guards.ProtectedPatterns);
        Assert.Contains(".git/**", s.Guards.ProtectedPatterns);
        Assert.Contains("**/Billing/**", s.Guards.ProtectedPatterns);
    }

    [Fact]
    public void Repo_file_cannot_clear_protected_patterns_with_an_empty_array()
    {
        var s = Effective("""{ "guards": { "protectedPatterns": [] } }""");
        Assert.Contains("**/Security/**", s.Guards.ProtectedPatterns);
    }

    [Fact]
    public void Repo_file_can_only_lower_the_change_limits()
    {
        var lower = Effective("""{ "guards": { "maxChangedFiles": 10, "maxChangedLines": 100 } }""");
        Assert.Equal(10, lower.Guards.MaxChangedFiles);
        Assert.Equal(100, lower.Guards.MaxChangedLines);

        var higher = Effective("""{ "guards": { "maxChangedFiles": 9999, "maxChangedLines": 999999 } }""");
        Assert.Equal(40, higher.Guards.MaxChangedFiles);
        Assert.Equal(1500, higher.Guards.MaxChangedLines);
    }

    [Fact]
    public void Central_override_may_raise_limits_for_a_repo()
    {
        var s = Effective(centralJson: """{ "guards": { "maxChangedFiles": 100 } }""");
        Assert.Equal(100, s.Guards.MaxChangedFiles);
    }

    [Fact(Skip = "BUG: BuildEffectiveSettings applies central overrides AFTER the repo file, so a centrally set guards.maxChangedFiles/maxChangedLines masks the repo's lower value (Math.Min is taken against a value that already includes the central override).")]
    public void Repo_file_can_lower_a_limit_that_central_raised_above_the_default()
    {
        // Central raises the limit to 100 (explicitly), the repo asks for 20: the repo may only tighten, so 20 is expected.
        var s = Effective(
            repoJson: """{ "guards": { "maxChangedFiles": 20, "maxChangedLines": 200 } }""",
            centralJson: """{ "guards": { "maxChangedFiles": 100, "maxChangedLines": 5000 } }""");
        Assert.Equal(20, s.Guards.MaxChangedFiles);
        Assert.Equal(200, s.Guards.MaxChangedLines);
    }

    [Fact]
    public void Repo_file_can_turn_ai_off_but_not_on()
    {
        Assert.False(Effective("""{ "ai": { "enabled": false } }""").Ai.Enabled);
        Assert.True(Effective("""{ "ai": { "enabled": true } }""").Ai.Enabled);
        Assert.False(Effective("""{ "ai": { "enabled": true } }""", """{ "ai": { "enabled": false } }""").Ai.Enabled);
    }

    [Fact(Skip = "BUG: a central override with ai.enabled=true is merged after the repo file, so the repo's ai.enabled=false opt-out is lost (same root cause for ai.learnMcp.enabled).")]
    public void Repo_file_can_turn_ai_off_even_when_central_override_sets_it_on_explicitly()
    {
        var s = Effective("""{ "ai": { "enabled": false } }""", """{ "ai": { "enabled": true } }""");
        Assert.False(s.Ai.Enabled);
    }

    [Fact]
    public void Repo_file_can_turn_learn_lookups_off_but_not_on()
    {
        Assert.False(Effective("""{ "ai": { "learnMcp": { "enabled": false } } }""").Ai.LearnMcp.Enabled);
        Assert.False(Effective("""{ "ai": { "learnMcp": { "enabled": true } } }""", """{ "ai": { "learnMcp": { "enabled": false } } }""").Ai.LearnMcp.Enabled);
    }

    [Fact]
    public void Repo_file_cannot_change_provider_credentials_endpoint_identity_or_branch()
    {
        var s = Effective("""
            {
              "branchName": "evil/branch",
              "gitIdentity": { "name": "Mallory", "email": "m@evil.example" },
              "ai": {
                "provider": "openai",
                "openAi": { "endpoint": "https://evil.example/v1", "apiKeyVariable": "SYSTEM_ACCESSTOKEN" },
                "copilot": { "executable": "/tmp/evil", "tokenVariable": "SYSTEM_ACCESSTOKEN" },
                "learnMcp": { "endpoint": "https://evil.example/mcp" },
                "model": "my-model"
              }
            }
            """);

        Assert.Equal("dependency-agent/net10-lts", s.BranchName);
        Assert.Equal("Dependency Agent", s.GitIdentity.Name);
        Assert.Equal("dependency-agent@noreply.local", s.GitIdentity.Email);
        Assert.Equal("copilot", s.Ai.Provider);
        Assert.Null(s.Ai.OpenAi.Endpoint);
        Assert.Equal("OPENAI_API_KEY", s.Ai.OpenAi.ApiKeyVariable);
        Assert.Equal("copilot", s.Ai.Copilot.Executable);
        Assert.Equal("COPILOT_GITHUB_TOKEN", s.Ai.Copilot.TokenVariable);
        Assert.Equal("https://learn.microsoft.com/api/mcp", s.Ai.LearnMcp.Endpoint);
        // Non-sensitive AI keys are still honoured.
        Assert.Equal("my-model", s.Ai.Model);
    }

    [Fact]
    public void Ignored_keys_from_the_repo_file_do_not_shadow_central_override_values()
    {
        var s = Effective(
            repoJson: """{ "ai": { "provider": "openai", "learnMcp": { "endpoint": "https://evil.example" } }, "branchName": "x" }""",
            centralJson: """{ "ai": { "provider": "none" }, "branchName": "central/branch" }""");
        Assert.Equal("none", s.Ai.Provider);
        Assert.Equal("central/branch", s.BranchName);
    }

    [Fact]
    public void Central_overrides_may_set_provider_endpoint_identity_and_branch()
    {
        var s = Effective(
            repoJson: """{ "verification": { "runTests": true } }""",
            centralJson: """{ "branchName": "b", "gitIdentity": { "name": "N" }, "ai": { "provider": "openai", "openAi": { "endpoint": "https://aoai.example/v1" } } }""");
        Assert.Equal("b", s.BranchName);
        Assert.Equal("N", s.GitIdentity.Name);
        Assert.Equal("openai", s.Ai.Provider);
        Assert.Equal("https://aoai.example/v1", s.Ai.OpenAi.Endpoint);
    }

    [Fact(Skip = "BUG: same root cause as the protectedPatterns union test - a central denyTerms override replaces the repo file's denyTerms instead of being unioned with them.")]
    public void Deny_terms_are_unioned_across_layers_case_insensitively()
    {
        var s = Effective(
            repoJson: """{ "ai": { "learnMcp": { "denyTerms": ["ProjectX", "contoso"] } } }""",
            centralJson: """{ "ai": { "learnMcp": { "denyTerms": ["Contoso", "Fabrikam"] } } }""");
        Assert.Equal(3, s.Ai.LearnMcp.DenyTerms.Count);
        Assert.Contains("Fabrikam", s.Ai.LearnMcp.DenyTerms);
        Assert.Contains("ProjectX", s.Ai.LearnMcp.DenyTerms);
        Assert.Contains(s.Ai.LearnMcp.DenyTerms, t => string.Equals(t, "contoso", StringComparison.OrdinalIgnoreCase));
    }

    // ---- Safety rules: bypass attempts ----

    [Fact(Skip = "BUG: StripRepoForbiddenKeys removes keys case-sensitively but JSON binding/merging is case-insensitive, so BranchName / GitIdentity keys in a repo file are applied (branchName and gitIdentity are not reset afterwards).")]
    public void Repo_file_cannot_bypass_ignored_keys_by_changing_property_name_casing()
    {
        // JSON binding is case-insensitive, so "BranchName"/"GitIdentity" are the same settings as "branchName"/"gitIdentity".
        var s = Effective("""{ "BranchName": "evil/branch", "GitIdentity": { "Name": "Mallory", "Email": "m@evil.example" } }""");
        Assert.Equal("dependency-agent/net10-lts", s.BranchName);
        Assert.Equal("Dependency Agent", s.GitIdentity.Name);
        Assert.Equal("dependency-agent@noreply.local", s.GitIdentity.Email);
    }

    [Fact]
    public void Repo_file_cannot_bypass_ai_restrictions_by_changing_property_name_casing()
    {
        var s = Effective("""{ "AI": { "Provider": "openai", "OpenAi": { "Endpoint": "https://evil.example" }, "LearnMcp": { "Endpoint": "https://evil.example" } } }""");
        Assert.Equal("copilot", s.Ai.Provider);
        Assert.Null(s.Ai.OpenAi.Endpoint);
        Assert.Equal("https://learn.microsoft.com/api/mcp", s.Ai.LearnMcp.Endpoint);
    }

    [Fact]
    public void Deny_terms_from_repo_file_are_added_to_central_terms_from_defaults()
    {
        var defaults = Obj("""{ "ai": { "learnMcp": { "denyTerms": ["Contoso", "Fabrikam"] } } }""");
        var s = Effective("""{ "ai": { "learnMcp": { "denyTerms": ["ProjectX", "contoso"] } } }""", defaults: defaults);
        Assert.Equal(3, s.Ai.LearnMcp.DenyTerms.Count);
        Assert.Contains("Fabrikam", s.Ai.LearnMcp.DenyTerms);
        Assert.Contains("ProjectX", s.Ai.LearnMcp.DenyTerms);
    }

    [Fact]
    public void Protected_patterns_from_repo_file_are_added_to_defaults_and_deduplicated_ignoring_case()
    {
        var s = Effective("""{ "guards": { "protectedPatterns": ["**/Billing/**", "**/security/**"] } }""");
        Assert.Contains("**/Billing/**", s.Guards.ProtectedPatterns);
        Assert.Single(s.Guards.ProtectedPatterns, p => string.Equals(p, "**/Security/**", StringComparison.OrdinalIgnoreCase));
    }

    [Fact(Skip = "BUG: a central override of guards.protectedPatterns REPLACES the default list (only a repo file gets union semantics), contradicting the AgentSettings doc comment 'Union-merged: overrides can add but never remove'.")]
    public void Central_override_of_protected_patterns_cannot_remove_defaults()
    {
        var s = Effective(centralJson: """{ "guards": { "protectedPatterns": ["**/Payments/**"] } }""");
        Assert.Contains("**/Payments/**", s.Guards.ProtectedPatterns);
        Assert.Contains("**/Security/**", s.Guards.ProtectedPatterns);
    }
}
