using aida.Components.Pages.Chat;
using aida.Services.Agents;
using Bunit;
using Microsoft.Extensions.AI;

namespace aida.Tests.Components;

/// <summary>
/// An ask_user exchange is part of the conversation, not of one turn: it is stored as a question row and
/// an answer row, it comes back when the conversation is reloaded, and the model reads it once as history.
/// </summary>
public class ChatAskUserPersistTests
{
    private const string Question = "Bạn muốn tóm tắt hay liệt kê?";
    private const string Answer = "Liệt kê";
    private const string Ask = "Tóm tắt tài liệu này";
    private const string Reply = "Đây là danh sách.";

    /// <summary>Stores one answered exchange, the way the runner stores it, and returns its conversation.</summary>
    private static async Task<Guid> SeedExchangeAsync(FakeConversationStore store)
    {
        var conversation = await store.CreateAsync(ChatTestHarness.UserId);
        await store.AppendAsync(conversation.Id, ChatTestHarness.UserId, "user", Ask);
        await store.AppendAsync(
            conversation.Id, ChatTestHarness.UserId, "assistant", Question, AskUserTranscript.QuestionKind);
        await store.AppendAsync(
            conversation.Id, ChatTestHarness.UserId, "user", Answer, AskUserTranscript.AnswerKind);
        await store.AppendAsync(conversation.Id, ChatTestHarness.UserId, "assistant", Reply);
        return conversation.Id;
    }

    [Fact]
    public async Task An_answered_question_is_stored_beside_the_turn_and_survives_a_reload()
    {
        var store = new FakeConversationStore();
        var conversation = await SeedExchangeAsync(store);

        using var harness = ChatTestHarness.Render(store: store);
        await harness.OpenAsync(conversation);

        // The reloaded transcript shows the question the tool asked and the answer it was given.
        harness.Page.WaitForAssertion(() => Assert.Contains(Question, harness.Page.Markup));
        Assert.Contains(Answer, harness.Page.Markup);
        Assert.Contains("Câu hỏi", harness.Page.Markup);
        Assert.Contains("Trả lời câu hỏi", harness.Page.Markup);
        Assert.Equal([Question, Reply], harness.AssistantTexts());
        Assert.Equal([Ask, Answer], harness.UserTexts());
        // The stored question is not the answer to regenerate: the regenerate button belongs to the reply.
        Assert.Single(harness.Page.FindAll(".message-regenerate-btn"));
    }

    [Fact]
    public async Task The_next_turn_after_a_reload_reads_the_question_and_the_answer_once()
    {
        var store = new FakeConversationStore();
        var conversation = await SeedExchangeAsync(store);

        using var harness = ChatTestHarness.Render(store: store);
        harness.Models.Answer("Tiếp tục đây.").Answer("Vẫn tiếp tục.");
        await harness.OpenAsync(conversation);

        await harness.StartSendAsync("Làm tiếp");
        harness.Page.WaitForAssertion(() => Assert.Equal(1, harness.Models.StreamingRequestCount));

        var reloadedTurn = harness.Models.StreamingBodies[0];
        Assert.Equal(1, Occurrences(reloadedTurn, Question));
        Assert.Equal(1, Occurrences(reloadedTurn, Answer));

        // The same conversation without a reload keeps the exchange in the agent session, not in the
        // history the runner sends, so the model still reads it once.
        await harness.StartSendAsync("Làm tiếp nữa");
        harness.Page.WaitForAssertion(() => Assert.Equal(2, harness.Models.StreamingRequestCount));

        var keptSessionTurn = harness.Models.StreamingBodies[1];
        Assert.Equal(1, Occurrences(keptSessionTurn, Question));
        Assert.Equal(1, Occurrences(keptSessionTurn, Answer));
    }

    [Fact]
    public async Task An_answer_the_user_gives_mid_turn_is_stored_before_the_turn_ends()
    {
        using var harness = ChatTestHarness.Render();
        harness.Models
            .AskUser(Question, ["Tóm tắt", Answer])
            .Answer(Reply);

        var turn = harness.StartSendAsync(Ask);
        harness.Page.WaitForAssertion(() => Assert.NotEmpty(harness.Page.FindAll(".ask-user-option")));

        harness.Page.FindAll(".ask-user-option")[1].Click();
        await turn;

        harness.Page.WaitForAssertion(() => Assert.Equal([Question, Reply], harness.AssistantTexts()));
        Assert.Equal([Ask, Answer], harness.UserTexts());

        var conversation = harness.ConversationId!.Value;
        Assert.Equal(
            [
                ("user", Ask, null),
                ("assistant", Question, AskUserTranscript.QuestionKind),
                ("user", Answer, AskUserTranscript.AnswerKind),
                ("assistant", Reply, null),
            ],
            harness.Store.Appended.Select(a => (a.Role, a.Text, a.Kind)));
        Assert.Equal(
            ["user", "assistant", "user", "assistant"],
            harness.Store.MessagesOf(conversation).Select(r => r.Role));
        // One row per transcript message: the store ordinal still equals the transcript index.
        Assert.Equal(harness.Messages.Count - 1, harness.Store.MessagesOf(conversation).Count);
    }

