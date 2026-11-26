using aida.Data;

namespace aida.Services.Conversations;

/// <param name="Kind">
/// <c>null</c> for a message the user or the model wrote; the ask_user exchange kind otherwise.
/// </param>
public record ChatMessageRecord(string Role, string Text, string? Kind = null);

public record ConversationSummary(Guid Id, string Title, DateTimeOffset UpdatedAt);

public interface IConversationStore
{
    Task<IReadOnlyList<ConversationSummary>> ListAsync(string userId, CancellationToken ct = default);
    Task<ConversationSummary> CreateAsync(string userId, CancellationToken ct = default);
    Task<IReadOnlyList<ChatMessageRecord>> LoadMessagesAsync(Guid conversationId, string userId, CancellationToken ct = default);
    Task AppendAsync(Guid conversationId, string userId, string role, string text, string? kind = null, CancellationToken ct = default);

    /// <summary>
    /// Appends <paramref name="rows"/> in order in one write, so they either all land with consecutive
    /// ordinals or none of them does.
    /// </summary>
    Task AppendManyAsync(Guid conversationId, string userId, IReadOnlyList<ChatMessageRecord> rows, CancellationToken ct = default);
    Task UpdateMessageAsync(Guid conversationId, string userId, int ordinal, string text, CancellationToken ct = default);
    Task DeleteMessagesFromAsync(Guid conversationId, string userId, int ordinal, CancellationToken ct = default);
    Task RenameAsync(Guid conversationId, string userId, string title, CancellationToken ct = default);
    Task DeleteAsync(Guid conversationId, string userId, CancellationToken ct = default);
}
