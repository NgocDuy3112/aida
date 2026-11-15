using aida.Components;
using aida.Data;
using aida.Extensions;
using aida.Services;
using aida.Services.Conversations;
using aida.Services.Documents;
using aida.Services.Ingestion;
using aida.Services.Agents;
using aida.Services.RateLimit;
using aida.Services.Usage;
using aida.Services.WebSearch;
using DotNetEnv;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using OpenAI;
using StackExchange.Redis;
using System.ClientModel;
using Microsoft.AspNetCore.DataProtection;
using OpenTelemetry.Trace;

Env.Load();

QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
QuestPDF.Settings.ThrowOnMissingFontFamilies = false;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

builder.Services.AddAppIdentity(builder.Configuration, builder.Environment);
builder.Services.AddScoped<IConversationStore, ConversationStore>();

var redisEnabled = builder.Configuration.GetValue<bool>("Redis:Enabled");
if (redisEnabled)
{
    var redisConnection = builder.Configuration["Redis:ConnectionString"]
        ?? throw new InvalidOperationException("Missing configuration: Redis__ConnectionString");
    var redis = ConnectionMultiplexer.Connect(redisConnection);
    builder.Services.AddSingleton<IConnectionMultiplexer>(redis);

    builder.Services.AddDataProtection()
        .SetApplicationName("aida")
        .PersistKeysToStackExchangeRedis(redis, "aida:data-protection");

    builder.Services.AddStackExchangeRedisCache(o =>
    {
        o.Configuration = redisConnection;
        o.InstanceName = "aida:";
    });
}
else
{
    builder.Services.AddDataProtection().SetApplicationName("aida");
    builder.Services.AddDistributedMemoryCache();
}

builder.Services.AddSingleton<IChatRateLimiter>(sp => new ChatRateLimiter(
    redisEnabled ? sp.GetRequiredService<IConnectionMultiplexer>() : null,
    sp.GetRequiredService<IConfiguration>(),
    sp.GetRequiredService<ILogger<ChatRateLimiter>>()));

builder.Services.AddSingleton<PricingTable>();
builder.Services.AddHttpClient(OpenRouterPricingService.HttpClientName);
builder.Services.AddHostedService<OpenRouterPricingService>();
builder.Services.AddScoped<IUsageTracker, UsageTracker>();
builder.Services.AddScoped<IUsageReport, UsageReport>();

var or = builder.Configuration.GetSection("OpenRouter");
var options = new OpenAIClientOptions { 
    Endpoint = new Uri(or["BaseUrl"] ?? "https://openrouter.ai/api/v1") 
};
var openAIClient = new OpenAIClient(new ApiKeyCredential(or["ApiKey"]!), options);
builder.Services.AddSingleton(openAIClient);

#pragma warning disable OPENAI001
var chatClient = openAIClient.GetResponsesClient().AsIChatClient(or["LightModel"]!);
#pragma warning restore OPENAI001
var embeddings = openAIClient.GetEmbeddingClient(or["EmbeddingModel"]!).AsIEmbeddingGenerator();

builder.Services.AddHttpClient<SystemOneRouter>();
builder.Services.AddHttpClient<WebSearchTool>();
builder.Services.AddScoped<ParallelResearchTool>();
builder.Services.AddScoped<CrewWriterTool>();
builder.Services.AddSingleton<SemanticSearch>();
builder.Services.AddScoped<DocumentIngestor>();
builder.Services.AddScoped<DocumentService>();
builder.Services.AddHttpClient(GoogleWorkspaceService.HttpClientName);
builder.Services.AddScoped<GoogleWorkspaceService>();
builder.Services.AddChatClient(chatClient).UseLogging();
builder.Services.AddEmbeddingGenerator(embeddings);

var otelEndpoint = builder.Configuration["OpenTelemetry:Endpoint"];
if (!string.IsNullOrWhiteSpace(otelEndpoint))
{
    builder.Services.AddOpenTelemetry()
        .WithTracing(t => t
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddEntityFrameworkCoreInstrumentation()
            .AddSource("Aida.Llm")
            .AddOtlpExporter(e => e.Endpoint = new Uri(otelEndpoint)));
}

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.UseStaticFiles();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapAuthEndpoints(app.Environment);
app.MapDocumentEndpoints();

if (app.Configuration.GetValue<bool>("Database:AutoMigrate"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
}

app.Run();