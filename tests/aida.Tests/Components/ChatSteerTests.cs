using Bunit;

namespace aida.Tests.Components;

/// <summary>
/// Characterization of the steer path: a message typed while a turn streams is appended to the open
/// conversation and injected into the running agent session instead of starting a second turn, and it
/// releases a waiting ask_user question.
/// (T4 của sprint này sẽ đổi kết quả trả về của TrySteerAsync — test này mô tả hành vi hiện tại.)
/// </summary>
public class ChatSteerTests
{
    [Fact]
    public async Task A_message_typed_during_a_turn_is_injected_instead_of_starting_a_new_one()
    {
        using var harness = ChatTestHarness.Render();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Models.GatedAnswer(gate.Task, "Đang", " trả lời");

        var turn = harness.StartSendAsync("Câu hỏi đầu");
        harness.Page.WaitForAssertion(() => Assert.Contains("Đang", harness.Page.Markup));

        await harness.StartSendAsync("Tin nhắn bổ sung");

        // The steer is shown and stored immediately, and no second model request was started.
        harness.Page.WaitForAssertion(() =>
            Assert.Equal(["Câu hỏi đầu", "Tin nhắn bổ sung"], harness.UserTexts()));
        Assert.Contains(harness.Store.Appended, a => a.Role == "user" && a.Text == "Tin nhắn bổ sung");
        Assert.Contains("Đã chuyển yêu cầu vào câu trả lời đang chạy", harness.Page.Markup);
        Assert.Equal(1, harness.Models.StreamingRequestCount);

        gate.SetResult();
        await turn;

        // Still one assistant answer: the steer rode along with the running turn.
        Assert.Single(harness.AssistantTexts());
    }

    [Fact]
    public async Task A_message_typed_during_a_turn_declines_the_waiting_question()
    {
        using var harness = ChatTestHarness.Render();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Models.GatedAnswer(gate.Task, "Đang", " trả lời");

        var turn = harness.StartSendAsync("Câu hỏi đầu");
        harness.Page.WaitForAssertion(() => Assert.Contains("Đang", harness.Page.Markup));

        var waiting = harness.Broker.AskAsync("Hỏi giữa lượt?", ["A"], true, CancellationToken.None);
        harness.Page.WaitForAssertion(() => Assert.NotEmpty(harness.Page.FindAll(".ask-user-panel")));

        await harness.StartSendAsync("Trả lời luôn trong chat");

        harness.Page.WaitForAssertion(() => Assert.Empty(harness.Page.FindAll(".ask-user-panel")));
        Assert.Null(harness.Broker.Pending);
        Assert.Equal("user declined to answer", await waiting);

        gate.SetResult();
        await turn;
    }

    [Fact]
    public async Task A_message_sent_while_idle_starts_a_new_turn()
    {
        using var harness = ChatTestHarness.Render();
        harness.Models.Answer("Trả lời", " một").Answer("Trả lời", " hai");

        await harness.StartSendAsync("Câu hỏi 1");
        harness.Page.WaitForAssertion(() => Assert.Equal(["Trả lời một"], harness.AssistantTexts()));

        await harness.StartSendAsync("Câu hỏi 2");

        harness.Page.WaitForAssertion(() =>
            Assert.Equal(["Trả lời một", "Trả lời hai"], harness.AssistantTexts()));
        Assert.Equal(["Câu hỏi 1", "Câu hỏi 2"], harness.UserTexts());
        Assert.Equal(2, harness.Models.StreamingRequestCount);
    }
}
