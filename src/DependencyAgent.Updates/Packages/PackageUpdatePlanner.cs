using DependencyAgent.Core.Configuration;
using NuGet.Versioning;

namespace DependencyAgent.Updates.Packages;

public enum PackageDecisionKind
{
    /// <summary>The package should move to <see cref="PackageUpdateDecision.NewVersion"/>.</summary>
    Update,

    /// <summary>Nothing newer is acceptable.</summary>
    UpToDate,

    /// <summary>Matched an <c>ignore</c> pattern.</summary>
    Ignored,

    /// <summary>Already at its configured pin.</summary>
    Pinned,

    /// <summary>The package source returned no versions.</summary>
    NoVersions,
}

/// <param name="NewVersion">The version to write, or null for no change.</param>
/// <param name="Reason">Short machine-friendly explanation (used for logs and tests).</param>
/// <param name="Note">Optional human-facing note for the PR (held back by pin, max-major, ignore, ...).</param>
public sealed record PackageUpdateDecision(PackageDecisionKind Kind, NuGetVersion? NewVersion, string Reason, string? Note = null);

/// <summary>Pure package-update policy: no IO, fully unit-testable.</summary>
public static class PackageUpdatePlanner
{
    public static PackageUpdateDecision Plan(
        string packageId,
        NuGetVersion current,
        IReadOnlyCollection<NuGetVersion> available,
        PackagePolicySettings policy,
        int targetMajor)
    {
        if (GlobMatcher.IsMatchAny(policy.Ignore, packageId))
        {
            return new PackageUpdateDecision(PackageDecisionKind.Ignored, null, "ignored by policy", $"{packageId} left at {current} (ignored by policy)");
        }

        if (TryGetPin(policy, packageId, out var pinText))
        {
            if (!NuGetVersion.TryParse(pinText, out var pin))
            {
                return new PackageUpdateDecision(PackageDecisionKind.UpToDate, null, "invalid pin", $"{packageId}: pin '{pinText}' is not a valid version; package left at {current}");
            }

            return pin.Equals(current)
                ? new PackageUpdateDecision(PackageDecisionKind.Pinned, null, "already at pin")
                : new PackageUpdateDecision(PackageDecisionKind.Update, pin, "pinned", $"{packageId} pinned to {Format(pin)} by policy");
        }

        if (available.Count == 0)
        {
            return new PackageUpdateDecision(PackageDecisionKind.NoVersions, null, "no versions found", $"No versions of {packageId} were found on the configured package sources; left at {current}");
        }

        var eligible = available.Where(v => policy.AllowPrerelease || !v.IsPrerelease).ToList();
        var overallBest = eligible.Count == 0 ? null : eligible.Max();

        var cap = MaxMajorFor(policy, packageId);
        if (cap is not null)
        {
            eligible = eligible.Where(v => v.Major <= cap).ToList();
        }

        NuGetVersion? chosen = null;
        var reason = "latest";
        if (GlobMatcher.IsMatchAny(policy.TrackTargetMajorFor, packageId))
        {
            var tracked = eligible.Where(v => v.Major == targetMajor).ToList();
            if (tracked.Count > 0)
            {
                chosen = tracked.Max();
                reason = "tracks target major";
            }
        }

        chosen ??= eligible.Count == 0 ? null : eligible.Max();

        string? note = null;
        if (cap is not null && overallBest is not null && overallBest.Major > cap && (chosen is null || overallBest > chosen) && overallBest > current)
        {
            note = $"{packageId} held back by max major {cap} (latest available is {Format(overallBest)})";
        }

        if (chosen is null || chosen <= current)
        {
            return new PackageUpdateDecision(PackageDecisionKind.UpToDate, null, "no newer acceptable version", note);
        }

        return new PackageUpdateDecision(PackageDecisionKind.Update, chosen, reason, note);
    }

    /// <summary>The text written to project files for a version.</summary>
    public static string Format(NuGetVersion version) => version.IsLegacyVersion ? version.ToFullString() : version.ToNormalizedString();

    private static bool TryGetPin(PackagePolicySettings policy, string packageId, out string pin)
    {
        foreach (var (id, version) in policy.Pins)
        {
            if (string.Equals(id, packageId, StringComparison.OrdinalIgnoreCase))
            {
                pin = version;
                return true;
            }
        }

        pin = "";
        return false;
    }

    private static int? MaxMajorFor(PackagePolicySettings policy, string packageId)
    {
        int? cap = null;
        foreach (var (pattern, max) in policy.MaxMajor)
        {
            if (GlobMatcher.IsMatch(pattern, packageId) && (cap is null || max < cap))
            {
                cap = max;
            }
        }

        return cap;
    }
}
