#pragma warning disable OPENAI001 // ReasoningEffortLevel is marked experimental in OpenAI 2.15; isolated to this file.
using System.ClientModel;
using OpenAI;
using OpenAI.Chat;

namespace DependencyAgent.Fixing.OpenAi;

/// <summary>
/// <see cref="IChatBackend"/> over the OpenAI .NET SDK (2.15) <see cref="ChatClient"/>. Works with api.openai.com and any
/// OpenAI-compatible endpoint (including Azure OpenAI's <c>/openai/v1</c> route) via <c>OpenAIClientOptions.Endpoint</c>.
/// </summary>
public sealed class OpenAiChatBackend : IChatBackend
{
    private readonly ChatClient _client;
    private readonly string? _reasoningEffort;

    public OpenAiChatBackend(string model, string apiKey, string? endpoint, string? reasoningEffort = null)
    {
        _reasoningEffort = reasoningEffort;
        var options = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            options.Endpoint = new Uri(endpoint, UriKind.Absolute);
        }

        _client = new ChatClient(model, new ApiKeyCredential(apiKey), options);
    }

    public async Task<ChatTurn> CompleteAsync(IReadOnlyList<ChatMessageItem> conversation, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>(conversation.Count);
        foreach (var m in conversation)
        {
            switch (m.Role)
            {
                case ChatRole.System:
                    messages.Add(new SystemChatMessage(m.Content ?? ""));
                    break;
                case ChatRole.User:
                    messages.Add(new UserChatMessage(m.Content ?? ""));
                    break;
                case ChatRole.Assistant when m.ToolCalls is { Count: > 0 }:
                    var calls = m.ToolCalls.Select(c => ChatToolCall.CreateFunctionToolCall(c.Id, c.Name, BinaryData.FromString(c.ArgumentsJson)));
                    var assistant = new AssistantChatMessage(calls);
                    if (!string.IsNullOrEmpty(m.Content))
                    {
                        assistant.Content.Add(ChatMessageContentPart.CreateTextPart(m.Content));
                    }

                    messages.Add(assistant);
                    break;
                case ChatRole.Assistant:
                    messages.Add(new AssistantChatMessage(m.Content ?? ""));
                    break;
                case ChatRole.Tool:
                    messages.Add(new ToolChatMessage(m.ToolCallId!, m.Content ?? ""));
                    break;
            }
        }

        var options = new ChatCompletionOptions();
        if (MapReasoningEffort(_reasoningEffort) is { } effort)
        {
            options.ReasoningEffortLevel = effort;
        }

        foreach (var t in tools)
        {
            options.Tools.Add(ChatTool.CreateFunctionTool(t.Name, t.Description, BinaryData.FromString(t.ParametersJsonSchema)));
        }

        ChatCompletion completion = (await _client.CompleteChatAsync(messages, options, cancellationToken)).Value;

        if (completion.FinishReason == ChatFinishReason.ToolCalls && completion.ToolCalls.Count > 0)
        {
            var calls = completion.ToolCalls
                .Select(c => new ToolCallRequest(c.Id, c.FunctionName, c.FunctionArguments.ToString()))
                .ToList();
            return new ChatTurn(TextOf(completion), calls);
        }

        return new ChatTurn(TextOf(completion), [], completion.FinishReason == ChatFinishReason.Length);
    }

    /// <summary>Maps minimal/low/medium/high to the SDK level; anything else (including null) yields null = omit.</summary>
    public static ChatReasoningEffortLevel? MapReasoningEffort(string? effort)
    {
        switch (effort?.Trim().ToLowerInvariant())
        {
            case "minimal": return ChatReasoningEffortLevel.Minimal;
            case "low": return ChatReasoningEffortLevel.Low;
            case "medium": return ChatReasoningEffortLevel.Medium;
            case "high": return ChatReasoningEffortLevel.High;
            default: return null;
        }
    }

    private static string? TextOf(ChatCompletion completion) =>
        completion.Content.Count == 0 ? null : string.Concat(completion.Content.Where(p => p.Kind == ChatMessageContentPartKind.Text).Select(p => p.Text));
}
