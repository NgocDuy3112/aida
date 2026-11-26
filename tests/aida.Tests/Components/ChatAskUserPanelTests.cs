using Bunit;

namespace aida.Tests.Components;

/// <summary>
/// Characterization of the ask_user panel: a question raised by the broker shows the panel, an answer
/// reaches the broker, and the panel always resets so a stale button cannot keep a dead question.
/// </summary>
public class ChatAskUserPanelTests
{
    private static readonly string[] Options = ["Tóm tắt", "Liệt kê"];

    [Fact]
    public async Task Question_shows_the_panel_with_options_and_the_free_text_input()
    {
        using var harness = ChatTestHarness.Render();

        var waiting = harness.Broker.AskAsync(
            "Bạn muốn tóm tắt hay liệt kê?", Options, true, CancellationToken.None);

        harness.Page.WaitForAssertion(() =>
            Assert.Equal("Bạn muốn tóm tắt hay liệt kê?", harness.Page.Find(".ask-user-question").TextContent));
        Assert.Equal(
            Options,
            harness.Page.FindAll(".ask-user-option").Select(o => o.TextContent.Trim()));
        Assert.NotEmpty(harness.Page.FindAll(".ask-user-input"));
        Assert.NotEmpty(harness.Page.FindAll(".ask-user-skip"));

        harness.Page.FindAll(".ask-user-option")[1].Click();

        Assert.Equal("Liệt kê", await waiting);
    }

    [Fact]
    public async Task Answering_an_option_forwards_it_to_the_waiting_tool_and_closes_the_panel()
    {
        using var harness = ChatTestHarness.Render();
        var waiting = harness.Broker.AskAsync("Chọn một", Options, true, CancellationToken.None);
        harness.Page.WaitForAssertion(() => Assert.NotEmpty(harness.Page.FindAll(".ask-user-panel")));

        harness.Page.FindAll(".ask-user-option")[0].Click();

        Assert.Equal("Tóm tắt", await waiting);
        Assert.Empty(harness.Page.FindAll(".ask-user-panel"));
    }

    [Fact]
    public async Task Submitting_typed_text_answers_the_question()
    {
        using var harness = ChatTestHarness.Render();
        var waiting = harness.Broker.AskAsync("Ghi chú gì không?", [], true, CancellationToken.None);
        harness.Page.WaitForAssertion(() => Assert.NotEmpty(harness.Page.FindAll(".ask-user-input")));

        harness.Page.Find(".ask-user-input").Change("  Ba dòng  ");
        harness.Page.Find(".ask-user-form").Submit();

        Assert.Equal("Ba dòng", await waiting);
        Assert.Empty(harness.Page.FindAll(".ask-user-panel"));
    }

    [Fact]
    public async Task Skip_declines_the_waiting_answer_and_closes_the_panel()
    {
        using var harness = ChatTestHarness.Render();
        var waiting = harness.Broker.AskAsync("Chọn một", Options, true, CancellationToken.None);
        harness.Page.WaitForAssertion(() => Assert.NotEmpty(harness.Page.FindAll(".ask-user-skip")));

        harness.Page.Find(".ask-user-skip").Click();

        Assert.Equal("user declined to answer", await waiting);
        Assert.Empty(harness.Page.FindAll(".ask-user-panel"));
    }

    [Fact]
    public async Task No_free_text_input_is_offered_when_the_question_forbids_it()
    {
        using var harness = ChatTestHarness.Render();
        var waiting = harness.Broker.AskAsync("Chọn một", Options, false, CancellationToken.None);

        harness.Page.WaitForAssertion(() => Assert.NotEmpty(harness.Page.FindAll(".ask-user-option")));
        Assert.Empty(harness.Page.FindAll(".ask-user-input"));
        Assert.NotEmpty(harness.Page.FindAll(".ask-user-skip"));

        harness.Page.FindAll(".ask-user-option")[1].Click();
        Assert.Equal("Liệt kê", await waiting);
    }

    [Fact]
    public async Task Panel_resets_even_when_no_question_waits_anymore()
    {
        using var harness = ChatTestHarness.Render();
        // A finished turn can leave its panel behind; the button must still close it instead of hanging.
        harness.SetField("pendingQuestion", "Câu hỏi của lượt đã xong");
        harness.SetField("pendingOptions", new List<string> { "A", "B" });
        harness.SetField("pendingAllowFreeText", true);
        await harness.Context.Renderer.Dispatcher.InvokeAsync(() => harness.Page.Render());

        Assert.NotEmpty(harness.Page.FindAll(".ask-user-option"));

        harness.Page.FindAll(".ask-user-option")[0].Click();

        Assert.Empty(harness.Page.FindAll(".ask-user-panel"));
        Assert.Null(harness.Broker.Pending);
        Assert.Null(harness.Field<string?>("pendingQuestion"));
        Assert.Empty(harness.Field<List<string>>("pendingOptions"));
    }
}
