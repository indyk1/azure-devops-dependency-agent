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
    public void BuildMatrix_ProducesSafeUniqueKeys()
    {
        var matrix = Commands.BuildMatrix(
        [
            new RepositoryEntry { Project = "My Project", Repository = "api.service" },
            new RepositoryEntry { Project = "My-Project", Repository = "api_service" },
        ]);

        Assert.Equal(["My_Project_api_service", "My_Project_api_service_2"], matrix.Select(p => p.Key));
        Assert.Equal("My Project", matrix["My_Project_api_service"]!["targetProject"]!.GetValue<string>());
        Assert.Equal("api.service", matrix["My_Project_api_service"]!["targetRepository"]!.GetValue<string>());
    }

    [Fact]
    public async Task Plan_EmitsMatrixAndCountOutputVariables()
    {
        using var dir = new TempConfig("""{ "repositories": [ { "project": "P", "repository": "A", "ring": "pilot" }, { "project": "P", "repository": "B", "enabled": false } ] }""");
        var output = new StringWriter();

        var exit = await Commands.PlanAsync(CommandLine.Parse(["plan", "--config", dir.Path, "--filter", "ring:pilot"], Commands.Flags), output);

        Assert.Equal(0, exit);
        var text = output.ToString();
        Assert.Contains("##vso[task.setvariable variable=repoCount;isOutput=true]1", text, StringComparison.Ordinal);
        var matrixLine = text.Split('\n').Single(l => l.Contains("variable=matrix", StringComparison.Ordinal));
        var matrix = JsonNode.Parse(matrixLine[(matrixLine.IndexOf(']', StringComparison.Ordinal) + 1)..])!.AsObject();
        Assert.Equal("A", matrix["P_A"]!["targetRepository"]!.GetValue<string>());
    }

    [Fact]
    public void ShippedConfiguration_IsValid()
    {
        Assert.Empty(Commands.ValidateConfiguration(Path.Combine(RepoRoot, "config")));
    }

    [Fact]
    public void ValidateConfiguration_ReportsSemanticErrors()
    {
        using var dir = new TempConfig(
            """{ "repositories": [ { "project": "P", "repository": "A", "overrides": { "ai": { "provider": "chatgpt" } } } ] }""",
            """{ "targetFramework": "10", "guards": { "maxChangedFiles": 0 } }""");

        var errors = Commands.ValidateConfiguration(dir.Path);

        Assert.Contains(errors, e => e.Contains("targetFramework", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("maxChangedFiles", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("P/A", StringComparison.Ordinal) && e.Contains("ai.provider", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Plan_FailsOnInvalidConfiguration()
    {
        using var dir = new TempConfig("""{ "repositories": [ { "project": "P", "repository": "A" }, { "project": "p", "repository": "a" } ] }""");

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
