namespace aida.Data;

public class ChatMessageRow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public string Role { get; set; } = default!;
    public string Text { get; set; } = "";

    /// <summary>
    /// <c>null</c> for a message the user or the model wrote; the ask_user exchange kind otherwise, so a
    /// reloaded transcript can render the question the tool asked and the answer it was given.
    /// </summary>
    public string? Kind { get; set; }

    public int Ordinal { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Conversation? Conversation { get; set; }
}