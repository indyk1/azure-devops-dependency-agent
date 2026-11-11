using DependencyAgent.Core.Processes;

namespace DependencyAgent.AzureDevOps;

/// <summary>
/// Where the Azure DevOps collection lives and how to authenticate against it. Built from the pipeline
/// environment (<c>SYSTEM_COLLECTIONURI</c>, <c>SYSTEM_ACCESSTOKEN</c>) or, for local runs, <c>AZURE_DEVOPS_TOKEN</c>.
/// </summary>
public sealed class AzureDevOpsConnection
{
    public const string CollectionUriVariable = "SYSTEM_COLLECTIONURI";
    public const string PipelineTokenVariable = "SYSTEM_ACCESSTOKEN";
    public const string LocalTokenVariable = "AZURE_DEVOPS_TOKEN";

    private readonly Func<string, string, string>? _gitUrlFactory;

    /// <param name="collectionUri">Collection URL, e.g. <c>https://dev.azure.com/org/</c>.</param>
    /// <param name="token">Bearer token (job access token or an Entra token).</param>
    /// <param name="gitUrlFactory">Test/local hook replacing the repository clone URL: (project, repository) → URL.</param>
    public AzureDevOpsConnection(string collectionUri, string token, Func<string, string, string>? gitUrlFactory = null)
    {
        if (string.IsNullOrWhiteSpace(collectionUri)
            || !Uri.TryCreate(collectionUri.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException($"'{collectionUri}' is not a valid Azure DevOps collection URL (expected e.g. https://dev.azure.com/<org>/).", nameof(collectionUri));
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("An Azure DevOps access token is required.", nameof(token));
        }

        var text = uri.GetLeftPart(UriPartial.Path);
        CollectionUri = text.EndsWith('/') ? text : text + "/";
        Token = token;
        _gitUrlFactory = gitUrlFactory;
        (Organization, IdentityBaseUrl) = ParseOrganization(new Uri(CollectionUri));
    }

    /// <summary>Collection URL with trailing slash.</summary>
    public string CollectionUri { get; }

    public string Token { get; }

    /// <summary>Organization name for dev.azure.com / *.visualstudio.com collections; null for on-premises servers.</summary>
    public string? Organization { get; }

    /// <summary>Base URL of the identity (vssps) service, or null when it cannot be derived (on-premises).</summary>
    public string? IdentityBaseUrl { get; }

    /// <summary>
    /// Reads the connection from environment variables. <paramref name="collectionUriOverride"/> wins over
    /// <c>SYSTEM_COLLECTIONURI</c>. <paramref name="getVariable"/> exists for tests.
    /// </summary>
    public static AzureDevOpsConnection FromEnvironment(string? collectionUriOverride = null, Func<string, string?>? getVariable = null)
    {
        getVariable ??= SecretEnvironment.GetSecret;
        var collection = string.IsNullOrWhiteSpace(collectionUriOverride) ? getVariable(CollectionUriVariable) : collectionUriOverride;
        if (string.IsNullOrWhiteSpace(collection))
        {
            throw new InvalidOperationException(
                $"Azure DevOps collection URL is unknown. Run inside an Azure Pipelines job (sets {CollectionUriVariable}) or pass the collection URL explicitly.");
        }

        var token = getVariable(PipelineTokenVariable) ?? getVariable(LocalTokenVariable);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                $"No Azure DevOps token found. Map {PipelineTokenVariable}: $(System.AccessToken) in the pipeline step env, or set {LocalTokenVariable} for local runs.");
        }

        return new AzureDevOpsConnection(collection, token);
    }

    private static string Esc(string value) => Uri.EscapeDataString(value);

    /// <summary>Clone URL: <c>{collection}{project}/_git/{repo}</c> (no credentials embedded).</summary>
    public string GetGitUrl(string project, string repository) =>
        _gitUrlFactory?.Invoke(project, repository) ?? $"{CollectionUri}{Esc(project)}/_git/{Esc(repository)}";

    /// <summary>REST base for a repository: <c>{collection}{project}/_apis/git/repositories/{repo}</c>.</summary>
    public string GetRepositoryApiUrl(string project, string repository) =>
        $"{CollectionUri}{Esc(project)}/_apis/git/repositories/{Esc(repository)}";

    /// <summary>Browser URL of a pull request.</summary>
    public string GetPullRequestWebUrl(string project, string repository, int pullRequestId) =>
        $"{CollectionUri}{Esc(project)}/_git/{Esc(repository)}/pullrequest/{pullRequestId}";

    private static (string? Org, string? IdentityBase) ParseOrganization(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        if (host == "dev.azure.com")
        {
            var org = uri.Segments.Length > 1 ? uri.Segments[1].Trim('/') : "";
            return org.Length == 0 ? (null, null) : (org, $"https://vssps.dev.azure.com/{org}/");
        }

        if (host.EndsWith(".visualstudio.com", StringComparison.Ordinal))
        {
            var org = host[..host.IndexOf('.')];
            return (org, $"https://{org}.vssps.visualstudio.com/");
        }

        return (null, null);
    }
}