    [Fact]
    public async Task A_steered_question_is_stored_before_the_message_that_replaced_it()
    {
        using var harness = ChatTestHarness.Render();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The injected message makes the agent ask the model again, so the turn has a second answer.
        harness.Models.GatedAnswer(gate.Task, "Đang", " trả lời").Answer(" sau khi chèn");

        var turn = harness.StartSendAsync(Ask);
        harness.Page.WaitForAssertion(() => Assert.Contains("Đang", harness.Page.Markup));

        var waiting = harness.Broker.AskAsync(Question, ["Tóm tắt", Answer], true, CancellationToken.None);
        harness.Page.WaitForAssertion(() => Assert.NotEmpty(harness.Page.FindAll(".ask-user-panel")));

        const string Steer = "Bỏ qua, làm bản ngắn";
        await harness.StartSendAsync(Steer);

        Assert.StartsWith(AskUserBroker.SteeredAnswerPrefix, await waiting);
        gate.SetResult();
        await turn;

        var conversation = harness.ConversationId!.Value;
        var rows = harness.Store.MessagesOf(conversation);
        Assert.Equal(
            [
                ("user", Ask, null),
                ("assistant", Question, AskUserTranscript.QuestionKind),
                ("user", Steer, null),
                ("assistant", "Đang trả lời sau khi chèn", null),
            ],
            rows.Select(r => (r.Role, r.Text, r.Kind)).ToArray());
        // The steer text answers the question; it is not stored twice.
        Assert.Single(rows, r => r.Text == Steer);
        Assert.Equal([Ask, Steer], harness.UserTexts());
        Assert.Equal([Question, "Đang trả lời sau khi chèn"], harness.AssistantTexts());
    }

    [Fact]
    public async Task Regenerate_continues_from_the_question_asked_and_drops_the_exchange_behind_it()
    {
        var store = new FakeConversationStore();
        var conversation = await SeedExchangeAsync(store);

        using var harness = ChatTestHarness.Render(store: store);
        harness.Models.Answer("Bản mới.");
        await harness.OpenAsync(conversation);

        await harness.RegenerateAsync();

        harness.Page.WaitForAssertion(() => Assert.Equal(["Bản mới."], harness.AssistantTexts()));
        Assert.Equal([Ask], harness.UserTexts());
        // The answer and the question it answered were part of the reply that regenerating dropped.
        Assert.Equal(
            [("user", Ask, null), ("assistant", "Bản mới.", null)],
            harness.Store.MessagesOf(conversation).Select(r => (r.Role, r.Text, r.Kind)).ToArray());
    }

    [Fact]
    public async Task Editing_the_question_asked_drops_the_exchange_and_keeps_the_store_in_step()
    {
        var store = new FakeConversationStore();
        var conversation = await SeedExchangeAsync(store);

        using var harness = ChatTestHarness.Render(store: store);
        harness.Models.Answer("Bản mới sau khi sửa.");
        await harness.OpenAsync(conversation);

        const string Edited = "Liệt kê các mục chính";
        var asked = harness.Messages.First(
            m => m.Role == ChatRole.User && !AskUserTranscript.IsExchange(m));
        await harness.EditMessageAsync(asked, Edited);

        harness.Page.WaitForAssertion(() => Assert.Equal([Edited], harness.UserTexts()));
        // The question and the answer it was given sat behind the edited message, so the edit dropped
        // them from both the transcript and the store, and it updated the row the transcript edited.
        Assert.Equal(
            [("user", Edited, null), ("assistant", "Bản mới sau khi sửa.", null)],
            harness.Store.MessagesOf(conversation).Select(r => (r.Role, r.Text, r.Kind)).ToArray());
        Assert.Equal(["Bản mới sau khi sửa."], harness.AssistantTexts());
        // One row per transcript message: the store ordinal still equals the transcript index.
        Assert.Equal(harness.Messages.Count - 1, harness.Store.MessagesOf(conversation).Count);
    }

