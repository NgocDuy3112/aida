using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Security.Claims;
using System.Text;
using aida.Data;
using aida.Services;
using aida.Services.Conversations;
using aida.Services.Ingestion;
using aida.Services.RateLimit;
using aida.Services.Usage;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using OpenAI;
using Pgvector;

namespace aida.Tests.Components;

/// <summary>
/// In-memory <see cref="IConversationStore"/>. Like the real store, message ordinals start at 1 and
/// delete-from-ordinal removes that row and everything after it.
/// </summary>
internal sealed class FakeConversationStore : IConversationStore
{
    private readonly Dictionary<Guid, List<(int Ordinal, ChatMessageRecord Record)>> messages = [];
    private readonly Dictionary<Guid, ConversationSummary> conversations = [];

    public List<(Guid ConversationId, string Role, string Text)> Appended { get; } = [];
    public List<(Guid ConversationId, int Ordinal)> DeletedFrom { get; } = [];

    public IReadOnlyList<ChatMessageRecord> MessagesOf(Guid id) =>
        messages.TryGetValue(id, out var rows)
            ? [.. rows.OrderBy(r => r.Ordinal).Select(r => r.Record)]
            : [];

    public Task<IReadOnlyList<ConversationSummary>> ListAsync(string userId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ConversationSummary>>([.. conversations.Values]);

    public Task<ConversationSummary> CreateAsync(string userId, CancellationToken ct = default)
    {
        var created = new ConversationSummary(Guid.NewGuid(), "Chat mới", DateTimeOffset.UnixEpoch);
        conversations[created.Id] = created;
        messages[created.Id] = [];
        return Task.FromResult(created);
    }

    public Task<IReadOnlyList<ChatMessageRecord>> LoadMessagesAsync(
        Guid conversationId, string userId, CancellationToken ct = default) =>
        Task.FromResult(MessagesOf(conversationId));

    public Task AppendAsync(
        Guid conversationId, string userId, string role, string text, CancellationToken ct = default)
    {
        Appended.Add((conversationId, role, text));
        var rows = messages[conversationId];
        var next = rows.Count == 0 ? 1 : rows.Max(r => r.Ordinal) + 1;
        rows.Add((next, new ChatMessageRecord(role, text)));
        return Task.CompletedTask;
    }

    public Task UpdateMessageAsync(
        Guid conversationId, string userId, int ordinal, string text, CancellationToken ct = default)
    {
        var rows = messages[conversationId];
        var index = rows.FindIndex(r => r.Ordinal == ordinal);
        if (index >= 0)
            rows[index] = (ordinal, new ChatMessageRecord(rows[index].Record.Role, text));
        return Task.CompletedTask;
    }

    public Task DeleteMessagesFromAsync(
        Guid conversationId, string userId, int ordinal, CancellationToken ct = default)
    {
        DeletedFrom.Add((conversationId, ordinal));
        messages[conversationId].RemoveAll(r => r.Ordinal >= ordinal);
        return Task.CompletedTask;
    }

    public Task RenameAsync(
        Guid conversationId, string userId, string title, CancellationToken ct = default)
    {
        if (conversations.TryGetValue(conversationId, out var existing))
            conversations[conversationId] = existing with { Title = title };
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid conversationId, string userId, CancellationToken ct = default)
    {
        conversations.Remove(conversationId);
        messages.Remove(conversationId);
        return Task.CompletedTask;
    }
}

internal sealed class FakeAuthenticationStateProvider : AuthenticationStateProvider
{
    private readonly ClaimsPrincipal principal;

    public FakeAuthenticationStateProvider(string userId)
    {
        principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim("name", "Người dùng thử"),
        ], "test"));
    }

    public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
        Task.FromResult(new AuthenticationState(principal));
}

internal sealed class FakeUserStore : IUserStore<AppUser>
{
    public Task<IdentityResult> CreateAsync(AppUser user, CancellationToken cancellationToken) =>
        Task.FromResult(IdentityResult.Success);

