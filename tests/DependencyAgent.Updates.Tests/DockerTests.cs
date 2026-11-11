using DependencyAgent.Core.Model;
using DependencyAgent.Updates.Steps;

namespace DependencyAgent.Updates.Tests;

public class DockerTagMapperTests
{
    [Theory]
    [InlineData("8.0", "10.0")]
    [InlineData("6.0", "10.0")]
    [InlineData("3.1", "10.0")]
    [InlineData("9.0", "10.0")]
    [InlineData("8.0-bookworm-slim", "10.0-noble")]
    [InlineData("8.0-bookworm", "10.0-noble")]
    [InlineData("6.0-bullseye-slim", "10.0-noble")]
    [InlineData("3.1-buster-slim", "10.0-noble")]
    [InlineData("8.0-jammy", "10.0-noble")]
    [InlineData("6.0-focal", "10.0-noble")]
    [InlineData("8.0-jammy-chiseled", "10.0-noble-chiseled")]
    [InlineData("8.0-jammy-chiseled-extra", "10.0-noble-chiseled-extra")]
    [InlineData("8.0-jammy-chiseled-composite-extra", "10.0-noble-chiseled-composite-extra")]
    [InlineData("9.0-noble", "10.0-noble")]
    [InlineData("9.0-noble-chiseled", "10.0-noble-chiseled")]
    [InlineData("8.0-alpine", "10.0-alpine")]
    [InlineData("8.0-alpine3.20", "10.0-alpine")]
    [InlineData("8.0-alpine3.19-composite", "10.0-alpine-composite")]
    [InlineData("8.0-cbl-mariner2.0", "10.0-azurelinux3.0")]
    [InlineData("8.0-cbl-mariner2.0-distroless", "10.0-azurelinux3.0-distroless")]
    [InlineData("9.0-azurelinux3.0-distroless", "10.0-azurelinux3.0-distroless")]
    [InlineData("8.0.11", "10.0")]
    [InlineData("8.0.404-bookworm-slim", "10.0-noble")]
    [InlineData("8.0-bookworm-slim-amd64", "10.0-noble-amd64")]
    [InlineData("8.0-alpine3.20-arm64v8", "10.0-alpine-arm64v8")]
    public void Maps_tags(string tag, string expected) =>
        Assert.Equal(expected, DockerTagMapper.Map(tag, 10).NewTag);

    [Theory]
    [InlineData("8.0-nanoserver-ltsc2022")]
    [InlineData("8.0-windowsservercore-ltsc2019")]
    [InlineData("8.0-nanoserver-1809")]
    [InlineData("8.0-somethingnew")]
    [InlineData("8.0-preview")]
    public void Leaves_windows_and_unknown_variants_with_a_note(string tag)
    {
        var r = DockerTagMapper.Map(tag, 10);
        Assert.Null(r.NewTag);
        Assert.Single(r.Notes);
    }

    [Theory]
    [InlineData("10.0")]
    [InlineData("10.0-noble")]
    [InlineData("11.0")]
    [InlineData("latest")]
    [InlineData("lts")]
    public void Leaves_current_or_unversioned_tags_silently(string tag)
    {
        var r = DockerTagMapper.Map(tag, 10);
        Assert.Null(r.NewTag);
        Assert.Empty(r.Notes);
    }

    [Fact]
    public void Notes_explain_os_and_patch_changes()
    {
        Assert.Contains("Debian", Assert.Single(DockerTagMapper.Map("8.0-bookworm-slim", 10).Notes));
        Assert.Contains("Patch pin", Assert.Single(DockerTagMapper.Map("8.0.11", 10).Notes));
        Assert.Contains("Alpine version 3.20", Assert.Single(DockerTagMapper.Map("8.0-alpine3.20", 10).Notes));
        Assert.Empty(DockerTagMapper.Map("8.0-jammy", 10).Notes);
    }
}

public class DockerImageUpdateStepTests : IDisposable
{
    private readonly TestRepo _repo = new();

    public void Dispose() => _repo.Dispose();

    private Task<UpdateStepResult> Run() => new DockerImageUpdateStep().ApplyAsync(_repo.Context(), CancellationToken.None);

