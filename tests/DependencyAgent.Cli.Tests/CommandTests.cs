using System.Text.Json.Nodes;
using DependencyAgent.Core.Configuration;

namespace DependencyAgent.Cli.Tests;

public sealed class CommandTests
{
    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DependencyAgent.slnx")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
    }

    [Fact]
    public void CommandLine_ParsesValuesFlagsAndEquals()
    {
        var cl = CommandLine.Parse(["run", "--repository", "P/R", "--dry-run", "--ai-provider=openai"], Commands.Flags);

        Assert.Equal("run", cl.Command);
        Assert.Equal("P/R", cl.Require("repository"));
        Assert.True(cl.Flag("dry-run"));
        Assert.Equal("openai", cl.Get("ai-provider"));
        Assert.Null(cl.Get("output"));
    }

    [Fact]
    public void CommandLine_RejectsMissingValuesAndPositionalArguments()
    {
        Assert.Throws<ArgumentException>(() => CommandLine.Parse(["run", "--repository"], Commands.Flags));
        Assert.Throws<ArgumentException>(() => CommandLine.Parse(["run", "stray"], Commands.Flags));
        Assert.Throws<ArgumentException>(() => CommandLine.Parse(["run"], Commands.Flags).Require("repository"));
    }

    [Fact]
    public void BuildMatrix_UsesOnlyTheRepositoryNameAsSafeUniqueKey()
    {
        var matrix = Commands.BuildMatrix(
        [
            new RepositoryEntry { Project = "My Project", Repository = "api.service" },
            new RepositoryEntry { Project = "My Project", Repository = "api_service" },
        ]);

        Assert.Equal(["api_service", "api_service_2"], matrix.Select(p => p.Key));
        var entry = matrix["api_service"]!.AsObject();
        Assert.Equal(["targetRepository"], entry.Select(p => p.Key));
        Assert.Equal("api.service", entry["targetRepository"]!.GetValue<string>());
    }

    [Fact]
    public async Task Plan_EmitsMatrixCountAndProjectOnce()
    {
        using var dir = new TempConfig("""{ "project": "Proj", "repositories": [ { "repository": "A", "ring": "pilot" }, { "repository": "B", "enabled": false } ] }""");
        var output = new StringWriter();

        var exit = await Commands.PlanAsync(CommandLine.Parse(["plan", "--config", dir.Path, "--filter", "ring:pilot"], Commands.Flags), output);

        Assert.Equal(0, exit);
        var text = output.ToString();
        Assert.Contains("##vso[task.setvariable variable=repoCount;isOutput=true]1", text, StringComparison.Ordinal);
        Assert.Contains("Project: Proj", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Proj/", text, StringComparison.Ordinal);
        var matrixLine = text.Split('\n').Single(l => l.Contains("variable=matrix", StringComparison.Ordinal));
        var matrix = JsonNode.Parse(matrixLine[(matrixLine.IndexOf(']', StringComparison.Ordinal) + 1)..])!.AsObject();
        Assert.Equal("A", matrix["A"]!["targetRepository"]!.GetValue<string>());
        Assert.Null(matrix["A"]!["targetProject"]);
    }

    [Fact]
    public void ResolveRepository_AcceptsRepoOrProjectSlashRepo()
    {
        var inventory = new RepositoryInventory
        {
            Project = "Engineering",
            Repositories = [new RepositoryEntry { Project = "Engineering", Repository = "Api" }],
        };

        Assert.Equal("Api", Commands.ResolveRepository(inventory, "Api", "x").Repository);
        Assert.Equal("Api", Commands.ResolveRepository(inventory, "engineering/api", "x").Repository);
    }

    [Fact]
    public void ResolveRepository_RejectsOtherProjectsAndUnknownRepositories()
    {
        var inventory = new RepositoryInventory
        {
            Project = "Engineering",
            Repositories = [new RepositoryEntry { Project = "Engineering", Repository = "Api" }],
        };

        Assert.Contains("Other", Assert.Throws<ArgumentException>(() => Commands.ResolveRepository(inventory, "Other/Api", "x")).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => Commands.ResolveRepository(inventory, "Engineering/Nope", "x"));
        Assert.Throws<ArgumentException>(() => Commands.ResolveRepository(inventory, "Nope", "x"));
    }

    [Fact]
    public void ShippedConfiguration_IsValid()
    {
        Assert.Empty(Commands.ValidateConfiguration(Path.Combine(RepoRoot, "config"), _ => null));
    }

    [Fact]
    public void ValidateConfiguration_ReportsSemanticErrors()
    {
        using var dir = new TempConfig(
            """{ "project": "P", "repositories": [ { "repository": "A", "overrides": { "ai": { "provider": "chatgpt" } } } ] }""",
            """{ "targetFramework": "10", "guards": { "maxChangedFiles": 0 } }""");

        var errors = Commands.ValidateConfiguration(dir.Path);

        Assert.Contains(errors, e => e.Contains("targetFramework", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("maxChangedFiles", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("P/A", StringComparison.Ordinal) && e.Contains("ai.provider", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateConfiguration_ChecksReasoningEffortsAndRoslynkArguments()
    {
        using var dir = new TempConfig(
            """{ "project": "P", "repositories": [] }""",
            """{ "ai": { "reasoningEffort": "ultra", "copilot": { "reasoningEffort": "turbo" }, "openAi": { "reasoningEffort": "xx" }, "roslynk": { "arguments": ["--verbose"] } } }""");

        var errors = Commands.ValidateConfiguration(dir.Path);

        Assert.Contains(errors, e => e.Contains("ai.reasoningEffort", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("ai.copilot.reasoningEffort", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("ai.openAi.reasoningEffort", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("stdio", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateConfiguration_AcceptsNullEffortsAndRoslynkWithoutStdioWhenDisabled()
    {
        using var dir = new TempConfig(
            """{ "project": "P", "repositories": [] }""",
            """{ "ai": { "copilot": { "reasoningEffort": null }, "roslynk": { "enabled": false, "arguments": [] } } }""");

        Assert.Empty(Commands.ValidateConfiguration(dir.Path));
    }

    [Fact]
    public void ValidateConfiguration_UsesPipelineProjectVariableWhenInventoryHasNone()
    {
        using var dir = new TempConfig("""{ "repositories": [ { "repository": "A" } ] }""");

        Assert.Empty(Commands.ValidateConfiguration(dir.Path, n => n == "SYSTEM_TEAMPROJECT" ? "PipelineProject" : null));
        Assert.Contains(Commands.ValidateConfiguration(dir.Path, _ => null), e => e.Contains("project", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Plan_FailsOnInvalidConfiguration()
    {
        using var dir = new TempConfig("""{ "project": "P", "repositories": [ { "repository": "A" }, { "repository": "a" } ] }""");

        var exit = await Commands.PlanAsync(CommandLine.Parse(["plan", "--config", dir.Path], Commands.Flags), new StringWriter());

        Assert.Equal(1, exit);
    }

    private sealed class TempConfig : IDisposable
    {
        public TempConfig(string repositories, string? defaults = null)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dagent-cfg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            File.WriteAllText(System.IO.Path.Combine(Path, "repositories.json"), repositories);
            if (defaults is not null)
            {
                File.WriteAllText(System.IO.Path.Combine(Path, "defaults.json"), defaults);
            }
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
