using System.Text;
using OpenAI;
using System.ClientModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace aida.Tests.Spike;

[Trait("Category", "Integration")]
public class MafHarnessSpikeTests
{
    [Fact]
    public async Task Harness_streams_and_calls_a_tool_over_openrouter()
    {
        if (Environment.GetEnvironmentVariable("RUN_MAF_SPIKE") != "1")
        {
            return;
        }

        var (apiKey, baseUrl) = LoadOpenRouterConfig();
        Assert.False(string.IsNullOrWhiteSpace(apiKey),
            "OpenRouter__ApiKey not found in environment or any .env walking up from the test output.");

        var client = new OpenAIClient(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = new Uri(baseUrl) });

        var chatClient = client.GetChatClient("openai/gpt-5-nano").AsIChatClient();

        var toolCalled = false;
        AIFunction spikeTool = AIFunctionFactory.Create(
            (string note) =>
            {
                toolCalled = true;
                return Task.FromResult($"recorded: {note}");
            },
            "spike_ping",
            "Records a ping. Always call this exactly once with note='ping' before answering the user.",
            null);

        var agent = chatClient.AsHarnessAgent(new HarnessAgentOptions
        {
            Name = "spike",
            DisableWebSearch = true,
            DisableFileMemory = true,
            MaximumIterationsPerRequest = 6,
            ChatOptions = new ChatOptions { Tools = [spikeTool] },
        });

        AgentSession session = await agent.CreateSessionAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        var updates = 0;
        var text = new StringBuilder();

        await foreach (var update in agent.RunStreamingAsync(
            "Call the spike_ping tool once with note 'ping', then reply with exactly: PONG",
            session,
            cancellationToken: timeout.Token))
        {
            updates++;
            text.Append(update.Text);
        }

        var output = text.ToString();

        Assert.True(toolCalled, $"Harness never called the tool. Output: {output}");
        Assert.Contains("PONG", output, StringComparison.Ordinal);
        Assert.True(updates > 1, $"Expected streaming (multiple updates), got {updates}.");
    }

    private static (string ApiKey, string BaseUrl) LoadOpenRouterConfig()
    {
        var apiKey = Environment.GetEnvironmentVariable("OpenRouter__ApiKey");
        var baseUrl = Environment.GetEnvironmentVariable("OpenRouter__BaseUrl");

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var envFile = Path.Combine(dir.FullName, ".env");
                if (File.Exists(envFile))
                {
                    foreach (var line in File.ReadAllLines(envFile))
                    {
                        var parts = line.Split('=', 2);
                        if (parts.Length != 2) continue;
                        apiKey ??= parts[0].Trim() == "OpenRouter__ApiKey" ? parts[1].Trim() : null;
                        baseUrl ??= parts[0].Trim() == "OpenRouter__BaseUrl" ? parts[1].Trim() : null;
                    }
                    break;
                }
                dir = dir.Parent;
            }
        }

        return (apiKey ?? string.Empty, string.IsNullOrWhiteSpace(baseUrl)
            ? "https://openrouter.ai/api/v1"
            : baseUrl);
    }
}
