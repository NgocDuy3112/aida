using System.Diagnostics;
using System.Text.RegularExpressions;
using aida.Data;
using aida.Services;
using aida.Services.Agents;
using aida.Services.Conversations;
using aida.Services.Documents;
using aida.Services.Ingestion;
using aida.Services.RateLimit;
using aida.Services.Usage;
using aida.Services.WebSearch;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;

namespace aida.Components.Pages.Chat;

/// <summary>
/// The rendering half of a turn. <see cref="ChatRunner"/> owns the turn itself and asks the page for
/// the few UI effects it needs, so the runner never depends on a Razor component.
/// </summary>
internal interface IChatTurnView
{
    /// <summary>The model pinned in the composer, if any; it replaces the router's choice.</summary>
    string? PinnedModel { get; }

    /// <summary>Re-renders the page.</summary>
    Task RenderAsync();

    /// <summary>Re-renders the page for the answer that is still streaming in.</summary>
    Task RenderStreamAsync();

    Task FocusComposerAsync();

    Task ReloadSidebarAsync();

    /// <summary>Closes the ask_user panel: nothing waits on an answer any more.</summary>
    void ResetAskUserPanel();
}

/// <summary>
/// Runs one chat turn: rate limit, model and search, prompt, agent session, streaming loop, storing
/// the answer and generating the title. It owns the state a turn produces (transcript, steps, traces,
/// status, the streaming bubble) and keeps the per-conversation agent runs; the page renders that
/// state and implements <see cref="IChatTurnView"/> for the render/focus/panel effects.
/// </summary>
internal sealed class ChatRunner
{
    private readonly IChatTurnView view;
    private readonly IConversationStore store;
    private readonly SemanticSearch search;
    private readonly SystemOneRouter router;
    private readonly IConfiguration config;
    private readonly OpenAIClient openAi;
    private readonly DocumentService documents;
    private readonly IWebHostEnvironment env;
    private readonly ILogger logger;
    private readonly IChatRateLimiter rateLimiter;
    private readonly IUsageTracker usageTracker;
    private readonly PricingTable pricing;
    private readonly ParallelResearchTool researchTools;
    private readonly CrewWriterTool crewWriter;
    private readonly WebSearchTool webSearch;
    private readonly OcrOptions ocr;
    private readonly AskUserBroker askUserBroker;

    private readonly Dictionary<string, IChatClient> chatClients = [];
    private readonly Dictionary<Guid, AgentRun> agentRuns = [];
    private readonly StreamRenderThrottle streamRenderThrottle =
        new(StreamRenderThrottle.DefaultInterval);
    private readonly Stopwatch streamClock = Stopwatch.StartNew();

    private CancellationTokenSource? currentResponseCancellation;
    private int turnEpoch;

    public ChatRunner(
        IChatTurnView view,
        ILogger logger,
        IConversationStore store,
        SemanticSearch search,
        SystemOneRouter router,
        IConfiguration config,
        OpenAIClient openAi,
        DocumentService documents,
        IWebHostEnvironment env,
        IChatRateLimiter rateLimiter,
        IUsageTracker usageTracker,
        PricingTable pricing,
        ParallelResearchTool researchTools,
        CrewWriterTool crewWriter,
        WebSearchTool webSearch,
        OcrOptions ocr,
        AskUserBroker askUserBroker)
    {
        this.view = view;
        this.logger = logger;
        this.store = store;
        this.search = search;
        this.router = router;
        this.config = config;
        this.openAi = openAi;
        this.documents = documents;
        this.env = env;
        this.rateLimiter = rateLimiter;
        this.usageTracker = usageTracker;
        this.pricing = pricing;
        this.researchTools = researchTools;
        this.crewWriter = crewWriter;
        this.webSearch = webSearch;
        this.ocr = ocr;
        this.askUserBroker = askUserBroker;
    }

    public string UserId { get; private set; } = string.Empty;

    public CrewState Crew { get; private set; } = null!;

    public string SystemPrompt { get; private set; } = string.Empty;

    /// <summary>The open transcript, system prompt included.</summary>
    public List<ChatMessage> Messages { get; } = [];

    /// <summary>Files shown under each message, by message.</summary>
    public Dictionary<ChatMessage, List<string>> MessageAttachments { get; } = [];

    /// <summary>Files attached to the next turn, as shown in the composer.</summary>
    public List<string> AttachedFiles { get; } = [];

    public List<string> ProcessSteps { get; } = [];

