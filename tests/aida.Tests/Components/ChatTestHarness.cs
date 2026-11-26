using System.Net;
using System.Text;
using System.Text.Json;
using aida.Components.Pages.Chat;
using aida.Data;
using aida.Services;
using aida.Services.Agents;
using aida.Services.Conversations;
using aida.Services.Documents;
using aida.Services.Ingestion;
using aida.Services.RateLimit;
using aida.Services.Usage;
using aida.Services.WebSearch;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace aida.Tests.Components;

/// <summary>
/// Renders the real <see cref="Chat"/> page with in-memory fakes (plus a scripted OpenAI transport)
/// so the characterization tests drive the component's own code instead of a copy of it.
/// </summary>
internal sealed class ChatTestHarness : IDisposable
{
    public const string UserId = "test-user";

    private ChatTestHarness(
        BunitContext context, IRenderedComponent<Chat> page, FakeConversationStore store, ModelScripts models)
    {
        Context = context;
        Page = page;
        Store = store;
        Models = models;
    }

    public BunitContext Context { get; }
    public IRenderedComponent<Chat> Page { get; }
    public FakeConversationStore Store { get; }
    public ModelScripts Models { get; }

    /// <summary>The runner that owns the turn state the page renders.</summary>
    public ChatRunner Runner => Field<ChatRunner>("chatRunner");

    /// <summary>The open transcript, i.e. what the UI shows for the open conversation.</summary>
    public List<ChatMessage> Messages => Runner.Messages;

    public T Field<T>(string name) =>
        (T)typeof(Chat).GetField(name, System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance)!.GetValue(Page.Instance)!;

    public void SetField<T>(string name, T value) =>
        typeof(Chat).GetField(name, System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance)!.SetValue(Page.Instance, value);

    public AskUserBroker Broker => Field<AskUserBroker>("askUserBroker");

    public Guid? ConversationId => Runner.ConversationId;

    public static string ContentRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null
                   && !File.Exists(Path.Combine(directory.FullName, "prompts", "SYSTEM.md")))
            {
                directory = directory.Parent;
            }
            return directory?.FullName
                ?? throw new InvalidOperationException("Could not locate the repository root (prompts/SYSTEM.md).");
        }
    }

    /// <param name="models">
    /// Scripted model answers. When omitted, any model call fails, which is what the pure UI tests want.
    /// </param>
    /// <param name="ingestor">
    /// Replaces the document ingestor, so a test can let an upload ingest for real without a vector database.
    /// </param>
    /// <param name="store">
    /// The conversation store to render with; a seeded one lets a test open a conversation that was
    /// already stored, i.e. what a reload builds the transcript from.
    /// </param>
    public static ChatTestHarness Render(
        ModelScripts? models = null, DocumentIngestor? ingestor = null, FakeConversationStore? store = null)
    {
        models ??= new ModelScripts();
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;

        store ??= new FakeConversationStore();
        var rateLimiter = new FakeRateLimiter();
        var dailyBudget = new FakeDailyBudget();
        var usage = new FakeUsageTracker();
        var userManager = new FakeUserManager();
        var config = ChatTestServices.Configuration();
        var environment = new FakeWebHostEnvironment(ContentRoot);
        var openAi = ChatTestServices.OpenAiClient(new ScriptedHttpHandler(models));

        context.Services.AddSingleton<IConfiguration>(config);
        context.Services.AddSingleton(store);
        context.Services.AddSingleton<IConversationStore>(store);
        context.Services.AddSingleton<AuthenticationStateProvider>(
            new FakeAuthenticationStateProvider(UserId));
        context.Services.AddSingleton<IWebHostEnvironment>(environment);
        context.Services.AddSingleton<UserManager<AppUser>>(userManager);
        context.Services.AddSingleton<IChatRateLimiter>(rateLimiter);
        context.Services.AddSingleton<IDailyBudget>(dailyBudget);
        context.Services.AddSingleton<IUsageTracker>(usage);
        context.Services.AddSingleton(new PricingTable());
        context.Services.AddSingleton(new OcrOptions());
        context.Services.AddSingleton(openAi);
        context.Services.AddSingleton(new DocumentService(
            environment,
            ingestor ?? new DocumentIngestor(
                new NoScopeFactory(), new StubEmbeddingGenerator(), new OcrOptions(),
                NullLogger<DocumentIngestor>.Instance),
            NullLogger<DocumentService>.Instance));
        context.Services.AddSingleton(new SemanticSearch(new NoScopeFactory(), new StubEmbeddingGenerator()));
        // The router degrades to the light model when its HTTP call fails, so a dead client pins routing.
        context.Services.AddSingleton(new SystemOneRouter(
            new HttpClient(new FailingHttpHandler()), config, NullLogger<SystemOneRouter>.Instance));
        context.Services.AddSingleton(new ParallelResearchTool(
            openAi, usage, new PricingTable(), environment, new OcrOptions(),
            NullLogger<ParallelResearchTool>.Instance));
        context.Services.AddSingleton(new CrewWriterTool(openAi, usage, new PricingTable()));
        context.Services.AddSingleton(new WebSearchTool(new HttpClient(new FailingHttpHandler()), config));
        context.Services.AddSingleton<ILogger<Chat>>(NullLogger<Chat>.Instance);
        context.Services.AddSingleton<ILogger<ChatInput>>(NullLogger<ChatInput>.Instance);

        var page = context.Render<Chat>();
        return new ChatTestHarness(context, page, store, models);
    }

    /// <summary>Starts the composer's send callback, exactly like the user pressing Enter.</summary>
    public Task SendAsync(string text) =>
        Page.FindComponent<ChatInput>().Instance.OnSend.InvokeAsync(new ChatMessage(ChatRole.User, text));

    /// <summary>Starts a send on the renderer thread without waiting for the turn to finish.</summary>
    public Task StartSendAsync(string text) =>
        Context.Renderer.Dispatcher.InvokeAsync(() => SendAsync(text));

    /// <summary>Opens a stored conversation, exactly like picking it in the sidebar.</summary>
    public Task OpenAsync(Guid conversationId) =>
        Context.Renderer.Dispatcher.InvokeAsync(
            () => Page.FindComponent<ChatSidebar>().Instance.OnSelect.InvokeAsync(conversationId));

    /// <summary>Stops the streaming turn, exactly like the stop button.</summary>
    public Task StopAsync() =>
        Context.Renderer.Dispatcher.InvokeAsync(
            () => Page.FindComponent<ChatInput>().Instance.OnStop.InvokeAsync());

    /// <summary>Re-runs the last user question, exactly like the regenerate button.</summary>
    public Task RegenerateAsync() =>
        Context.Renderer.Dispatcher.InvokeAsync(
            () => Page.FindComponent<ChatMessageList>().Instance.OnRegenerate.InvokeAsync());

    public IReadOnlyList<string> AssistantTexts() =>
        [.. Messages.Where(m => m.Role == ChatRole.Assistant).Select(m => m.Text ?? string.Empty)];

    public IReadOnlyList<string> UserTexts() =>
        [.. Messages.Where(m => m.Role == ChatRole.User).Select(m => m.Text ?? string.Empty)];

    public void Dispose() => Context.Dispose();
}

