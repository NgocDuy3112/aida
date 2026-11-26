using Bunit;

namespace aida.Tests.Components;

/// <summary>
/// Characterization of regenerate: it drops exactly the assistant replies that follow the last user
/// message, keeps that user message (and everything before it) and re-runs the same question.
/// </summary>
public class ChatRegenerateTests
{
    [Fact]
    public async Task Regenerate_drops_the_last_answer_and_answers_the_same_question_again()
    {
        using var harness = ChatTestHarness.Render();
        harness.Models.Answer("Đáp án", " một").Answer("Đáp án", " hai");

        await harness.StartSendAsync("Câu hỏi");
        harness.Page.WaitForAssertion(() => Assert.Equal(["Đáp án một"], harness.AssistantTexts()));

        await harness.RegenerateAsync();

        harness.Page.WaitForAssertion(() => Assert.Equal(["Đáp án hai"], harness.AssistantTexts()));
        Assert.Equal(["Câu hỏi"], harness.UserTexts());
        Assert.Equal(2, harness.Models.StreamingRequestCount);
    }

    [Fact]
    public async Task Regenerate_deletes_only_the_messages_after_the_last_user_message_in_the_store()
    {
        using var harness = ChatTestHarness.Render();
        harness.Models.Answer("Đáp án", " một").Answer("Đáp án", " hai");

        await harness.StartSendAsync("Câu hỏi");
        harness.Page.WaitForAssertion(() => Assert.Equal(1, harness.Store.Appended.Count(a => a.Role == "assistant")));

        await harness.RegenerateAsync();
        harness.Page.WaitForAssertion(() => Assert.Equal(2, harness.Models.StreamingRequestCount));

        var conversation = harness.ConversationId!.Value;
        // messages[0] is the system prompt, so the last user message sits at index 1.
        Assert.Contains(harness.Store.DeletedFrom, d => d.ConversationId == conversation && d.Ordinal == 2);
        Assert.Equal(
            ["user", "assistant"],
            harness.Store.MessagesOf(conversation).Select(r => r.Role));
        Assert.Equal("Câu hỏi", harness.Store.MessagesOf(conversation)[0].Text);
        Assert.Equal("Đáp án hai", harness.Store.MessagesOf(conversation)[1].Text);
    }

    [Fact]
    public async Task Regenerate_keeps_the_earlier_turns_of_the_conversation()
    {
        using var harness = ChatTestHarness.Render();
        harness.Models
            .Answer("Trả lời", " một")
            .Answer("Trả lời", " hai")
            .Answer("Trả lời", " ba");

        await harness.StartSendAsync("Câu hỏi 1");
        harness.Page.WaitForAssertion(() => Assert.Equal(["Trả lời một"], harness.AssistantTexts()));
        await harness.StartSendAsync("Câu hỏi 2");
        harness.Page.WaitForAssertion(() => Assert.Equal(["Trả lời một", "Trả lời hai"], harness.AssistantTexts()));

        await harness.RegenerateAsync();

        harness.Page.WaitForAssertion(() =>
            Assert.Equal(["Trả lời một", "Trả lời ba"], harness.AssistantTexts()));
        Assert.Equal(["Câu hỏi 1", "Câu hỏi 2"], harness.UserTexts());
        // The first turn is untouched: only the answer to the second question was dropped.
        var conversation = harness.ConversationId!.Value;
        Assert.Equal(
            ["user", "assistant", "user", "assistant"],
            harness.Store.MessagesOf(conversation).Select(r => r.Role));
        Assert.Equal(3, harness.Models.StreamingRequestCount);
    }
}
