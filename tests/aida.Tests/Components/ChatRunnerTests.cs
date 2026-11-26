using aida.Components.Pages.Chat;
using aida.Services;
using aida.Services.Agents;
using aida.Services.Documents;
using aida.Services.Ingestion;
using aida.Services.Usage;
using aida.Services.WebSearch;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace aida.Tests.Components;

/// <summary>
/// Drives <see cref="ChatRunner"/> on its own: no renderer, no Razor component, just fakes plus the
/// scripted model transport. These are the tests the extraction exists for.
/// </summary>
public class ChatRunnerTests
{
    [Fact]
    public async Task A_turn_streams_the_answer_into_the_transcript_and_stores_it()
    {
        using var harness = ChatRunnerHarness.Create();
        harness.Models.Answer("Đáp án", " một");

        await harness.Runner.ProcessUserMessageAsync(new ChatMessage(ChatRole.User, "Câu hỏi"), append: true);

        Assert.Equal(["Câu hỏi"], harness.UserTexts());
        Assert.Equal(["Đáp án một"], harness.AssistantTexts());
        Assert.Equal(
            [("user", "Câu hỏi"), ("assistant", "Đáp án một")],
            harness.Store.Appended.Select(a => (a.Role, a.Text)));
        Assert.NotNull(harness.Runner.ConversationId);
        Assert.Equal("Tựa đề 1", harness.Runner.Title);
        Assert.Null(harness.Runner.Status);
        Assert.False(harness.Runner.LastResponseFailed);
        // The streaming bubble became a transcript message, and the live panel saw the work.
        Assert.Null(harness.Runner.CurrentResponseMessage);
        Assert.True(harness.View.StreamRenderCount > 0);
        Assert.Contains("Đang tạo", harness.Runner.ProcessSteps);
        Assert.Equal(1, harness.View.SidebarReloads);
    }

    [Fact]
    public async Task A_rate_limited_turn_answers_nothing_and_spends_no_model_call()
    {
        using var harness = ChatRunnerHarness.Create();
        harness.RateLimiter.Allowed = false;

        await harness.Runner.ProcessUserMessageAsync(new ChatMessage(ChatRole.User, "Câu hỏi"), append: true);

        Assert.Equal("Bạn gửi tin quá nhanh — đợi một lát rồi thử lại.", harness.Runner.Status);
        Assert.Empty(harness.UserTexts());
        Assert.Empty(harness.AssistantTexts());
        Assert.Empty(harness.Store.Appended);
        Assert.Null(harness.Runner.ConversationId);
        Assert.Equal(0, harness.Models.StreamingRequestCount);
    }

    [Fact]
    public async Task A_turn_over_the_daily_budget_is_blocked_before_any_model_call()
    {
        using var harness = ChatRunnerHarness.Create();
        harness.DailyBudget.Status = new DailyBudgetStatus(BudgetState.Exceeded, 2.5m, 2m);

        await harness.Runner.ProcessUserMessageAsync(new ChatMessage(ChatRole.User, "Câu hỏi"), append: true);

        Assert.Equal(
            "Hết ngân sách hôm nay (2.5/2$) — thử lại mai hoặc tăng Budget:DailyUsd.",
            harness.Runner.Status);
        Assert.Empty(harness.UserTexts());
        Assert.Empty(harness.AssistantTexts());
        Assert.Empty(harness.Store.Appended);
        Assert.Null(harness.Runner.ConversationId);
        Assert.Equal(0, harness.Models.StreamingRequestCount);
    }

    [Fact]
    public async Task A_near_limit_budget_warns_but_lets_the_turn_run()
    {
        using var harness = ChatRunnerHarness.Create();
        harness.DailyBudget.Status = new DailyBudgetStatus(BudgetState.NearLimit, 1.6m, 2m);
        harness.Models.Answer("Đáp án");

        await harness.Runner.ProcessUserMessageAsync(new ChatMessage(ChatRole.User, "Câu hỏi"), append: true);

        Assert.Equal(["Đáp án"], harness.AssistantTexts());
        Assert.Equal(
            "Sắp hết ngân sách hôm nay (1.6/2$) — lượt này vẫn chạy.",
            harness.Runner.Status);
        Assert.Equal(1, harness.Models.StreamingRequestCount);
    }

