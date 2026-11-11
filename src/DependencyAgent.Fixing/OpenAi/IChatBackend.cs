namespace DependencyAgent.Fixing.OpenAi;

public enum ChatRole
{
    System,
    User,
    Assistant,
    Tool,
}

/// <summary>A function tool the model may call. <see cref="ParametersJsonSchema"/> is a JSON-schema object.</summary>
public sealed record ToolDefinition(string Name, string Description, string ParametersJsonSchema);

public sealed record ToolCallRequest(string Id, string Name, string ArgumentsJson);

/// <summary>Provider-neutral conversation message so the tool loop can be tested without the SDK.</summary>
public sealed record ChatMessageItem(ChatRole Role, string? Content, IReadOnlyList<ToolCallRequest>? ToolCalls = null, string? ToolCallId = null)
{
    public static ChatMessageItem System(string text) => new(ChatRole.System, text);

    public static ChatMessageItem User(string text) => new(ChatRole.User, text);

    public static ChatMessageItem Assistant(string? text, IReadOnlyList<ToolCallRequest>? calls = null) => new(ChatRole.Assistant, text, calls);

    public static ChatMessageItem Tool(string callId, string text) => new(ChatRole.Tool, text, null, callId);
}

/// <summary>One model response: either final text or tool calls.</summary>
/// <param name="Truncated">The model stopped because of the output length limit.</param>
public sealed record ChatTurn(string? Text, IReadOnlyList<ToolCallRequest> ToolCalls, bool Truncated = false);

/// <summary>Settings for creating a chat backend. ReasoningEffort is already validated (minimal/low/medium/high) or null.</summary>
public sealed record ChatBackendOptions(string Model, string ApiKey, string? Endpoint, string? ReasoningEffort = null);

/// <summary>Thin seam over the OpenAI SDK <c>ChatClient</c>.</summary>
public interface IChatBackend
{
    Task<ChatTurn> CompleteAsync(IReadOnlyList<ChatMessageItem> conversation, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken);
}
