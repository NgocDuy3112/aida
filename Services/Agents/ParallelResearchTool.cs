using System.Text;
using aida.Services.Documents;
using aida.Services.Usage;
using Microsoft.Extensions.AI;
using OpenAI;

namespace aida.Services.Agents;

public class ParallelResearchTool(
    OpenAIClient openAi,
    IUsageTracker tracker,
    PricingTable pricing,
    IWebHostEnvironment env,
    ILogger<ParallelResearchTool> logger)
{
    internal const int MaxWorkers = 3;

    private const string ResearcherInstructions =
        "You are a research sub-agent in a crew of agents. Find and report the facts relevant ONLY to " +
        "the task you are given. Use the read_document tool when the task refers to a stored file, and " +
        "prefer concrete details (numbers, dates, names) over generalities. Report findings concisely as " +
        "bullet points; mark anything you could not verify as unverified, and state plainly when " +
        "information is missing. Do not ask questions, do not address the original user directly, and do " +
        "not draft final documents — findings only.";

    public AIFunction Create(string userId, string model, string thinking) =>
        AIFunctionFactory.Create(
            (string[] tasks, CancellationToken ct) => RunAsync(tasks, userId, model, ct, thinking),
            "parallel_research",
            $"Run up to {MaxWorkers} INDEPENDENT research sub-tasks concurrently with lightweight "
            + "researcher agents in isolated contexts (each worker can read documents; no conversation "
            + "history is shared). Use only when the tasks do not depend on each other — sequential or "
            + "dependent steps should be done directly. Returns the merged findings of every task.",
            null);

    internal async Task<string> RunAsync(
        string[]? tasks, string userId, string model, CancellationToken ct,
        string? thinking = null)
    {
        var cleaned = (tasks ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        if (cleaned.Count == 0)
            return $"Error: parallel_research needs 1–{MaxWorkers} task descriptions.";

        var truncated = cleaned.Count > MaxWorkers;
        var selected = cleaned.Take(MaxWorkers).ToList();

        var results = await Task.WhenAll(
            selected.Select(t => RunWithRetryAsync(t, userId, model, thinking, ct)));

        var builder = new StringBuilder();
        if (truncated)
            builder.AppendLine($"(only the first {MaxWorkers} of {cleaned.Count} tasks were run)")
                   .AppendLine();

        for (var i = 0; i < results.Length; i++)
        {
            builder.AppendLine($"=== sub-task {i + 1}/{results.Length}: {selected[i]} ===");
            builder.AppendLine(results[i]);
            if (i < results.Length - 1) builder.AppendLine();
        }

        return builder.ToString().Trim();
    }

    private async Task<string> RunWithRetryAsync(
        string task, string userId, string model, string? thinking, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                return await RunWorkerAsync(task, userId, model, ct, thinking);
            }
            catch (OperationCanceledException)
            {
                throw; 
            }
            catch (Exception ex) when (attempt == 2)
            {
                logger.LogError(ex,
                    "parallel_research: task failed after {Attempt} attempts: {Task}",
                    attempt, task);
                return $"[sub-task failed after {attempt} attempts: {ex.Message}]";
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "parallel_research: attempt {Attempt}/2 failed, retrying task: {Task}",
                    attempt, task);
            }
        }

        return "[sub-task failed]"; 
    }

    internal virtual async Task<string> RunWorkerAsync(
        string task, string userId, string model, CancellationToken ct,
        string? thinking = null)
    {
        var client = new UsageReportingChatClient(
            openAi.GetChatClient(model).AsIChatClient(),
            tracker, pricing, userId, model, "agent",
            stepLabel: $"worker: {Truncate(task, 48)}");

        var response = await client.GetResponseAsync(
        [
            new ChatMessage(ChatRole.System, ResearcherInstructions),
            new ChatMessage(ChatRole.User, task),
        ],
        new ChatOptions
        {
            Tools = [ChatDocumentTools.CreateReadDocument(userId, env)],
            Reasoning = CrewOptions.Reasoning(thinking),
        },
        ct);

        var text = response.Text?.Trim();
        return string.IsNullOrEmpty(text) ? "(worker returned no text)" : text;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
