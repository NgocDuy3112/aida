using aida.Services.Agents;

namespace aida.Tests.Services;

public class AskUserBrokerTests
{
    private static readonly string[] Options = ["Tóm tắt", "Liệt kê"];

    [Fact]
    public async Task Wait_returns_the_answer_the_ui_submits()
    {
        var broker = new AskUserBroker();
        var waiting = broker.AskAsync("Bạn muốn gì?", Options, true, CancellationToken.None);

        Assert.True(broker.TryAnswer("Liệt kê"));

        Assert.Equal("Liệt kê", await waiting);
        Assert.Null(broker.Pending);
    }

    [Fact]
    public async Task Answer_is_trimmed_and_the_panel_closes()
    {
        var broker = new AskUserBroker();
        var seen = new List<AskUserQuestion?>();
        broker.Changed += question => seen.Add(question);

        var waiting = broker.AskAsync("Bạn muốn gì?", Options, true, CancellationToken.None);
        broker.TryAnswer("  Liệt kê  ");
        await waiting;

        Assert.Equal("Liệt kê", await waiting);
        Assert.Equal(2, seen.Count);
        Assert.Equal("Bạn muốn gì?", seen[0]?.Question);
        Assert.Equal(Options, seen[0]?.Options);
        Assert.Null(seen[1]);
    }

    [Fact]
    public async Task Skip_returns_the_declined_answer()
    {
        var broker = new AskUserBroker();
        var waiting = broker.AskAsync("Bạn muốn gì?", Options, true, CancellationToken.None);

        Assert.True(broker.TryDecline());

        Assert.Equal(AskUserBroker.DeclinedAnswer, await waiting);
    }

    [Fact]
    public async Task Cancel_releases_a_waiting_question()
    {
        var broker = new AskUserBroker();
        var waiting = broker.AskAsync("Bạn muốn gì?", Options, true, CancellationToken.None);

        broker.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Null(broker.Pending);
    }

    [Fact]
    public async Task Cancelling_the_passed_token_releases_a_waiting_question()
    {
        var broker = new AskUserBroker();
        using var cancellation = new CancellationTokenSource();
        var waiting = broker.AskAsync("Bạn muốn gì?", Options, true, cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task Answering_before_the_tool_awaits_still_wins_the_race()
    {
        var broker = new AskUserBroker();
        broker.Changed += question =>
        {
            if (question is not null) broker.TryAnswer("Tóm tắt");
        };

        var answer = await broker.AskAsync("Bạn muốn gì?", Options, true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Tóm tắt", answer);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Answering_with_blank_text_is_still_an_answer(string answer)
    {
        var broker = new AskUserBroker();
        var waiting = broker.AskAsync("Bạn muốn gì?", Options, true, CancellationToken.None);

        Assert.True(broker.TryAnswer(answer));

        Assert.Equal(string.Empty, await waiting);
    }

    [Fact]
    public async Task Answering_without_a_waiting_question_is_rejected()
    {
        var broker = new AskUserBroker();

        Assert.False(broker.TryAnswer("Tóm tắt"));
        Assert.False(broker.TryDecline());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_throwing_panel_handler_does_not_leave_the_question_stuck()
    {
        var broker = new AskUserBroker();
        broker.Changed += _ => throw new InvalidOperationException("panel is gone");

        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => broker.AskAsync("Bạn muốn gì?", Options, true, CancellationToken.None));

        Assert.Null(broker.Pending);
        Assert.False(broker.TryAnswer("Tóm tắt"));
    }

    [Fact]
    public async Task A_second_question_declines_the_unanswered_first()
    {
        var broker = new AskUserBroker();
        var first = broker.AskAsync("Câu một?", Options, true, CancellationToken.None);
        var second = broker.AskAsync("Câu hai?", Options, false, CancellationToken.None);

        Assert.Equal(AskUserBroker.DeclinedAnswer, await first);
        Assert.Equal("Câu hai?", broker.Pending?.Question);
        Assert.False(broker.Pending?.AllowFreeText);

        broker.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
    }
}
