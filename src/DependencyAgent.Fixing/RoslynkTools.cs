namespace DependencyAgent.Fixing;

/// <summary>Knowledge about the Roslynk MCP server's tools (https://github.com/mrpmorris/Roslynk, version 3.0.0).</summary>
public static class RoslynkTools
{
    /// <summary>Tools that modify files (all accept <c>checkOnly=true</c> for a preview). Denied when <c>ai.roslynk.allowWriteTools</c> is false.</summary>
    public static readonly IReadOnlyList<string> WriteTools =
    [
        "apply_code_action", "apply_code_fix", "apply_patch", "rename_symbol", "rename_parameter",
        "change_signature", "extract_method", "remove_unused_usings",
    ];

    public static bool IsWriteTool(string name) => WriteTools.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Prefix of the function names the OpenAI provider exposes (<c>roslynk__find_references</c>).</summary>
    public const string OpenAiPrefix = "roslynk__";
}