    [Fact]
    public async Task An_off_budget_leaves_the_turn_exactly_as_before()
    {
        using var harness = ChatRunnerHarness.Create();
        harness.Models.Answer("Đáp án");

        await harness.Runner.ProcessUserMessageAsync(new ChatMessage(ChatRole.User, "Câu hỏi"), append: true);

        Assert.Equal(1, harness.DailyBudget.Calls);
        Assert.Equal(["Đáp án"], harness.AssistantTexts());
        Assert.Null(harness.Runner.Status);
        Assert.NotNull(harness.Runner.ConversationId);
    }

    [Fact]
    public async Task Stopping_mid_stream_keeps_the_partial_answer_and_drops_the_rest_of_the_turn()
    {
        using var harness = ChatRunnerHarness.Create();
        var gate = Gate();
        harness.Models.GatedAnswer(gate.Task, "Phần đầu", "PHẦN-SAU-KHI-HỦY");

        var turn = harness.Runner.ProcessUserMessageAsync(new ChatMessage(ChatRole.User, "Câu hỏi"), append: true);
        await harness.View.FirstStreamRender.WaitAsync(TimeSpan.FromSeconds(5));

        // A question raised mid-turn belongs to the turn being stopped.
        var waiting = harness.Broker.AskAsync("Hỏi giữa lượt?", ["A"], true, CancellationToken.None);
        var panelResets = harness.View.PanelResets;

        harness.Runner.CancelCurrentResponse();
        // The partial bubble is kept by the stop itself, not by the stream ending.
        Assert.Equal(["Phần đầu"], harness.AssistantTexts());
        Assert.Null(harness.Broker.Pending);
        Assert.Equal(panelResets + 1, harness.View.PanelResets);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        gate.SetResult();
        await turn;

        Assert.Equal(["Phần đầu"], harness.AssistantTexts());
        Assert.DoesNotContain(harness.Store.Appended, a => a.Role == "assistant");
        Assert.Equal(["Câu hỏi"], harness.UserTexts());
        Assert.Null(harness.Runner.CurrentResponseMessage);
    }