/// <summary>One request the component sent to the model: the title call, or a chat turn.</summary>
internal readonly record struct ModelRequest(int Index, bool IsStreaming);

/// <summary>
/// Scripts the model side of a chat turn: one answer per streaming request, plus the title call the
/// component fires after the first answer. Records every request so tests can prove a turn re-ran.
/// </summary>
internal sealed class ModelScripts
{
    private readonly Queue<Func<ModelRequest, HttpContent>> streamAnswers = new();
    private readonly List<ModelRequest> requests = [];
    private readonly List<(ModelRequest Request, string Body)> sent = [];

    public int StreamingRequestCount => requests.Count(r => r.IsStreaming);

    /// <summary>The bodies of the streaming chat requests, in order, as the model read them.</summary>
    public IReadOnlyList<string> StreamingBodies =>
        [.. sent.Where(s => s.Request.IsStreaming).Select(s => s.Body)];

    /// <summary>Queues the delta texts a streaming answer is built from.</summary>
    public ModelScripts Answer(params string[] deltas)
    {
        streamAnswers.Enqueue(_ => Sse.Answer(deltas));
        return this;
    }

    /// <summary>
    /// Queues an answer that calls ask_user first: the tool blocks until the test answers it, then the
    /// next queued answer finishes the turn.
    /// </summary>
    public ModelScripts AskUser(string question, string[] options)
    {
        streamAnswers.Enqueue(_ => Sse.ToolCall(AskUserTool.Name, new { question, options }));
        return this;
    }

    /// <summary>Queues an answer that stalls on <paramref name="gate"/> between its deltas.</summary>
    public ModelScripts GatedAnswer(Task gate, string before, string after)
    {
        streamAnswers.Enqueue(_ => Sse.Gated(gate, before, after));
        return this;
    }

    /// <summary>Queues an answer that stalls on <paramref name="gate"/> before its only delta.</summary>
    public ModelScripts GatedStart(Task gate, string text)
    {
        streamAnswers.Enqueue(_ => Sse.GatedStart(gate, text));
        return this;
    }

    internal HttpContent Next(ModelRequest request)
    {
        if (!request.IsStreaming)
            return Sse.Title($"Tựa đề {request.Index}");

        if (streamAnswers.Count == 0)
            throw new InvalidOperationException($"Unexpected streaming model request #{request.Index}.");

        return streamAnswers.Dequeue()(request);
    }

    internal void Record(ModelRequest request, string body)
    {
        requests.Add(request);
        sent.Add((request, body));
    }
}