    [Fact]
    public async Task A_store_failure_keeps_the_turn_alive()
    {
        using var harness = ChatTestHarness.Render();
        harness.Models.AskUser(Question, ["Tóm tắt", Answer]).Answer(Reply);

        var turn = harness.StartSendAsync(Ask);
        harness.Page.WaitForAssertion(() => Assert.NotEmpty(harness.Page.FindAll(".ask-user-option")));

        // The exchange write fails; the answer already reached the model, so the turn must keep going.
        harness.Store.FailNextAppends = 1;
        harness.Page.FindAll(".ask-user-option")[1].Click();
        await turn;

        Assert.False(harness.Runner.LastResponseFailed);
        harness.Page.WaitForAssertion(() => Assert.Equal([Reply], harness.AssistantTexts()));
        Assert.Equal([Ask], harness.UserTexts());
        Assert.Equal(
            [("user", Ask, null), ("assistant", Reply, null)],
            harness.Store.MessagesOf(harness.ConversationId!.Value)
                .Select(r => (r.Role, r.Text, r.Kind)).ToArray());
    }

    [Fact]
    public async Task An_exchange_of_another_conversation_is_stored_but_not_rendered()
    {
        using var harness = ChatRunnerHarness.Create();
        var asked = await harness.Store.CreateAsync(ChatTestHarness.UserId);
        var open = await harness.Store.CreateAsync(ChatTestHarness.UserId);
        // The turn moved to another conversation while the question was still waiting.
        harness.Runner.ConversationId = open.Id;
        var before = harness.Runner.Messages.Count;

        await harness.Runner.RecordAskUserExchangeAsync(
            asked.Id,
            new AskUserExchange(new AskUserQuestion(1, Question, [], true), AskUserOutcome.Answered, Answer));

        // The turn keeps its rows in its own conversation and does not write into the open transcript.
        Assert.Equal(
            [
                ("assistant", Question, AskUserTranscript.QuestionKind),
                ("user", Answer, AskUserTranscript.AnswerKind),
            ],
            (await harness.Store.LoadMessagesAsync(asked.Id, ChatTestHarness.UserId))
                .Select(r => (r.Role, r.Text, r.Kind)).ToArray());
        Assert.Equal(before, harness.Runner.Messages.Count);
        Assert.Empty(await harness.Store.LoadMessagesAsync(open.Id, ChatTestHarness.UserId));
    }

    [Fact]
    public async Task Two_questions_in_one_turn_are_stored_in_the_order_they_were_asked()
    {
        using var harness = ChatTestHarness.Render();
        harness.Models
            .AskUser("Câu hỏi một?", ["Một"])
            .AskUser("Câu hỏi hai?", ["Hai"])
            .Answer(Reply);

        var turn = harness.StartSendAsync(Ask);
        harness.Page.WaitForAssertion(() =>
            Assert.Equal("Câu hỏi một?", harness.Page.Find(".ask-user-question").TextContent));
        harness.Page.FindAll(".ask-user-option")[0].Click();

        harness.Page.WaitForAssertion(() =>
            Assert.Equal("Câu hỏi hai?", harness.Page.Find(".ask-user-question").TextContent));
        harness.Page.FindAll(".ask-user-option")[0].Click();
        await turn;

        Assert.Equal(
            [
                ("user", Ask, null),
                ("assistant", "Câu hỏi một?", AskUserTranscript.QuestionKind),
                ("user", "Một", AskUserTranscript.AnswerKind),
                ("assistant", "Câu hỏi hai?", AskUserTranscript.QuestionKind),
                ("user", "Hai", AskUserTranscript.AnswerKind),
                ("assistant", Reply, null),
            ],
            harness.Store.Appended.Select(a => (a.Role, a.Text, a.Kind)));
        Assert.Equal(["Câu hỏi một?", "Câu hỏi hai?", Reply], harness.AssistantTexts());
        Assert.Equal([Ask, "Một", "Hai"], harness.UserTexts());
    }

