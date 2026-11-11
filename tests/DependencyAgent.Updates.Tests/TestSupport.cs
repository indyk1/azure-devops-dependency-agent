using System.Text;
using DependencyAgent.Core.Configuration;
using DependencyAgent.Core.Model;
using DependencyAgent.Updates.Packages;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Versioning;

namespace DependencyAgent.Updates.Tests;

internal sealed class TestRepo : IDisposable
{
    public TestRepo()
    {
        Root = Path.Combine(Path.GetTempPath(), "depagent-updates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public AgentSettings Settings { get; } = new();

    public void Write(string relative, string content, bool bom = false)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var body = new UTF8Encoding(false).GetBytes(content);
        File.WriteAllBytes(path, bom ? [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. body] : body);
    }

    public string Read(string relative)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Root, relative));
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return new UTF8Encoding(false).GetString(bytes, offset, bytes.Length - offset);
    }

    public byte[] ReadBytes(string relative) => File.ReadAllBytes(Path.Combine(Root, relative));

    public RepoContext Context() => new()
    {
        Entry = new RepositoryEntry { Project = "P", Repository = "R" },
        Settings = Settings,
        WorkingDirectory = Root,
        Options = new RunOptions(),
        Logger = NullLogger.Instance,
    };

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal sealed class FakeVersionSource : IPackageVersionSource
{
    private readonly Dictionary<string, string[]> _versions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failing = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Requests { get; } = [];

    public FakeVersionSource Add(string id, params string[] versions)
    {
        _versions[id] = versions;
        return this;
    }

    public FakeVersionSource Fail(string id)
    {
        _failing.Add(id);
        return this;
    }

    public Task<IReadOnlyList<NuGetVersion>> GetVersionsAsync(string packageId, CancellationToken ct)
    {
        lock (Requests)
        {
            Requests.Add(packageId);
        }

        if (_failing.Contains(packageId))
        {
            throw new InvalidOperationException("boom");
        }

        IReadOnlyList<NuGetVersion> result = _versions.TryGetValue(packageId, out var v) ? v.Select(NuGetVersion.Parse).ToList() : [];
        return Task.FromResult(result);
    }
}

internal static class Versions
{
    public static NuGetVersion[] Of(params string[] versions) => versions.Select(NuGetVersion.Parse).ToArray();
}