internal static class Sse
{
    private static string Chunk(string? delta = null, string? finishReason = null)
    {
        var payload = JsonSerializer.Serialize(new
        {
            id = "chatcmpl-test",
            @object = "chat.completion.chunk",
            created = 0,
            model = "test/light",
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = delta is null ? new { } : (object)new { content = delta },
                    finish_reason = finishReason,
                },
            },
        });
        return $"data: {payload}\n\n";
    }

    /// <summary>One complete streaming assistant answer, as the OpenAI SDK parses it.</summary>
    public static HttpContent Answer(params string[] deltas) =>
        Raw((string.Concat(deltas.Select(d => Chunk(d))) + Chunk(finishReason: "stop") + "data: [DONE]\n\n", null));

    /// <summary>One streaming answer that calls <paramref name="name"/> and stops for the tool result.</summary>
    public static HttpContent ToolCall(string name, object arguments)
    {
        var payload = JsonSerializer.Serialize(new
        {
            id = "chatcmpl-test",
            @object = "chat.completion.chunk",
            created = 0,
            model = "test/light",
            choices = new object[]
            {
                new
                {
                    index = 0,
                    delta = new
                    {
                        tool_calls = new object[]
                        {
                            new
                            {
                                index = 0,
                                id = "call_1",
                                type = "function",
                                function = new { name, arguments = JsonSerializer.Serialize(arguments) },
                            },
                        },
                    },
                    finish_reason = (string?)null,
                },
            },
        });
        return Raw(($"data: {payload}\n\n" + Chunk(finishReason: "tool_calls") + "data: [DONE]\n\n", null));
    }

    public static HttpContent Gated(Task gate, string before, string after) =>
        Raw((Chunk(before), null),
            (Chunk(after) + Chunk(finishReason: "stop") + "data: [DONE]\n\n", gate));

    public static HttpContent GatedStart(Task gate, string text) =>
        Raw((Chunk(text) + Chunk(finishReason: "stop") + "data: [DONE]\n\n", gate));

    public static HttpContent Title(string title) =>
        new StringContent(
            JsonSerializer.Serialize(new
            {
                id = "chatcmpl-title",
                @object = "chat.completion",
                created = 0,
                model = "test/light",
                choices = new[]
                {
                    new { index = 0, message = new { role = "assistant", content = title }, finish_reason = "stop" },
                },
            }),
            Encoding.UTF8,
            "application/json");

    private static HttpContent Raw(params (string Text, Task? Gate)[] steps) => new ScriptedSseContent(steps);
}

/// <summary>HTTP handler that answers every request from <see cref="ModelScripts"/>.</summary>
internal sealed class ScriptedHttpHandler(ModelScripts models) : HttpMessageHandler
{
    private int index;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? "{}"
            : await request.Content.ReadAsStringAsync(cancellationToken);
        var streaming = JsonDocument.Parse(body).RootElement.TryGetProperty("stream", out var flag)
                        && flag.ValueKind == JsonValueKind.True;
        var modelRequest = new ModelRequest(Interlocked.Increment(ref index) - 1, streaming);
        models.Record(modelRequest, body);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = models.Next(modelRequest),
        };
    }
}

/// <summary>
/// A text/event-stream body delivered in scripted steps; a step can wait on a gate so a test can act
/// while the turn is mid-stream.
/// </summary>
internal sealed class ScriptedSseContent : HttpContent
{
    private readonly IReadOnlyList<(string Text, Task? Gate)> steps;

    public ScriptedSseContent(params (string Text, Task? Gate)[] steps)
    {
        this.steps = steps;
        Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
    }

    protected override Task<Stream> CreateContentReadStreamAsync() =>
        Task.FromResult<Stream>(new ScriptedSseStream(steps));

    protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
    {
        await using var source = new ScriptedSseStream(steps);
        await source.CopyToAsync(stream);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}

internal sealed class ScriptedSseStream(IReadOnlyList<(string Text, Task? Gate)> steps) : Stream
{
    private readonly MemoryStream current = new();
    private int index = -1;
    private bool completed;

    private async ValueTask<bool> AdvanceAsync(CancellationToken cancellationToken)
    {
        while (current.Position >= current.Length)
        {
            if (completed) return false;
            index++;
            if (index >= steps.Count)
            {
                completed = true;
                return false;
            }

            var (text, gate) = steps[index];
            // The real transport aborts the read when the turn is stopped, so the fake stream must too.
            if (gate is not null) await gate.WaitAsync(cancellationToken);
            if (text.Length == 0) continue;

            current.SetLength(0);
            var bytes = Encoding.UTF8.GetBytes(text);
            current.Write(bytes, 0, bytes.Length);
            current.Position = 0;
        }

        return true;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!await AdvanceAsync(cancellationToken)) return 0;
        return current.Read(buffer.Span);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }

    protected override void Dispose(bool disposing)
    {
        if (disposing) current.Dispose();
        base.Dispose(disposing);
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
