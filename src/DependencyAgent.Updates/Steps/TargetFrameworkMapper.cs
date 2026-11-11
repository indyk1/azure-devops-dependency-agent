using System.Text.RegularExpressions;

namespace DependencyAgent.Updates.Steps;

internal enum TfmKind
{
    /// <summary>netcoreapp*, net5.0 and newer.</summary>
    Modern,

    /// <summary>net20..net481.</summary>
    NetFramework,

    NetStandard,

    /// <summary>Anything else (uap, monoandroid, property references, ...).</summary>
    Other,
}

internal readonly record struct TfmMapResult(string Value, bool Changed, string? Note);

/// <summary>Pure mapping of one target framework moniker to the target moniker.</summary>
internal static partial class TargetFrameworkMapper
{
    [GeneratedRegex(@"^(?:net(?<major>\d+)\.(?<minor>\d+)|netcoreapp(?<cmajor>\d+)\.(?<cminor>\d+))(?:-(?<platform>[A-Za-z]+)(?<pver>[\d.]*))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModernRegex();

    [GeneratedRegex(@"^netstandard\d+\.\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NetStandardRegex();

    [GeneratedRegex(@"^net\d{2,3}(?:-.+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NetFrameworkRegex();

    /// <summary>Parses the target moniker (<c>net10.0</c>) into its major and minor version.</summary>
    public static bool TryParseTarget(string target, out int major, out int minor)
    {
        major = minor = 0;
        var m = ModernRegex().Match(target.Trim());
        if (!m.Success || !m.Groups["major"].Success)
        {
            return false;
        }

        major = int.Parse(m.Groups["major"].Value, System.Globalization.CultureInfo.InvariantCulture);
        minor = int.Parse(m.Groups["minor"].Value, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    public static TfmKind Classify(string tfm)
    {
        var t = tfm.Trim();
        if (t.Contains("$(", StringComparison.Ordinal))
        {
            return TfmKind.Other;
        }

        var m = ModernRegex().Match(t);
        if (m.Success)
        {
            return m.Groups["cmajor"].Success || int.Parse(m.Groups["major"].Value, System.Globalization.CultureInfo.InvariantCulture) >= 5
                ? TfmKind.Modern
                : TfmKind.Other;
        }

        if (NetStandardRegex().IsMatch(t))
        {
            return TfmKind.NetStandard;
        }

        return NetFrameworkRegex().IsMatch(t) ? TfmKind.NetFramework : TfmKind.Other;
    }

    /// <summary>
    /// Maps one moniker. Monikers at or above the target major, .NET Framework, .NET Standard and unknown monikers are
    /// returned unchanged.
    /// </summary>
    public static TfmMapResult Map(string tfm, int targetMajor, int targetMinor)
    {
        var t = tfm.Trim();
        var m = ModernRegex().Match(t);
        if (!m.Success || Classify(t) != TfmKind.Modern)
        {
            return new TfmMapResult(t, false, null);
        }

        var isCoreApp = m.Groups["cmajor"].Success;
        var major = int.Parse(isCoreApp ? m.Groups["cmajor"].Value : m.Groups["major"].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (!isCoreApp && major >= targetMajor)
        {
            return new TfmMapResult(t, false, null);
        }

        var notes = new List<string>();
        if (isCoreApp && major <= 2)
        {
            notes.Add($"{t} is a very old target (.NET Core {major}.x); the jump to net{targetMajor}.{targetMinor} may need significant code changes");
        }

        var result = $"net{targetMajor}.{targetMinor}";
        if (m.Groups["platform"].Success)
        {
            var platform = m.Groups["platform"].Value;
            result += "-" + platform;
            if (m.Groups["pver"].Value.Length > 0)
            {
                notes.Add($"Dropped platform version '{platform}{m.Groups["pver"].Value}' while moving {t} to {result}; review the minimum platform version");
            }
        }

        return new TfmMapResult(result, true, notes.Count == 0 ? null : string.Join("; ", notes));
    }
}