    public Task<IdentityResult> DeleteAsync(AppUser user, CancellationToken cancellationToken) =>
        Task.FromResult(IdentityResult.Success);

    public void Dispose() { }

    public Task<AppUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) =>
        Task.FromResult<AppUser?>(null);

    public Task<AppUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) =>
        Task.FromResult<AppUser?>(null);

    public Task<string?> GetNormalizedUserNameAsync(AppUser user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public Task<string> GetUserIdAsync(AppUser user, CancellationToken cancellationToken) =>
        Task.FromResult(user.Id);

    public Task<string?> GetUserNameAsync(AppUser user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(user.UserName);

    public Task SetNormalizedUserNameAsync(
        AppUser user, string? normalizedName, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SetUserNameAsync(AppUser user, string? userName, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<IdentityResult> UpdateAsync(AppUser user, CancellationToken cancellationToken) =>
        Task.FromResult(IdentityResult.Success);
}

/// <summary>UserManager the Chat component only calls FindByIdAsync on; the chat does not need a real store.</summary>
internal sealed class FakeUserManager : UserManager<AppUser>
{
    public FakeUserManager()
        : base(
            new FakeUserStore(),
            Microsoft.Extensions.Options.Options.Create(new IdentityOptions()),
            new PasswordHasher<AppUser>(),
            [],
            [],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            null!,
            null!)
    {
    }

    public AppUser? User { get; set; }

    public override Task<AppUser?> FindByIdAsync(string userId) =>
        Task.FromResult(User);
}

internal sealed class FakeRateLimiter : IChatRateLimiter
{
    public bool Allowed { get; set; } = true;
    public int Calls { get; private set; }

    public Task<bool> TryAcquireAsync(string userId, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(Allowed);
    }
}

internal sealed class FakeUsageTracker : IUsageTracker
{
    public List<UsageEvent> Events { get; } = [];

    public Task RecordAsync(UsageEvent evt, CancellationToken ct = default)
    {
        Events.Add(evt);
        return Task.CompletedTask;
    }
}

internal sealed class FakeWebHostEnvironment(string contentRoot) : IWebHostEnvironment
{
    public string ContentRootPath { get; set; } = contentRoot;
    public string WebRootPath { get; set; } = contentRoot;
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string ApplicationName { get; set; } = "aida";
    public string EnvironmentName { get; set; } = "Development";
}

/// <summary>Service wiring shared by the rendered-page tests and the direct <c>ChatRunner</c> tests.</summary>
internal static class ChatTestServices
{
    public static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Models:StrongModel"] = "test/strong",
                ["Models:ModerateModel"] = "test/moderate",
                ["Models:LightModel"] = "test/light",
                ["Models:EmbeddingModel"] = "test/embed",
                ["Crew:DirectorModel"] = "test/director",
                ["Crew:ResearcherModel"] = "test/researcher",
                ["Crew:WriterModel"] = "test/writer",
                ["OpenRouter:ApiKey"] = "test-key",
                ["OpenRouter:BaseUrl"] = "https://openrouter.test/api/v1",
                ["OpenRouter:SystemOneEndpoint"] = "https://openrouter.test/api/v1/systemone",
                ["OpenRouter:SystemOneModel"] = "test/systemone",
                ["WebSearch:ApiKey"] = "",
            })
            .Build();

    public static OpenAIClient OpenAiClient(HttpMessageHandler handler) =>
        new(
            new ApiKeyCredential("test-key"),
            new OpenAIClientOptions
            {
                Endpoint = new Uri("https://openrouter.test/api/v1"),
                Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            });
}

internal sealed class NoScopeFactory : IServiceScopeFactory
{
    public IServiceScope CreateScope() =>
        throw new NotSupportedException("The chat tests never resolve scoped services.");
}

internal sealed class FailingHttpHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
}

internal sealed class StubEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Embeddings are not used by the render tests.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
