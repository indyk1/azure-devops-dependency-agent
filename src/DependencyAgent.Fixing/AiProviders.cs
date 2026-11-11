using DependencyAgent.Core;
using DependencyAgent.Core.Processes;
using DependencyAgent.Fixing.OpenAi;

namespace DependencyAgent.Fixing;

public static class AiProviders
{
    /// <summary>The built-in providers: <c>copilot</c> (Copilot CLI) and <c>openai</c> (OpenAI-compatible API).</summary>
    public static IReadOnlyList<IAiProvider> CreateDefault(IProcessRunner runner) =>
        [new CopilotCliProvider(runner), new OpenAiProvider()];
}
