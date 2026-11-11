using System.Collections.Concurrent;
using System.Text.Json;
using DependencyAgent.Core.Processes;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using NullLogger = NuGet.Common.NullLogger;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace DependencyAgent.Updates.Packages;

/// <summary>Looks up the listed versions of a package.</summary>
public interface IPackageVersionSource
{
    /// <summary>All listed (not unlisted) versions, including prereleases. Throws when no source could be queried.</summary>
    Task<IReadOnlyList<NuGetVersion>> GetVersionsAsync(string packageId, CancellationToken ct);
}

/// <summary>
/// Production version source. Uses the repository's NuGet.config chain, honours package source mapping (only mapped
/// sources are queried for an id, which protects against dependency confusion), injects Azure Pipelines feed
/// credentials and excludes unlisted versions.
/// </summary>
public sealed class NuGetPackageVersionSource : IPackageVersionSource
{
    private const int MaxParallelRequests = 8;

    private readonly ILogger? _logger;
    private readonly Func<string, string?> _getVariable;
    private readonly IReadOnlyList<PackageSource> _sources;
    private readonly PackageSourceMapping _mapping;
    private readonly SemaphoreSlim _throttle = new(MaxParallelRequests);
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<NuGetVersion>>>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public NuGetPackageVersionSource(string repositoryRoot, ILogger? logger = null)
        : this(Settings.LoadDefaultSettings(repositoryRoot), logger, SecretEnvironment.GetSecret)
    {
    }

    internal NuGetPackageVersionSource(ISettings settings, ILogger? logger, Func<string, string?> getVariable)
    {
        _logger = logger;
        _getVariable = getVariable;
        _mapping = PackageSourceMapping.GetPackageSourceMapping(settings);
        var provider = new PackageSourceProvider(settings);
        _sources = provider.LoadPackageSources()
            .Where(s => s.IsEnabled)
            .Select(s => NuGetFeedCredentials.Apply(s, getVariable))
            .ToList();
    }

    public Task<IReadOnlyList<NuGetVersion>> GetVersionsAsync(string packageId, CancellationToken ct)
    {
        var lazy = _cache.GetOrAdd(packageId, id => new Lazy<Task<IReadOnlyList<NuGetVersion>>>(() => FetchAsync(id, CancellationToken.None)));
        var task = lazy.Value;
        if (task.IsFaulted)
        {
            _cache.TryRemove(new KeyValuePair<string, Lazy<Task<IReadOnlyList<NuGetVersion>>>>(packageId, lazy));
        }

        return task.WaitAsync(ct);
    }

    /// <summary>The enabled sources that may be queried for <paramref name="packageId"/> (after source mapping).</summary>
    internal IReadOnlyList<PackageSource> SelectSources(string packageId)
    {
        if (!_mapping.IsEnabled)
        {
            return _sources;
        }

        var mapped = _mapping.GetConfiguredPackageSources(packageId);
        return _sources.Where(s => mapped.Contains(s.Name, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    private async Task<IReadOnlyList<NuGetVersion>> FetchAsync(string packageId, CancellationToken ct)
    {
        var sources = SelectSources(packageId);
        if (sources.Count == 0)
        {
            throw new InvalidOperationException(_mapping.IsEnabled
                ? $"no package source is mapped for '{packageId}' (package source mapping)"
                : "no enabled package sources are configured");
        }

        var results = await Task.WhenAll(sources.Select(async source =>
        {
            try
            {
                return (Versions: await QuerySourceAsync(source, packageId, ct).ConfigureAwait(false), Error: (Exception?)null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning("Package source '{Source}' failed for {PackageId}: {Message}", source.Name, packageId, ex.Message);
                return (Versions: (IReadOnlyList<NuGetVersion>)[], Error: ex);
            }
        })).ConfigureAwait(false);

        if (results.All(r => r.Error is not null))
        {
            throw new InvalidOperationException($"all {results.Length} package source(s) failed: {results[0].Error!.Message}", results[0].Error);
        }

        return results.SelectMany(r => r.Versions).Distinct().OrderBy(v => v).ToList();
    }

    private async Task<IReadOnlyList<NuGetVersion>> QuerySourceAsync(PackageSource source, string packageId, CancellationToken ct)
    {
        await _throttle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var repository = Repository.Factory.GetCoreV3(source);
            var resource = await repository.GetResourceAsync<PackageMetadataResource>(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"source '{source.Name}' does not support package metadata");
            using var cache = new SourceCacheContext();
            var metadata = await resource.GetMetadataAsync(packageId, includePrerelease: true, includeUnlisted: false, cache, NullLogger.Instance, ct).ConfigureAwait(false);
            return metadata.Where(m => m.IsListed).Select(m => m.Identity.Version).ToList();
        }
        finally
        {
            _throttle.Release();
        }
    }
}

/// <summary>Applies Azure Pipelines / Azure Artifacts credentials from environment variables to package sources.</summary>
internal static class NuGetFeedCredentials
{
    private const string DefaultUserName = "VssSessionToken";

    public static PackageSource Apply(PackageSource source, Func<string, string?> getVariable)
    {
        if (source.Credentials is not null || !source.IsHttp)
        {
            return source;
        }

        if (FindExternalEndpoint(source, getVariable) is { } external)
        {
            return WithCredentials(source, external.UserName, external.Password);
        }

        var token = getVariable("VSS_NUGET_ACCESSTOKEN");
        var prefixes = getVariable("VSS_NUGET_URI_PREFIXES");
        if (token is not null && prefixes is not null
            && prefixes.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(p => source.Source.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            return WithCredentials(source, DefaultUserName, token);
        }

        return source;
    }

    private static PackageSource WithCredentials(PackageSource source, string userName, string password)
    {
        var clone = source.Clone();
        clone.Credentials = new PackageSourceCredential(source.Name, userName, password, isPasswordClearText: true, validAuthenticationTypesText: null);
        return clone;
    }

    private static (string UserName, string Password)? FindExternalEndpoint(PackageSource source, Func<string, string?> getVariable)
    {
        var json = getVariable("VSS_NUGET_EXTERNAL_FEED_ENDPOINTS");
        if (json is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("endpointCredentials", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var item in list.EnumerateArray())
            {
                var endpoint = item.TryGetProperty("endpoint", out var e) ? e.GetString() : null;
                var password = item.TryGetProperty("password", out var p) ? p.GetString() : null;
                if (endpoint is null || password is null || !source.Source.StartsWith(endpoint, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var user = item.TryGetProperty("username", out var u) ? u.GetString() : null;
                return (string.IsNullOrEmpty(user) ? DefaultUserName : user, password);
            }
        }
        catch (JsonException)
        {
            // Malformed endpoint list: treat as absent (never echo its content, it holds secrets).
        }

        return null;
    }
}