    [Fact]
    public async Task A_skipped_question_is_stored_as_the_decline_the_model_read()
    {
        using var harness = ChatTestHarness.Render();
        harness.Models.AskUser(Question, ["Tóm tắt"]).Answer(Reply);

        var turn = harness.StartSendAsync(Ask);
        harness.Page.WaitForAssertion(() => Assert.NotEmpty(harness.Page.FindAll(".ask-user-skip")));

        harness.Page.Find(".ask-user-skip").Click();
        await turn;

        harness.Page.WaitForAssertion(() => Assert.Equal([Question, Reply], harness.AssistantTexts()));
        Assert.Equal(
            [
                ("user", Ask, null),
                ("assistant", Question, AskUserTranscript.QuestionKind),
                ("user", AskUserBroker.DeclinedAnswer, AskUserTranscript.DeclinedKind),
                ("assistant", Reply, null),
            ],
            harness.Store.Appended.Select(a => (a.Role, a.Text, a.Kind)));
        // The store keeps what the model read; the transcript says it in the user's language.
        harness.Page.WaitForAssertion(() =>
            Assert.Contains(AskUserTranscript.DeclinedText, harness.Page.Markup));
    }

    [Fact]
    public async Task The_written_exchange_reads_back_from_the_store_with_its_kind()
    {
        using var harness = ChatRunnerHarness.Create();
        var conversation = await harness.Store.CreateAsync(ChatTestHarness.UserId);
        harness.Runner.ConversationId = conversation.Id;

        await harness.Runner.RecordAskUserExchangeAsync(
            conversation.Id,
            new AskUserExchange(
                new AskUserQuestion(1, "Tóm tắt hay liệt kê?", ["Tóm tắt", Answer], true),
                AskUserOutcome.Answered,
                Answer));
        await harness.Runner.RecordAskUserExchangeAsync(
            conversation.Id,
            new AskUserExchange(
                new AskUserQuestion(2, "Ghi chú gì không?", [], true),
                AskUserOutcome.Declined,
                AskUserBroker.DeclinedAnswer));

        var rows = await harness.Store.LoadMessagesAsync(conversation.Id, ChatTestHarness.UserId);

        Assert.Equal(
            [
                ("assistant", "Tóm tắt hay liệt kê?", AskUserTranscript.QuestionKind),
                ("user", Answer, AskUserTranscript.AnswerKind),
                ("assistant", "Ghi chú gì không?", AskUserTranscript.QuestionKind),
                ("user", AskUserBroker.DeclinedAnswer, AskUserTranscript.DeclinedKind),
            ],
            rows.Select(r => (r.Role, r.Text, r.Kind)));
        // The reloaded transcript holds the same exchange, in the same order, as the store.
        Assert.Equal(
            rows.Select(r => AskUserTranscript.Restore(r.Role, r.Text, r.Kind)).Select(m => (m.Role.Value, m.Text)),
            harness.Runner.Messages.Skip(1).Select(m => (m.Role.Value, m.Text)));
    }

    [Fact]
    public async Task A_reloaded_conversation_shows_how_each_question_was_answered()
    {
        var store = new FakeConversationStore();
        var conversation = await store.CreateAsync(ChatTestHarness.UserId);
        var id = conversation.Id;
        const string user = ChatTestHarness.UserId;

        await store.AppendAsync(id, user, "user", Ask);
        await store.AppendAsync(
            id, user, "assistant", "Tóm tắt hay liệt kê?", AskUserTranscript.QuestionKind);
        await store.AppendAsync(id, user, "user", Answer, AskUserTranscript.AnswerKind);
        await store.AppendAsync(id, user, "assistant", "Ghi chú gì không?", AskUserTranscript.QuestionKind);
        await store.AppendAsync(
            id, user, "user", AskUserBroker.DeclinedAnswer, AskUserTranscript.DeclinedKind);
        await store.AppendAsync(id, user, "assistant", Reply);

        using var harness = ChatTestHarness.Render(store: store);
        await harness.OpenAsync(id);

        Assert.Equal([Ask, Answer, AskUserBroker.DeclinedAnswer], harness.UserTexts());
        Assert.Equal(["Tóm tắt hay liệt kê?", "Ghi chú gì không?", Reply], harness.AssistantTexts());
        harness.Page.WaitForAssertion(() => Assert.Contains(AskUserTranscript.DeclinedText, harness.Page.Markup));
        Assert.Contains("Trả lời câu hỏi", harness.Page.Markup);
        Assert.Contains("Bỏ qua", harness.Page.Markup);
        // Both questions were asked by the assistant, and neither offers to be regenerated.
        Assert.Equal(2, harness.Page.FindAll(".assistant-message-header").Count(h => h.TextContent == "Câu hỏi"));
        Assert.Single(harness.Page.FindAll(".message-regenerate-btn"));
    }

    private static int Occurrences(string body, string text)
    {
        var count = 0;
        for (var index = body.IndexOf(text, StringComparison.Ordinal); index >= 0;
             index = body.IndexOf(text, index + text.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }
}
