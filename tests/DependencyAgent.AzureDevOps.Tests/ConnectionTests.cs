namespace DependencyAgent.AzureDevOps.Tests;

public class ConnectionTests
{
    [Fact]
    public void DevAzureCom_urls_escape_names()
    {
        var c = new AzureDevOpsConnection("https://dev.azure.com/contoso", "t");
        Assert.Equal("https://dev.azure.com/contoso/", c.CollectionUri);
        Assert.Equal("https://dev.azure.com/contoso/Proj%20One/_git/My%20Repo", c.GetGitUrl("Proj One", "My Repo"));
        Assert.Equal("https://dev.azure.com/contoso/Proj%20One/_apis/git/repositories/My%20Repo", c.GetRepositoryApiUrl("Proj One", "My Repo"));
        Assert.Equal("https://dev.azure.com/contoso/Proj%20One/_git/My%20Repo/pullrequest/7", c.GetPullRequestWebUrl("Proj One", "My Repo", 7));
        Assert.Equal("contoso", c.Organization);
        Assert.Equal("https://vssps.dev.azure.com/contoso/", c.IdentityBaseUrl);
    }

    [Fact]
    public void VisualStudioCom_collection()
    {
        var c = new AzureDevOpsConnection("https://contoso.visualstudio.com/", "t");
        Assert.Equal("https://contoso.visualstudio.com/Eng/_git/Svc", c.GetGitUrl("Eng", "Svc"));
        Assert.Equal("contoso", c.Organization);
        Assert.Equal("https://contoso.vssps.visualstudio.com/", c.IdentityBaseUrl);
    }

    [Fact]
    public void OnPremises_collection_has_no_identity_service()
    {
        var c = new AzureDevOpsConnection("https://tfs.corp.local/tfs/DefaultCollection", "t");
        Assert.Equal("https://tfs.corp.local/tfs/DefaultCollection/", c.CollectionUri);
        Assert.Null(c.IdentityBaseUrl);
    }

    [Fact]
    public void FromEnvironment_prefers_override_and_pipeline_token()
    {
        var vars = new Dictionary<string, string?>
        {
            ["SYSTEM_COLLECTIONURI"] = "https://dev.azure.com/a/",
            ["SYSTEM_ACCESSTOKEN"] = "job",
            ["AZURE_DEVOPS_TOKEN"] = "local",
        };
        var c = AzureDevOpsConnection.FromEnvironment("https://dev.azure.com/b/", k => vars.GetValueOrDefault(k));
        Assert.Equal("https://dev.azure.com/b/", c.CollectionUri);
        Assert.Equal("job", c.Token);
    }

    [Fact]
    public void FromEnvironment_falls_back_to_local_token()
    {
        var vars = new Dictionary<string, string?> { ["SYSTEM_COLLECTIONURI"] = "https://dev.azure.com/a/", ["AZURE_DEVOPS_TOKEN"] = "local" };
        Assert.Equal("local", AzureDevOpsConnection.FromEnvironment(null, k => vars.GetValueOrDefault(k)).Token);
    }

    [Fact]
    public void Missing_values_give_clear_errors()
    {
        var noUrl = Assert.Throws<InvalidOperationException>(() => AzureDevOpsConnection.FromEnvironment(null, _ => null));
        Assert.Contains("SYSTEM_COLLECTIONURI", noUrl.Message);
        var noToken = Assert.Throws<InvalidOperationException>(() =>
            AzureDevOpsConnection.FromEnvironment(null, k => k == "SYSTEM_COLLECTIONURI" ? "https://dev.azure.com/a/" : null));
        Assert.Contains("SYSTEM_ACCESSTOKEN", noToken.Message);
        Assert.Contains("AZURE_DEVOPS_TOKEN", noToken.Message);
    }

    [Fact]
    public void Invalid_url_is_rejected() =>
        Assert.Throws<ArgumentException>(() => new AzureDevOpsConnection("not a url", "t"));
}