    [Fact]
    public async Task A_message_sent_during_a_turn_joins_the_running_turn_instead_of_starting_another()
    {
        using var harness = ChatRunnerHarness.Create();
        var gate = Gate();
        harness.Models.GatedAnswer(gate.Task, "Đang", " trả lời");

        var turn = harness.Runner.ProcessUserMessageAsync(new ChatMessage(ChatRole.User, "Câu hỏi đầu"), append: true);
        await harness.View.FirstStreamRender.WaitAsync(TimeSpan.FromSeconds(5));

        await harness.Runner.ProcessUserMessageAsync(
            new ChatMessage(ChatRole.User, "Tin nhắn bổ sung"), append: true);

        Assert.Equal(["Câu hỏi đầu", "Tin nhắn bổ sung"], harness.UserTexts());
        Assert.Contains(harness.Store.Appended, a => a.Role == "user" && a.Text == "Tin nhắn bổ sung");
        Assert.Equal("Đã chuyển yêu cầu vào câu trả lời đang chạy…", harness.Runner.Status);
        Assert.Equal(1, harness.Models.StreamingRequestCount);

        gate.SetResult();
        await turn;

        // Still one assistant answer: the steer rode along with the running turn.
        Assert.Single(harness.AssistantTexts());
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>The page's half of a turn, recorded instead of rendered.</summary>
internal sealed class FakeChatTurnView : IChatTurnView
{
    private readonly TaskCompletionSource firstStreamRender =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string? PinnedModel { get; set; }

    public int RenderCount { get; private set; }

    public int StreamRenderCount { get; private set; }

    public int PanelResets { get; private set; }

    public int SidebarReloads { get; private set; }

    /// <summary>Completes when the answer streaming in was rendered for the first time.</summary>
    public Task FirstStreamRender => firstStreamRender.Task;

    Task IChatTurnView.RenderAsync()
    {
        RenderCount++;
        return Task.CompletedTask;
    }

    /// <summary>Nothing renders, so the dispatcher the page would use is the caller's thread.</summary>
    Task IChatTurnView.InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    Task IChatTurnView.RenderStreamAsync()
    {
        StreamRenderCount++;
        firstStreamRender.TrySetResult();
        return Task.CompletedTask;
    }

    Task IChatTurnView.FocusComposerAsync() => Task.CompletedTask;

    Task IChatTurnView.ReloadSidebarAsync()
    {
        SidebarReloads++;
        return Task.CompletedTask;
    }

    void IChatTurnView.ResetAskUserPanel() => PanelResets++;
}

/// <summary>
/// A <see cref="ChatRunner"/> wired with the same in-memory fakes and scripted model transport the
/// rendered-page tests use, but nothing is rendered.
/// </summary>
internal sealed class ChatRunnerHarness : IDisposable
{
    private ChatRunnerHarness()
    {
        var config = ChatTestServices.Configuration();
        var environment = new FakeWebHostEnvironment(ChatTestHarness.ContentRoot);
        var openAi = ChatTestServices.OpenAiClient(new ScriptedHttpHandler(Models));
        var ocr = new OcrOptions();
        var pricing = new PricingTable();

        Store = new FakeConversationStore();
        RateLimiter = new FakeRateLimiter();
        DailyBudget = new FakeDailyBudget();
        Broker = new AskUserBroker();
        Runner = new ChatRunner(
            View,
            NullLogger.Instance,
            Store,
            new SemanticSearch(new NoScopeFactory(), new StubEmbeddingGenerator()),
            // The router degrades to the light model when its HTTP call fails, so a dead client pins routing.
            new SystemOneRouter(
                new HttpClient(new FailingHttpHandler()), config, NullLogger<SystemOneRouter>.Instance),
            config,
            openAi,
            new DocumentService(
                environment,
                new DocumentIngestor(
                    new NoScopeFactory(), new StubEmbeddingGenerator(), ocr,
                    NullLogger<DocumentIngestor>.Instance),
                NullLogger<DocumentService>.Instance),
            environment,
            RateLimiter,
            DailyBudget,
            Usage,
            pricing,
            new ParallelResearchTool(
                openAi, Usage, pricing, environment, ocr, NullLogger<ParallelResearchTool>.Instance),
            new CrewWriterTool(openAi, Usage, pricing),
            new WebSearchTool(new HttpClient(new FailingHttpHandler()), config),
            ocr,
            Broker);

        Runner.Initialize(
            ChatTestHarness.UserId,
            AgentSettings.Load(null, config),
            File.ReadAllText(Path.Combine(ChatTestHarness.ContentRoot, "prompts", "SYSTEM.md")));
    }

    public ChatRunner Runner { get; }

    public FakeChatTurnView View { get; } = new();

    public ModelScripts Models { get; } = new();

    public FakeConversationStore Store { get; }

    public FakeRateLimiter RateLimiter { get; }

    public FakeDailyBudget DailyBudget { get; }

    public FakeUsageTracker Usage { get; } = new();

    public AskUserBroker Broker { get; }

    public IReadOnlyList<string> AssistantTexts() =>
        [.. Runner.Messages.Where(m => m.Role == ChatRole.Assistant).Select(m => m.Text ?? string.Empty)];

    public IReadOnlyList<string> UserTexts() =>
        [.. Runner.Messages.Where(m => m.Role == ChatRole.User).Select(m => m.Text ?? string.Empty)];

    public void Dispose() => Runner.Dispose();

    public static ChatRunnerHarness Create() => new();
}
