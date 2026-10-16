namespace CardCrafter.Core.Assistant;

/// <summary>A conversation with an AI model about the user's cards, whichever provider runs it.</summary>
public interface IAssistantChat
{
    /// <summary>
    /// Sends the user's message and runs the tool calls it leads to. Updates arrive as they happen. When the turn fails or
    /// is stopped, the conversation goes back to how it was before the message (changes already made to cards stay).
    /// </summary>
    Task Send(string text, Action<AssistantUpdate> update, CancellationToken cancel = default);

    /// <summary>Forgets the conversation, keeping the key and settings.</summary>
    void Clear();

    bool IsEmpty { get; }

    /// <summary>Tokens used so far in this conversation, as the provider counted them.</summary>
    long InputTokens { get; }
    long OutputTokens { get; }
    long CachedTokens { get; }
}
