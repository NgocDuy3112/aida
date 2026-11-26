using Bunit;

namespace aida.Tests.Components;

/// <summary>
/// Characterization of the turn epoch guard: stopping mid-stream keeps the partial answer, never adds
/// an empty assistant bubble, and the late writes of the replaced turn are dropped (messages and DB).
/// </summary>
public class ChatTurnCancelTests
{
    [Fact]
    public async Task Stopping_before_the_first_token_leaves_no_assistant_bubble()
    {
        using var harness = ChatTestHarness.Render();
        var gate = Gate();
        harness.Models.GatedStart(gate.Task, "Không bao giờ tới");

        var turn = harness.StartSendAsync("Câu hỏi");
        harness.Page.WaitForAssertion(() => Assert.Equal(1, harness.Models.StreamingRequestCount));

        await harness.StopAsync();
        gate.SetResult();
        await turn;

        Assert.Empty(harness.AssistantTexts());
        Assert.Equal(["Câu hỏi"], harness.UserTexts());
        Assert.DoesNotContain(harness.Store.Appended, a => a.Role == "assistant");
    }

    [Fact]
    public async Task Stopping_mid_stream_keeps_the_partial_text_and_drops_the_rest_of_the_turn()
    {
        using var harness = ChatTestHarness.Render();
        var gate = Gate();
        harness.Models.GatedAnswer(gate.Task, "Phần đầu", "PHẦN-SAU-KHI-HỦY");

        var turn = harness.StartSendAsync("Câu hỏi");
        harness.Page.WaitForAssertion(() => Assert.Contains("Phần đầu", harness.Page.Markup));

        await harness.StopAsync();
        // The partial bubble is kept by the stop itself, not by the stream ending.
        Assert.Equal(["Phần đầu"], harness.AssistantTexts());

        gate.SetResult();
        await turn;

        // The replaced turn adds no second bubble and writes nothing to the conversation store.
        Assert.Equal(["Phần đầu"], harness.AssistantTexts());
        Assert.DoesNotContain("PHẦN-SAU-KHI-HỦY", harness.Page.Markup);
        Assert.DoesNotContain(harness.Store.Appended, a => a.Role == "assistant");
        Assert.Equal(["Câu hỏi"], harness.UserTexts());
    }

    [Fact]
    public async Task A_cancelled_turn_still_waits_for_no_ask_user_question()
    {
        using var harness = ChatTestHarness.Render();
        var gate = Gate();
        harness.Models.GatedAnswer(gate.Task, "Đang", " trả lời");

        var turn = harness.StartSendAsync("Câu hỏi");
        harness.Page.WaitForAssertion(() => Assert.Contains("Đang", harness.Page.Markup));

        var waiting = harness.Broker.AskAsync("Hỏi giữa lượt?", ["A"], true, CancellationToken.None);
        harness.Page.WaitForAssertion(() => Assert.NotEmpty(harness.Page.FindAll(".ask-user-panel")));

        await harness.StopAsync();
        gate.SetResult();
        await turn;

        Assert.Null(harness.Broker.Pending);
        Assert.Empty(harness.Page.FindAll(".ask-user-panel"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    private static TaskCompletionSource Gate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