    /// <summary>What ran in a finished answer, kept for the transcript once the live panel is gone.</summary>
    public Dictionary<ChatMessage, ProcessTrace> MessageTraces { get; } = [];

    /// <summary>The open conversation; <c>null</c> until the first turn creates it.</summary>
    public Guid? ConversationId { get; set; }

    public string? Title { get; set; }

    public string? Status { get; set; }

    public bool LastResponseFailed { get; set; }

    public string? ActiveModel { get; set; }

    /// <summary>The answer streaming in, rendered as a bubble until the turn adds it to the transcript.</summary>
    public ChatMessage? CurrentResponseMessage { get; private set; }

    public bool CanEditMessages => CurrentResponseMessage is null;

    /// <summary>Loads the per-user turn settings; <see cref="Messages"/> starts with the system prompt.</summary>
    public void Initialize(string userId, CrewState crew, string systemPrompt)
    {
        UserId = userId;
        Crew = crew;
        SystemPrompt = systemPrompt;
        Messages.Add(new ChatMessage(ChatRole.System, systemPrompt));
    }

    /// <summary>Empties the transcript and re-adds the system prompt when the conversation changes.</summary>
    public void ResetTranscript()
    {
        Messages.Clear();
        Messages.Add(new ChatMessage(ChatRole.System, SystemPrompt));
    }

    /// <summary>Drops what the previous conversation left on screen, except the transcript itself.</summary>
    public void ResetTransientState()
    {
        Status = null;
        LastResponseFailed = false;
        ActiveModel = null;
        MessageTraces.Clear();
        ProcessSteps.Clear();
    }

    /// <summary>
    /// Invalidates the running turn so its late writes and catch handlers stay away from the
    /// conversation that replaces it, and keeps the partial answer it already produced.
    /// </summary>
    public void CancelCurrentResponse()
    {
        turnEpoch++;
        // Releases a tool call waiting on the user, so a stopped turn cannot hang the session.
        askUserBroker.Cancel();
        view.ResetAskUserPanel();
        if (CurrentResponseMessage is not null && !string.IsNullOrWhiteSpace(CurrentResponseMessage.Text))
            Messages.Add(CurrentResponseMessage);
        currentResponseCancellation?.Cancel();
        CurrentResponseMessage = null;
    }

    /// <summary>Drops the cached agent session so the next turn rebuilds it from the transcript.</summary>
    public void ForgetRun(Guid conversationId) => agentRuns.Remove(conversationId);

    public void Dispose()
    {
        askUserBroker.Cancel();
        currentResponseCancellation?.Cancel();
        currentResponseCancellation?.Dispose();
    }

