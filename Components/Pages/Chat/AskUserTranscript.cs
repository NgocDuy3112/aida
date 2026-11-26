using aida.Services.Agents;
using Microsoft.Extensions.AI;

namespace aida.Components.Pages.Chat;

/// <summary>
/// Marks the transcript rows an ask_user exchange turns into. The question is an assistant row and the
/// answer a user row, so the stored history reads to the model as the exchange it really was; the kind
/// keeps the transcript from rendering them as a plain answer and a plain user message.
/// </summary>
internal static class AskUserTranscript
{
    /// <summary>The assistant row holding the question the tool asked.</summary>
    internal const string QuestionKind = "ask_user_question";

    /// <summary>The user row holding the text the user answered with.</summary>
    internal const string AnswerKind = "ask_user_answer";

    /// <summary>The user row recording a skipped question; its text is the decline the model read.</summary>
    internal const string DeclinedKind = "ask_user_declined";

    /// <summary>The line the transcript shows for a skipped question.</summary>
    internal const string DeclinedText = "Người dùng bỏ qua câu hỏi.";

    internal static ChatMessage Question(string question) =>
        new(ChatRole.Assistant, question) { AuthorName = QuestionKind };

    internal static ChatMessage Answer(AskUserOutcome outcome, string answer) =>
        new(ChatRole.User, answer) { AuthorName = KindOf(outcome) };

    /// <summary>The stored kind of the answer row for <paramref name="outcome"/>.</summary>
    internal static string KindOf(AskUserOutcome outcome) =>
        outcome == AskUserOutcome.Declined ? DeclinedKind : AnswerKind;

    internal static bool IsQuestion(ChatMessage message) => message.AuthorName == QuestionKind;

    internal static bool IsExchange(ChatMessage message) =>
        message.AuthorName is QuestionKind or AnswerKind or DeclinedKind;

    internal static bool IsDeclined(ChatMessage message) => message.AuthorName == DeclinedKind;

    /// <summary>What the transcript shows for <paramref name="message"/>; a skipped question reads as one.</summary>
    internal static string DisplayText(ChatMessage message) =>
        IsDeclined(message) ? DeclinedText : message.Text ?? string.Empty;

    /// <summary>Applies a stored kind to the row it belongs to; an unknown kind stays a normal message.</summary>
    internal static ChatMessage Restore(string role, string text, string? kind)
    {
        var message = new ChatMessage(role == "assistant" ? ChatRole.Assistant : ChatRole.User, text);
        if (kind is QuestionKind or AnswerKind or DeclinedKind)
            message.AuthorName = kind;
        return message;
    }
}
