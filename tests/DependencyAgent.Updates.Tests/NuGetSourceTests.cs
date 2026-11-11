using DependencyAgent.Updates.Packages;
using NuGet.Configuration;

namespace DependencyAgent.Updates.Tests;

public class NuGetSourceTests : IDisposable
{
    private readonly TestRepo _repo = new();

    public void Dispose() => _repo.Dispose();

    private NuGetPackageVersionSource Create(string config, Dictionary<string, string?>? env = null)
    {
        _repo.Write("nuget.config", config);
        var settings = Settings.LoadSettingsGivenConfigPaths([Path.Combine(_repo.Root, "nuget.config")]);
        return new NuGetPackageVersionSource(settings, null, name => env is not null && env.TryGetValue(name, out var v) ? v : null);
    }

    private const string TwoFeeds = """
        <configuration>
          <packageSources>
            <clear />
            <add key="public" value="https://api.nuget.org/v3/index.json" />
            <add key="internal" value="https://pkgs.dev.azure.com/org/_packaging/feed/nuget/v3/index.json" />
            <add key="disabled" value="https://disabled.example/index.json" />
          </packageSources>
          <disabledPackageSources>
            <add key="disabled" value="true" />
          </disabledPackageSources>
        </configuration>
        """;

    [Fact]
    public void Without_mapping_all_enabled_sources_are_queried()
    {
        var source = Create(TwoFeeds);
        Assert.Equal(["public", "internal"], source.SelectSources("Anything").Select(s => s.Name).ToArray());
    }

    [Fact]
    public void Source_mapping_restricts_sources_per_package()
    {
        var source = Create("""
            <configuration>
              <packageSources>
                <clear />
                <add key="public" value="https://api.nuget.org/v3/index.json" />
                <add key="internal" value="https://pkgs.dev.azure.com/org/_packaging/feed/nuget/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="internal">
                  <package pattern="Contoso.*" />
                </packageSource>
                <packageSource key="public">
                  <package pattern="*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);

        Assert.Equal(["internal"], source.SelectSources("Contoso.Core").Select(s => s.Name).ToArray());
        Assert.Equal(["public"], source.SelectSources("Newtonsoft.Json").Select(s => s.Name).ToArray());
    }

    [Fact]
    public async Task Mapped_to_no_source_fails_instead_of_querying_everything()
    {
        var source = Create("""
            <configuration>
              <packageSources>
                <clear />
                <add key="internal" value="https://pkgs.dev.azure.com/org/_packaging/feed/nuget/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="internal">
                  <package pattern="Contoso.*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);

        Assert.Empty(source.SelectSources("Newtonsoft.Json"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetVersionsAsync("Newtonsoft.Json", CancellationToken.None));
        Assert.Contains("source mapping", ex.Message);
    }

    [Fact]
    public void Azure_artifacts_token_is_applied_to_matching_prefixes_only()
    {
        var source = Create(TwoFeeds, new()
        {
            ["VSS_NUGET_ACCESSTOKEN"] = "secret-token",
            ["VSS_NUGET_URI_PREFIXES"] = "https://pkgs.dev.azure.com/org/;https://other.example/",
        });

        var selected = source.SelectSources("X");
        var pub = selected.Single(s => s.Name == "public");
        var feed = selected.Single(s => s.Name == "internal");
        Assert.Null(pub.Credentials);
        Assert.NotNull(feed.Credentials);
        Assert.True(feed.Credentials!.IsPasswordClearText);
        Assert.Equal("secret-token", feed.Credentials.Password);
    }

    [Fact]
    public void No_credentials_without_variables()
    {
        var source = Create(TwoFeeds);
        Assert.All(source.SelectSources("X"), s => Assert.Null(s.Credentials));
    }

    [Fact]
    public void External_feed_endpoints_are_parsed()
    {
        var source = Create(TwoFeeds, new()
        {
            ["VSS_NUGET_EXTERNAL_FEED_ENDPOINTS"] = """{"endpointCredentials":[{"endpoint":"https://pkgs.dev.azure.com/org/_packaging/feed/nuget/v3/index.json","username":"svc","password":"pw"}]}""",
        });

        var feed = source.SelectSources("X").Single(s => s.Name == "internal");
        Assert.Equal("svc", feed.Credentials!.Username);
        Assert.Equal("pw", feed.Credentials.Password);
        Assert.Null(source.SelectSources("X").Single(s => s.Name == "public").Credentials);
    }

    [Fact]
    public void Malformed_external_endpoints_are_ignored()
    {
        var source = Create(TwoFeeds, new() { ["VSS_NUGET_EXTERNAL_FEED_ENDPOINTS"] = "{not json" });
        Assert.All(source.SelectSources("X"), s => Assert.Null(s.Credentials));
    }
}
