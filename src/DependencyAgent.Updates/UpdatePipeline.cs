using DependencyAgent.Core;
using DependencyAgent.Updates.Packages;
using DependencyAgent.Updates.Steps;

namespace DependencyAgent.Updates;

/// <summary>Composes the deterministic update steps in their execution order.</summary>
public static class UpdatePipeline
{
    /// <summary>
    /// target-framework, global-json, nuget-packages, docker. <paramref name="versionSourceFactory"/> receives the
    /// repository root and is invoked once per repository when the package step runs.
    /// </summary>
    public static IReadOnlyList<IUpdateStep> CreateDefault(Func<string, IPackageVersionSource> versionSourceFactory)
    {
        ArgumentNullException.ThrowIfNull(versionSourceFactory);
        return
        [
            new TargetFrameworkUpdateStep(),
            new GlobalJsonUpdateStep(),
            new NuGetPackageUpdateStep(versionSourceFactory),
            new DockerImageUpdateStep(),
        ];
    }

    /// <summary>Uses <see cref="NuGetPackageVersionSource"/> (repository NuGet.config, source mapping, pipeline credentials).</summary>
    public static IReadOnlyList<IUpdateStep> CreateDefault() =>
        CreateDefault(root => new NuGetPackageVersionSource(root));
}