    public async Task ProcessUserMessageAsync(ChatMessage userMessage, bool append)
    {
        if (string.IsNullOrWhiteSpace(UserId)) return;

        if (!await rateLimiter.TryAcquireAsync(UserId))
        {
            Status = "Bạn gửi tin quá nhanh — đợi một lát rồi thử lại.";
            return;
        }

        ChatMessage? myResponse = null;
        TextContent? responseText = null;
        var usedModel = "unknown";
        var toolCalls = 0;
        var llmClock = Stopwatch.StartNew();
        // A turn owns ConversationId and Messages until it is replaced. Stopping the turn, switching
        // conversation, starting a new chat or starting the next turn bumps turnEpoch, so the late writes
        // of an old turn (after many awaits) are dropped instead of landing in the new conversation.
        Guid? turnConversation = null;
        int? turnEpochAtStart = null;
        bool IsTurnActive() =>
            myResponse is not null
            && turnEpochAtStart == turnEpoch
            && ConversationId is not null
            && ConversationId == turnConversation;
        try
        {
            await EnsureConversationAsync();

            if (await TrySteerAsync(userMessage)) return;

            CancelCurrentResponse();
            turnConversation = ConversationId;
            turnEpochAtStart = turnEpoch;

            var question = userMessage.Text ?? string.Empty;
            if (append)
            {
                Messages.Add(userMessage);
                if (AttachedFiles.Count > 0)
                    MessageAttachments[userMessage] = new List<string>(AttachedFiles);
                await store.AppendAsync(turnConversation!.Value, UserId, "user", question);
            }

            Status = null;
            LastResponseFailed = false;
            ProcessSteps.Clear();
            view.ResetAskUserPanel();
            AddStep("Đang suy nghĩ…");
            await view.FocusComposerAsync();

            var modelTask = view.PinnedModel is not null
                ? Task.FromResult(view.PinnedModel)
                : router.PickChatModelAsync(question);
            var hitsTask = Task.FromResult<IReadOnlyList<IngestedChunk>>([]);
            var searchScope = BuildSearchScope(question);
            if (searchScope.Count > 0)
                hitsTask = search.SearchAsync(question, UserId, searchScope, maxResults: 6);

            await Task.WhenAll(modelTask, hitsTask);

            var model = await modelTask;
            usedModel = model;
            ActiveModel = model;
            var modelSource = "router";
            if (view.PinnedModel is not null)
                modelSource = "pinned";
            logger.LogInformation(
                "Answering with {Model} ({Source})",
                model,
                modelSource);
            var hits = await hitsTask;
            if (hits.Count > 0)
                AddStep($"Đã tìm {hits.Count} đoạn");
            var prompt = BuildPrompt(question, hits, AttachedFiles);

            responseText = new TextContent("");
            myResponse = new ChatMessage(ChatRole.Assistant, [responseText]);
            CurrentResponseMessage = myResponse;
            currentResponseCancellation?.Dispose();
            currentResponseCancellation = new();
            await view.RenderAsync();

            var (harness, session, sessionFresh) = await GetAgentRunAsync(turnConversation!.Value, model);

            var currentContents = new List<AIContent> { new TextContent(prompt) };
            currentContents.AddRange(Messages[^1].Contents.OfType<DataContent>());
            var currentMessage = new ChatMessage(ChatRole.User, currentContents);
            var agentInput = BuildAgentInput(sessionFresh, currentMessage);

            streamRenderThrottle.Reset();
            streamClock.Restart();
            await foreach (var update in harness.RunStreamingAsync(
                agentInput, session, cancellationToken: currentResponseCancellation.Token))
            {
                await ProcessUpdateAsync(update.Text, update.Contents);
            }
            streamClock.Stop();
            await RenderStreamAsync();

            llmClock.Stop();
            // The stream can finish while the user is stopping the turn or moving to another
            // conversation; CancelCurrentResponse already kept the partial bubble in that case.
            if (!IsTurnActive()) return;

            askUserBroker.Cancel();
            view.ResetAskUserPanel();
            RecordTrace(myResponse!, toolCalls);
            Messages.Add(myResponse!);
            CurrentResponseMessage = null;

            if (!string.IsNullOrWhiteSpace(responseText!.Text))
            {
                await store.AppendAsync(turnConversation!.Value, UserId, "assistant", responseText.Text);
                // Re-check after the write; the title request re-checks again after its own await.
                if (IsTurnActive())
                {
                    AttachGeneratedDocuments(responseText.Text);
                    await MaybeGenerateTitleAsync(turnConversation!.Value, question, responseText.Text);
                }
            }

            if (IsTurnActive())
                await view.ReloadSidebarAsync();
        }
        catch (OperationCanceledException)
        {
            if (IsTurnActive())
            {
                askUserBroker.Cancel();
                view.ResetAskUserPanel();
                AddResponseIfAnswered(myResponse!, toolCalls);
                CurrentResponseMessage = null;
            }

            llmClock.Stop();
            _ = usageTracker.RecordAsync(new UsageEvent
            {
                UserId = UserId,
                Model = usedModel,
                Mode = "agent",
                DurationMs = llmClock.ElapsedMilliseconds,
                ToolCalls = toolCalls,
                Error = "cancelled",
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to answer message.");
            Status = "Đã có lỗi khi trả lời. Vui lòng thử lại.";
            LastResponseFailed = true;
            if (IsTurnActive())
            {
                askUserBroker.Cancel();
                view.ResetAskUserPanel();
                AddResponseIfAnswered(myResponse!, toolCalls);
                CurrentResponseMessage = null;
            }

            llmClock.Stop();
            _ = usageTracker.RecordAsync(new UsageEvent
            {
                UserId = UserId,
                Model = usedModel,
                Mode = "agent",
                DurationMs = llmClock.ElapsedMilliseconds,
                ToolCalls = toolCalls,
                Error = ex.Message,
            });
        }

        async Task ProcessUpdateAsync(string? text, IList<AIContent> contents)
        {
            var changed = false;

            foreach (var item in contents)
            {
                if (item is FunctionCallContent call)
                {
                    toolCalls++;
                    logger.LogDebug("FunctionCall: {Name} args={Args}",
                        call.Name, System.Text.Json.JsonSerializer.Serialize(call.Arguments));
                    // ask_user gets its own banner and takes the focus there; the others are a process step.
                    if (call.Name != AskUserTool.Name)
                        AddStep($"Dùng {call.Name.Replace('_', ' ')}");
                    changed = true;
                }
            }

            if (!string.IsNullOrEmpty(text))
            {
                responseText!.Text += text;
                AddStep("Đang tạo");
                changed = true;
            }

            // Tokens keep accumulating above; the UI only re-renders on the throttle tick.
            if (changed && streamRenderThrottle.TryAcquire(streamClock.ElapsedMilliseconds))
                await RenderStreamAsync();
        }
    }

    private async Task<bool> TrySteerAsync(ChatMessage userMessage)
    {
        if (CurrentResponseMessage is null) return false;
        if (ConversationId is null) return false;
        if (!agentRuns.TryGetValue(ConversationId.Value, out var activeRun)) return false;

        var question = userMessage.Text ?? string.Empty;
        Messages.Add(userMessage);
        if (AttachedFiles.Count > 0)
            MessageAttachments[userMessage] = new List<string>(AttachedFiles);
        await store.AppendAsync(ConversationId.Value, UserId, "user", question);

        await activeRun.Injector.EnqueueMessagesAsync(
            activeRun.Session, [userMessage], CancellationToken.None);
        // Release a waiting ask_user so the injected message reaches the model instead of hanging.
        if (askUserBroker.Pending is not null)
        {
            askUserBroker.TrySteer(question);
            view.ResetAskUserPanel();
        }
        Status = "Đã chuyển yêu cầu vào câu trả lời đang chạy…";
        return true;
    }

    /// <summary>Points the live process panel at <paramref name="step"/>, skipping a repeat of the last one.</summary>
    public void AddStep(string step)
    {
        if (ProcessSteps.Count == 0 || ProcessSteps[^1] != step)
            ProcessSteps.Add(step);
    }

    private Task RenderStreamAsync() => view.RenderStreamAsync();

    private void AddResponseIfAnswered(ChatMessage response, int toolCalls)
    {
        if (string.IsNullOrWhiteSpace(response.Text)) return;
        RecordTrace(response, toolCalls);
        Messages.Add(response);
    }

    private void RecordTrace(ChatMessage response, int toolCalls)
    {
        if (toolCalls == 0 || string.IsNullOrWhiteSpace(response.Text)) return;
        MessageTraces[response] = new ProcessTrace(toolCalls, [.. ProcessSteps]);
    }

    private List<string> BuildSearchScope(string question)
    {
        var scope = new List<string>(AttachedFiles);
        var documentsDir = Path.Combine(env.ContentRootPath, "App_Data", "documents", UserId);
        if (Directory.Exists(documentsDir))
        {
            foreach (var path in Directory.EnumerateFiles(documentsDir))
            {
                var name = Path.GetFileName(path);
                if (scope.Contains(name))
                    continue;
                if (question.Contains(name, StringComparison.OrdinalIgnoreCase))
                    scope.Add(name);
            }
        }
        return scope;
    }

    private static string BuildPrompt(
        string question, IReadOnlyList<IngestedChunk> hits, IReadOnlyList<string> attachedFiles)
    {
        var parts = new List<string>();

        if (attachedFiles.Count > 0)
        {
            parts.Add("<attached-files>\n" + string.Join("\n", attachedFiles) + "\n</attached-files>");
        }

        if (hits.Count > 0)
        {
            var context = string.Join("\n", hits.Select(h =>
                $"<result filename=\"{h.DocumentId}\">{h.Text}</result>"));
            parts.Add("<context>\n" + context + "\n</context>");
        }

        parts.Add($"Question: {question}");
        return string.Join("\n\n", parts);
    }

    private sealed record AgentRun(HarnessAgent Agent, AgentSession Session, string Model, CrewState Crew, MessageInjectingChatClient Injector);

    private IChatClient CreateChatClient(string model)
    {
        if (!chatClients.TryGetValue(model, out var client))
        {
            client = openAi.GetChatClient(model).AsIChatClient();
            chatClients[model] = client;
        }
        return client;
    }

    private async Task<(HarnessAgent Agent, AgentSession Session, bool Fresh)> GetAgentRunAsync(
        Guid conversationId, string model)
    {
        if (agentRuns.TryGetValue(conversationId, out var existing)
            && existing.Model == model && existing.Crew == Crew)
            return (existing.Agent, existing.Session, false);

        var reporting = new UsageReportingChatClient(
            openAi.GetChatClient(model).AsIChatClient(),
            usageTracker, pricing, UserId, model, "agent");

        var tools = BuildAgentTools(model);
        logger.LogInformation("Agent tools: {Tools}",
            string.Join(", ", tools.OfType<AIFunctionDeclaration>().Select(t => t.Name)));

        var harnessInstructions = SystemPrompt + "\n" + AskUserTool.Instructions;
        logger.LogDebug("Instructions tail: {Tail}", harnessInstructions[^220..]);

        var injecting = new MessageInjectingChatClient(reporting);
        var agent = injecting
            .AsHarnessAgent(new HarnessAgentOptions
            {
                Name = "Agent nghiên cứu tài liệu",
                HarnessInstructions = harnessInstructions,
                MaximumIterationsPerRequest = 12,
                DisableWebSearch = true,
                DisableFileMemory = true,
                DisableAgentModeProvider = true,
                ChatOptions = new ChatOptions
                {
                    Tools = tools,
                    Reasoning = CrewOptions.Reasoning(Crew.DirectorThinking),
                },
            });

        var session = await agent.CreateSessionAsync();
        agentRuns[conversationId] = new AgentRun(agent, session, model, Crew, injecting);
        return (agent, session, true);
    }

    private List<ChatMessage> BuildAgentInput(bool sessionFresh, ChatMessage currentMessage)
    {
        if (!sessionFresh)
        {
            return [currentMessage];
        }

        var history = Messages
            .Take(Messages.Count - 1)
            .Where(m => m.Role != ChatRole.System)
            .Where(m => !string.IsNullOrWhiteSpace(m.Text))
            .TakeLast(8)
            .Select(m => new ChatMessage(m.Role, TruncateForHistory(m.Text)))
            .ToList();

        history.Add(currentMessage);
        return history;
    }

    private static string TruncateForHistory(string text)
    {
        if (text.Length <= 1500)
        {
            return text;
        }
        return text[..1500] + " …";
    }

    private List<AITool> BuildAgentTools(string model)
    {
        var webSearchTool = webSearch.Create();

        List<AITool> tools =
        [
            ChatDocumentTools.CreateReadDocument(UserId, env, ocr),
            ChatDocumentTools.CreateSaveDocument(UserId, documents),
            researchTools.Create(UserId, Crew.ResearcherModel, Crew.ResearcherThinking),
            crewWriter.Create(UserId, Crew.WriterModel, Crew.WriterThinking),
            AskUserTool.Create(askUserBroker),
            // The catalog must not advertise web_search when no key is configured and the tool is absent.
            ToolSearchTool.Create(includeWebSearch: webSearchTool is not null),
        ];

        if (webSearchTool is not null) tools.Add(webSearchTool);

        return tools;
    }

    private async Task EnsureConversationAsync()
    {
        if (ConversationId is not null) return;
        var created = await store.CreateAsync(UserId);
        ConversationId = created.Id;
        Title = created.Title;
    }

    private async Task MaybeGenerateTitleAsync(Guid conversation, string question, string answer)
    {
        if (Title is not null && Title != "Chat mới") return;

        var fallback = question.Length > 60 ? question[..60] : question;
        var generated = fallback;

        try
        {
            var lightModel = config["Models:LightModel"]!;
            var client = CreateChatClient(lightModel);
            var response = await client.GetResponseAsync(
                $"Give a 3-5 word title for a conversation that starts with: {question}");
            var candidate = response.Text?.Trim().Trim('"', '.', '\n');
            if (!string.IsNullOrWhiteSpace(candidate) && candidate.Length <= 60)
                generated = candidate;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Title generation failed — using fallback title '{Title}'.", generated);
        }

        // The title request can outlive the turn: never rename (or relabel the header of) whatever
        // conversation is on screen by then.
        if (ConversationId != conversation) return;

        Title = generated;
        await store.RenameAsync(conversation, UserId, generated);
    }

    /// <summary>Adds the documents the answer linked to, so they show as attachments of the chat.</summary>
    public void AttachGeneratedDocuments(string responseText)
    {
        foreach (Match match in Regex.Matches(responseText, "/documents/([^)\\]\\s\"']+)"))
        {
            var name = Uri.UnescapeDataString(match.Groups[1].Value);
            if (!AttachedFiles.Contains(name))
                AttachedFiles.Add(name);
        }
    }
}
