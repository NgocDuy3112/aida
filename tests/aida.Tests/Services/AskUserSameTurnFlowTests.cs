using System.Runtime.CompilerServices;
using System.Text;
using aida.Services.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace aida.Tests.Services;

/// <summary>
/// Drives a real HarnessAgent with a scripted chat client to prove the ask_user tool result reaches
/// the model inside the same turn (no extra user turn, no lost agent session).
/// </summary>
public class AskUserSameTurnFlowTests
{
    private sealed class ScriptedChatClient : IChatClient
    {
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("streaming only");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var request = messages.ToList();
            Requests.Add(request);

            if (Requests.Count == 1)
            {
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [
                        new FunctionCallContent("call-1", AskUserTool.Name, new Dictionary<string, object?>
                        {
                            ["question"] = "Bạn muốn tóm tắt hay liệt kê?",
                            ["options"] = new[] { "Tóm tắt", "Liệt kê" },
                        }),
                    ]);
                yield break;
            }

            var result = request
                .SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>()
                .LastOrDefault();

            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(
                ChatRole.Assistant, $"Đã nhận: {result?.Result}");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static HarnessAgent CreateAgent(IChatClient client, AskUserBroker broker) =>
        client.AsHarnessAgent(new HarnessAgentOptions
        {
            Name = "test-agent",
            DisableWebSearch = true,
            DisableFileMemory = true,
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableOpenTelemetry = true,
            MaximumIterationsPerRequest = 4,
            ChatOptions = new ChatOptions
            {
                Tools = [AskUserTool.Create(broker), ToolSearchTool.Create()],
            },
        });

    [Fact]
    public async Task Tool_result_reaches_the_model_in_the_same_turn()
    {
        var client = new ScriptedChatClient();
        var broker = new AskUserBroker();
        var agent = CreateAgent(client, broker);
        var session = await agent.CreateSessionAsync();

        broker.Changed += question =>
        {
            if (question is not null) broker.TryAnswer("Tóm tắt");
        };

        var text = new StringBuilder();
        await foreach (var update in agent.RunStreamingAsync("Tóm tắt tài liệu này", session))
        {
            text.Append(update.Text);
        }

        Assert.Equal(2, client.Requests.Count);
        Assert.Contains("Đã nhận: Tóm tắt", text.ToString(), StringComparison.Ordinal);

        var toolResult = client.Requests[1]
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .Single();
        Assert.Equal("Tóm tắt", toolResult.Result);
    }

    [Fact]
    public async Task Declined_question_still_continues_the_turn()
    {
        var client = new ScriptedChatClient();
        var broker = new AskUserBroker();
        var agent = CreateAgent(client, broker);
        var session = await agent.CreateSessionAsync();

        broker.Changed += question =>
        {
            if (question is not null) broker.TryDecline();
        };

        var text = new StringBuilder();
        await foreach (var update in agent.RunStreamingAsync("Tóm tắt tài liệu này", session))
        {
            text.Append(update.Text);
        }

        Assert.Contains($"Đã nhận: {AskUserBroker.DeclinedAnswer}", text.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_while_the_question_waits_releases_the_turn()
    {
        var client = new ScriptedChatClient();
        var broker = new AskUserBroker();
        var agent = CreateAgent(client, broker);
        var session = await agent.CreateSessionAsync();

        using var cancellation = new CancellationTokenSource();
        broker.Changed += question =>
        {
            if (question is not null) cancellation.CancelAfter(TimeSpan.FromMilliseconds(20));
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in agent.RunStreamingAsync(
                "Tóm tắt tài liệu này", session, cancellationToken: cancellation.Token))
            {
            }
        });
    }
}