    [Fact]
    public async Task Rewrites_from_lines_preserving_the_rest()
    {
        _repo.Write("Dockerfile", "# FROM mcr.microsoft.com/dotnet/sdk:6.0\nFROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build\nWORKDIR /src\nfrom mcr.microsoft.com/dotnet/aspnet:8.0 as final  \nCOPY --from=build /app .\n");
        var result = await Run();

        Assert.Equal("# FROM mcr.microsoft.com/dotnet/sdk:6.0\nFROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build\nWORKDIR /src\nfrom mcr.microsoft.com/dotnet/aspnet:10.0 as final  \nCOPY --from=build /app .\n", _repo.Read("Dockerfile"));
        Assert.Equal(2, result.Changes.Count);
        Assert.Equal(new ChangeRecord(ChangeKind.DockerImage, "Dockerfile", "mcr.microsoft.com/dotnet/sdk", "8.0-bookworm-slim", "10.0-noble"), result.Changes[0]);
        Assert.Equal("mcr.microsoft.com/dotnet/aspnet", result.Changes[1].Subject);
        Assert.Contains(result.Notes, n => n.Contains("Debian"));
    }

    [Fact]
    public async Task Preserves_crlf_and_missing_final_newline_and_bom()
    {
        var text = "FROM mcr.microsoft.com/dotnet/runtime:8.0-jammy\r\nRUN echo\r\nFROM mcr.microsoft.com/dotnet/runtime-deps:8.0-alpine3.19";
        _repo.Write("app/Dockerfile.prod", text, bom: true);
        await Run();
        Assert.Equal("FROM mcr.microsoft.com/dotnet/runtime:10.0-noble\r\nRUN echo\r\nFROM mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine", _repo.Read("app/Dockerfile.prod"));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, _repo.ReadBytes("app/Dockerfile.prod")[..3]);
    }

    [Theory]
    [InlineData("api.Dockerfile")]
    [InlineData("deploy/web.dockerfile")]
    [InlineData("Dockerfile.dev")]
    public async Task Matches_configured_file_patterns(string path)
    {
        _repo.Write(path, "FROM mcr.microsoft.com/dotnet/sdk:8.0\n");
        var result = await Run();
        Assert.Single(result.Changes);
    }

    [Fact]
    public async Task Does_not_touch_unmatched_files()
    {
        _repo.Write("docs/readme.txt", "FROM mcr.microsoft.com/dotnet/sdk:8.0\n");
        var result = await Run();
        Assert.Empty(result.Changes);
    }

    [Fact]
    public async Task Skips_digest_pinned_images_with_note()
    {
        const string text = "FROM mcr.microsoft.com/dotnet/aspnet:8.0@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\nFROM mcr.microsoft.com/dotnet/sdk@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\n";
        _repo.Write("Dockerfile", text);
        var result = await Run();
        Assert.Empty(result.Changes);
        Assert.Equal(text, _repo.Read("Dockerfile"));
        Assert.Equal(2, result.Notes.Count(n => n.Contains("digest-pinned")));
    }

    [Fact]
    public async Task Skips_nightly_and_other_dotnet_images_and_foreign_registries()
    {
        const string text = "FROM mcr.microsoft.com/dotnet/nightly/sdk:10.0\nFROM mcr.microsoft.com/dotnet/monitor:8.0\nFROM ubuntu:22.04\nFROM docker.io/library/node:18\nFROM mcr.microsoft.com/dotnet/framework/sdk:4.8\n";
        _repo.Write("Dockerfile", text);
        var result = await Run();
        Assert.Empty(result.Changes);
        Assert.Equal(text, _repo.Read("Dockerfile"));
        Assert.Contains(result.Notes, n => n.Contains("nightly"));
    }

    [Fact]
    public async Task Windows_variants_unchanged_with_note()
    {
        const string text = "FROM mcr.microsoft.com/dotnet/aspnet:8.0-nanoserver-ltsc2022\n";
        _repo.Write("Dockerfile", text);
        var result = await Run();
        Assert.Empty(result.Changes);
        Assert.Equal(text, _repo.Read("Dockerfile"));
        Assert.Contains(result.Notes, n => n.Contains("Windows"));
    }

    [Fact]
    public async Task Updates_arg_default_used_in_from()
    {
        _repo.Write("Dockerfile", "ARG DOTNET_VERSION=8.0-bookworm-slim\nFROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build\nFROM mcr.microsoft.com/dotnet/aspnet:$DOTNET_VERSION\n");
        var result = await Run();
        Assert.Equal("ARG DOTNET_VERSION=10.0-noble\nFROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build\nFROM mcr.microsoft.com/dotnet/aspnet:$DOTNET_VERSION\n", _repo.Read("Dockerfile"));
        Assert.Equal(2, result.Changes.Count);
        Assert.All(result.Changes, c => Assert.Equal("8.0-bookworm-slim", c.From ?? "8.0-bookworm-slim"));
        Assert.Contains(result.Changes, c => c.Subject == "mcr.microsoft.com/dotnet/sdk" && c.To == "10.0-noble");
    }

    [Fact]
    public async Task Updates_quoted_arg_default()
    {
        _repo.Write("Dockerfile", "ARG VER=\"8.0\"\nFROM mcr.microsoft.com/dotnet/sdk:${VER}\n");
        await Run();
        Assert.StartsWith("ARG VER=\"10.0\"\n", _repo.Read("Dockerfile"));
    }

    [Fact]
    public async Task Arg_without_default_is_noted()
    {
        const string text = "ARG VER\nFROM mcr.microsoft.com/dotnet/sdk:${VER}\n";
        _repo.Write("Dockerfile", text);
        var result = await Run();
        Assert.Empty(result.Changes);
        Assert.Contains(result.Notes, n => n.Contains("ARG"));
        Assert.Equal(text, _repo.Read("Dockerfile"));
    }

    [Fact]
    public async Task Is_idempotent()
    {
        _repo.Write("Dockerfile", "ARG V=8.0\nFROM mcr.microsoft.com/dotnet/sdk:${V}\nFROM mcr.microsoft.com/dotnet/aspnet:8.0.11-jammy\n");
        await Run();
        var after = _repo.Read("Dockerfile");
        var second = await Run();
        Assert.Empty(second.Changes);
        Assert.Equal(after, _repo.Read("Dockerfile"));
    }

    [Fact]
    public async Task Patch_pin_note()
    {
        _repo.Write("Dockerfile", "FROM mcr.microsoft.com/dotnet/aspnet:8.0.11\n");
        var result = await Run();
        Assert.Contains("aspnet:10.0\n", _repo.Read("Dockerfile"));
        Assert.Contains(result.Notes, n => n.Contains("Patch pin"));
    }

    [Fact]
    public async Task Legacy_core_image_path_is_rewritten()
    {
        _repo.Write("Dockerfile", "FROM mcr.microsoft.com/dotnet/core/sdk:3.1-buster AS b\n");
        var result = await Run();
        Assert.Equal("FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS b\n", _repo.Read("Dockerfile"));
        Assert.Equal("mcr.microsoft.com/dotnet/sdk", Assert.Single(result.Changes).Subject);
    }

    [Fact]
    public async Task Disabled_does_nothing()
    {
        _repo.Settings.Docker.Enabled = false;
        const string text = "FROM mcr.microsoft.com/dotnet/sdk:8.0\n";
        _repo.Write("Dockerfile", text);
        var result = await Run();
        Assert.Empty(result.Changes);
        Assert.Equal(text, _repo.Read("Dockerfile"));
    }

    [Fact]
    public async Task Honors_custom_file_patterns()
    {
        _repo.Settings.Docker.FilePatterns = ["**/Containerfile"];
        _repo.Write("Containerfile", "FROM mcr.microsoft.com/dotnet/sdk:8.0\n");
        _repo.Write("Dockerfile", "FROM mcr.microsoft.com/dotnet/sdk:8.0\n");
        var result = await Run();
        Assert.Equal("Containerfile", Assert.Single(result.Changes).File);
    }
}
