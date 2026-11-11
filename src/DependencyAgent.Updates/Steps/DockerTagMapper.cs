using System.Globalization;
using System.Text.RegularExpressions;

namespace DependencyAgent.Updates.Steps;

/// <summary>Result of mapping one image tag. <see cref="NewTag"/> is null when the tag is left unchanged.</summary>
internal sealed record DockerTagMapResult(string? NewTag, IReadOnlyList<string> Notes);

/// <summary>Pure mapping of an official .NET image tag to the tag for the target major version.</summary>
internal static partial class DockerTagMapper
{
    [GeneratedRegex(@"^(?<major>\d+)\.(?<minor>\d+)(?:\.(?<patch>\d+))?(?:-(?<variant>.+))?$", RegexOptions.CultureInvariant)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"^(?<core>.+?)(?<arch>-(?:amd64|arm64v8|arm32v7|arm64))?$", RegexOptions.CultureInvariant)]
    private static partial Regex ArchRegex();

    [GeneratedRegex(@"^(?:bookworm|bullseye|buster|stretch)(?:-slim)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DebianRegex();

    [GeneratedRegex(@"^jammy(?<rest>-chiseled(?:-[a-z0-9]+)*)$", RegexOptions.CultureInvariant)]
    private static partial Regex JammyChiseledRegex();

    [GeneratedRegex(@"^noble(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex NobleRegex();

    [GeneratedRegex(@"^alpine(?<version>\d+\.\d+)?(?<rest>(?:-[a-z0-9]+)*)$", RegexOptions.CultureInvariant)]
    private static partial Regex AlpineRegex();

    [GeneratedRegex(@"^cbl-mariner2\.0(?<rest>(?:-[a-z0-9]+)*)$", RegexOptions.CultureInvariant)]
    private static partial Regex MarinerRegex();

    [GeneratedRegex(@"^azurelinux3\.0(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex AzureLinuxRegex();

    public static bool IsVersionLike(string tag) => TagRegex().IsMatch(tag);

    public static DockerTagMapResult Map(string tag, int targetMajor)
    {
        var m = TagRegex().Match(tag);
        if (!m.Success || int.Parse(m.Groups["major"].Value, CultureInfo.InvariantCulture) >= targetMajor)
        {
            return new DockerTagMapResult(null, []);
        }

        var notes = new List<string>();
        var variant = m.Groups["variant"].Value;
        string newVariant;
        if (variant.Length == 0)
        {
            newVariant = "";
        }
        else if (MapVariant(variant, notes) is { } mapped)
        {
            newVariant = mapped;
        }
        else
        {
            return new DockerTagMapResult(null, notes);
        }

        if (m.Groups["patch"].Success)
        {
            notes.Add($"Patch pin dropped from tag '{tag}'; the image now floats on {targetMajor}.0");
        }

        var newTag = $"{targetMajor}.0" + (newVariant.Length > 0 ? "-" + newVariant : "");
        return newTag == tag ? new DockerTagMapResult(null, []) : new DockerTagMapResult(newTag, notes);
    }

    /// <summary>Returns the new variant, or null when the variant cannot be mapped (a note is then added).</summary>
    private static string? MapVariant(string variant, List<string> notes)
    {
        var arch = ArchRegex().Match(variant);
        var core = arch.Groups["core"].Value;
        var suffix = arch.Groups["arch"].Value;

        if (core.StartsWith("nanoserver", StringComparison.Ordinal) || core.StartsWith("windowsservercore", StringComparison.Ordinal))
        {
            notes.Add($"Windows image variant '{variant}' left unchanged; update it manually");
            return null;
        }

        if (DebianRegex().IsMatch(core))
        {
            notes.Add($"Base OS changes from Debian ('{variant}') to Ubuntu 24.04 (noble) because Debian images are no longer published for .NET 10; review the image");
            return "noble" + suffix;
        }

        if (core is "jammy" or "focal")
        {
            return "noble" + suffix;
        }

        if (JammyChiseledRegex().Match(core) is { Success: true } jammy)
        {
            return "noble" + jammy.Groups["rest"].Value + suffix;
        }

        if (NobleRegex().IsMatch(core) || AzureLinuxRegex().IsMatch(core))
        {
            return variant;
        }

        if (AlpineRegex().Match(core) is { Success: true } alpine)
        {
            if (alpine.Groups["version"].Success)
            {
                notes.Add($"Pinned Alpine version {alpine.Groups["version"].Value} was dropped from '{variant}'; the image now uses the default Alpine release");
            }

            return "alpine" + alpine.Groups["rest"].Value + suffix;
        }

        if (MarinerRegex().Match(core) is { Success: true } mariner)
        {
            notes.Add($"Base OS changes from CBL-Mariner 2.0 ('{variant}') to Azure Linux 3.0; review the image");
            return "azurelinux3.0" + mariner.Groups["rest"].Value + suffix;
        }

        notes.Add($"Unknown image variant '{variant}' left unchanged; update it manually");
        return null;
    }
}
