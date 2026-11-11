using DependencyAgent.Core.Configuration;
using DependencyAgent.Core.Model;

namespace DependencyAgent.Fixing;

/// <summary>Resolves the effective model and reasoning effort for a provider (request, then <c>ai.*</c>, then provider default).</summary>
public static class AiRunDefaults
{
    /// <summary>Reasoning levels the Copilot CLI accepts (<c>copilot --help</c>, <c>--reasoning-effort</c>).</summary>
    public static readonly IReadOnlyList<string> CopilotEfforts = ["none", "minimal", "low", "medium", "high", "xhigh", "max"];

    /// <summary>Reasoning levels mapped for the OpenAI chat-completions API.</summary>
    public static readonly IReadOnlyList<string> OpenAiEfforts = ["minimal", "low", "medium", "high"];

    public static string EffectiveModel(string provider, AiRunRequest? request, AgentSettings settings)
    {
        var configured = FirstNonBlank(request?.Model, settings.Ai.Model);
        if (configured is not null)
        {
            return configured;
        }

        return provider.Equals("openai", StringComparison.OrdinalIgnoreCase) ? settings.Ai.OpenAi.DefaultModel : settings.Ai.Copilot.Model;
    }

    /// <summary>The requested effort (trimmed, lower-case) before validation; null = provider default.</summary>
    public static string? RequestedEffort(string provider, AiRunRequest? request, AgentSettings settings)
    {
        var providerDefault = provider.Equals("openai", StringComparison.OrdinalIgnoreCase) ? settings.Ai.OpenAi.ReasoningEffort : settings.Ai.Copilot.ReasoningEffort;
        return FirstNonBlank(request?.ReasoningEffort, settings.Ai.ReasoningEffort, providerDefault)?.ToLowerInvariant();
    }

    /// <summary>Returns the effort when the provider supports it; otherwise null and a note explaining why it is ignored.</summary>
    public static (string? Effort, string? Note) ValidateEffort(string provider, string? requested)
    {
        if (requested is null)
        {
            return (null, null);
        }

        var allowed = provider.Equals("openai", StringComparison.OrdinalIgnoreCase) ? OpenAiEfforts : CopilotEfforts;
        return allowed.Contains(requested, StringComparer.Ordinal)
            ? (requested, null)
            : (null, $"reasoning effort '{requested}' is not supported by provider '{provider}' (allowed: {string.Join(", ", allowed)}); using the provider default");
    }

    private static string? FirstNonBlank(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
