using DependencyAgent.Core.Model;
using DependencyAgent.Updates.Steps;

namespace DependencyAgent.Updates.Tests;

public class GlobalJsonTests : IDisposable
{
    private readonly TestRepo _repo = new();
    private readonly GlobalJsonUpdateStep _step = new();

    public void Dispose() => _repo.Dispose();

    private Task<UpdateStepResult> Run() => _step.ApplyAsync(_repo.Context(), CancellationToken.None);

    [Fact]
    public async Task Updates_version_and_relaxes_roll_forward()
    {
        _repo.Write("global.json", "{\n  \"sdk\": {\n    \"version\": \"8.0.404\",\n    \"rollForward\": \"latestPatch\"\n  }\n}\n");
        var result = await Run();

        Assert.Equal("{\n  \"sdk\": {\n    \"version\": \"10.0.100\",\n    \"rollForward\": \"latestFeature\"\n  }\n}\n", _repo.Read("global.json"));
        Assert.Equal(2, result.Changes.Count);
        Assert.Equal(new ChangeRecord(ChangeKind.Sdk, "global.json", "sdk.version", "8.0.404", "10.0.100"), result.Changes[0]);
    }

    [Theory]
    [InlineData("disable")]
    [InlineData("patch")]
    [InlineData("latestPatch")]
    [InlineData("feature")]
    [InlineData("minor")]
    [InlineData("latestMinor")]
    public async Task Relaxes_restrictive_roll_forward(string value)
    {
        _repo.Write("global.json", $"{{\"sdk\":{{\"version\":\"8.0.100\",\"rollForward\":\"{value}\"}}}}");
        await Run();
        Assert.Equal("{\"sdk\":{\"version\":\"10.0.100\",\"rollForward\":\"latestFeature\"}}", _repo.Read("global.json"));
    }

    [Theory]
    [InlineData("latestMajor")]
    [InlineData("major")]
    [InlineData("latestFeature")]
    public async Task Keeps_permissive_roll_forward(string value)
    {
        _repo.Write("global.json", $"{{\"sdk\":{{\"version\":\"8.0.100\",\"rollForward\":\"{value}\"}}}}");
        var result = await Run();
        Assert.Equal($"{{\"sdk\":{{\"version\":\"10.0.100\",\"rollForward\":\"{value}\"}}}}", _repo.Read("global.json"));
        Assert.Single(result.Changes);
    }

    [Fact]
    public async Task Adds_missing_roll_forward_with_detected_indent_lf()
    {
        _repo.Write("global.json", "{\n    \"sdk\": {\n        \"version\": \"6.0.100\",\n        \"allowPrerelease\": false\n    },\n    \"msbuild-sdks\": { \"X\": \"1.0.0\" }\n}\n");
        await Run();
        Assert.Equal("{\n    \"sdk\": {\n        \"version\": \"10.0.100\",\n        \"rollForward\": \"latestFeature\",\n        \"allowPrerelease\": false\n    },\n    \"msbuild-sdks\": { \"X\": \"1.0.0\" }\n}\n", _repo.Read("global.json"));
    }

    [Fact]
    public async Task Adds_missing_roll_forward_with_crlf_and_tabs_and_bom()
    {
        _repo.Write("global.json", "{\r\n\t\"sdk\": {\r\n\t\t\"version\": \"7.0.100\"\r\n\t}\r\n}\r\n", bom: true);
        await Run();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, _repo.ReadBytes("global.json")[..3]);
        Assert.Equal("{\r\n\t\"sdk\": {\r\n\t\t\"version\": \"10.0.100\",\r\n\t\t\"rollForward\": \"latestFeature\"\r\n\t}\r\n}\r\n", _repo.Read("global.json"));
    }

    [Fact]
    public async Task Handles_single_line_object_without_roll_forward()
    {
        _repo.Write("global.json", "{ \"sdk\": { \"version\": \"8.0.100\" } }");
        await Run();
        Assert.Equal("{ \"sdk\": { \"version\": \"10.0.100\", \"rollForward\": \"latestFeature\" } }", _repo.Read("global.json"));
    }

    [Fact]
    public async Task Preserves_comments_and_other_properties()
    {
        const string input = "{\n  // pinned\n  \"sdk\": {\n    \"version\": \"8.0.100\", // old\n    \"allowPrerelease\": true,\n  },\n  \"test\": { \"runner\": \"Microsoft.Testing.Platform\" },\n  \"msbuild-sdks\": { \"A.Sdk\": \"1.2.3\" }\n}\n";
        _repo.Write("global.json", input);
        await Run();
        var text = _repo.Read("global.json");
        Assert.Contains("// pinned", text);
        Assert.Contains("// old", text);
        Assert.Contains("\"allowPrerelease\": true,", text);
        Assert.Contains("\"runner\": \"Microsoft.Testing.Platform\"", text);
        Assert.Contains("\"A.Sdk\": \"1.2.3\"", text);
        Assert.Contains("\"rollForward\": \"latestFeature\", // old", text);
    }

    [Theory]
    [InlineData("10.0.100")]
    [InlineData("10.0.203")]
    [InlineData("11.0.100")]
    public async Task Does_not_touch_current_or_newer_sdk(string version)
    {
        var input = $"{{\"sdk\":{{\"version\":\"{version}\",\"rollForward\":\"disable\"}}}}";
        _repo.Write("global.json", input);
        var result = await Run();
        Assert.Empty(result.Changes);
        Assert.Equal(input, _repo.Read("global.json"));
    }

    [Fact]
    public async Task Ignores_global_json_without_sdk_version()
    {
        const string input = "{ \"msbuild-sdks\": { \"X\": \"1.0.0\" } }";
        _repo.Write("global.json", input);
        var result = await Run();
        Assert.Empty(result.Changes);
        Assert.Equal(input, _repo.Read("global.json"));
    }

    [Fact]
    public async Task Is_idempotent()
    {
        _repo.Write("global.json", "{\n  \"sdk\": {\n    \"version\": \"8.0.100\"\n  }\n}\n");
        await Run();
        var after = _repo.Read("global.json");
        var second = await Run();
        Assert.Empty(second.Changes);
        Assert.Equal(after, _repo.Read("global.json"));
    }

    [Fact]
    public async Task Updates_every_global_json_in_the_repo_and_skips_node_modules()
    {
        _repo.Write("global.json", "{\"sdk\":{\"version\":\"8.0.100\"}}");
        _repo.Write("tools/global.json", "{\"sdk\":{\"version\":\"6.0.100\"}}");
        _repo.Write("node_modules/x/global.json", "{\"sdk\":{\"version\":\"6.0.100\"}}");
        var result = await Run();
        Assert.Equal(["global.json", "global.json", "tools/global.json", "tools/global.json"], result.Changes.Select(c => c.File).Order().ToArray());
        Assert.Contains("6.0.100", _repo.Read("node_modules/x/global.json"));
    }

    [Fact]
    public async Task Malformed_json_yields_note_not_exception()
    {
        _repo.Write("global.json", "{ \"sdk\": ");
        var result = await Run();
        Assert.Empty(result.Changes);
        Assert.Contains(result.Notes, n => n.Contains("global.json"));
    }
}
