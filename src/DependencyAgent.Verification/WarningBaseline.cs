using DependencyAgent.Core.Model;

namespace DependencyAgent.Verification;

/// <summary>Compares warnings of the upgraded build against the baseline (pre-upgrade) build.</summary>
public static class WarningBaseline
{
    /// <summary>
    /// Warnings in <paramref name="current"/> that are not covered by <paramref name="baseline"/>.
    /// Comparison is a multiset comparison on <see cref="BuildDiagnostic.Fingerprint"/> (code, file, message — line numbers
    /// are ignored): if the baseline had two identical warnings and the current build has three, one is new.
    /// Errors are ignored; only warnings are compared.
    /// <para>
    /// A baseline that could not be produced (null, or the baseline build failed) must be handled by the caller — this method
    /// cannot tell "no warnings" from "unknown". Callers should skip the new-warnings check in that case rather than pass an
    /// empty baseline, which would report every warning as new.
    /// </para>
    /// </summary>
    public static IReadOnlyList<BuildDiagnostic> NewWarnings(IEnumerable<BuildDiagnostic> baseline, IEnumerable<BuildDiagnostic> current)
    {
        var budget = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var d in baseline.Where(d => d.Severity == DiagnosticSeverity.Warning))
        {
            budget[d.Fingerprint] = budget.GetValueOrDefault(d.Fingerprint) + 1;
        }

        var result = new List<BuildDiagnostic>();
        foreach (var d in current.Where(d => d.Severity == DiagnosticSeverity.Warning))
        {
            if (budget.TryGetValue(d.Fingerprint, out var remaining) && remaining > 0)
            {
                budget[d.Fingerprint] = remaining - 1;
            }
            else
            {
                result.Add(d);
            }
        }

        return result;
    }
}
