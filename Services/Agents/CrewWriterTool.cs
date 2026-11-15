using aida.Services.Usage;
using Microsoft.Extensions.AI;
using OpenAI;

namespace aida.Services.Agents;


public class CrewWriterTool(
    OpenAIClient openAi,
    IUsageTracker tracker,
    PricingTable pricing)
{
    internal const string WriterInstructions =
        "You are the writer in a crew of agents. Produce a COMPLETE, publication-ready document in "
        + "Markdown exactly as the spec requests: honor the title, purpose, audience, required "
        + "sections and approximate length. Use headings, bullets, **bold**, pipe tables and code "
        + "fences where they genuinely help the reader. Write in the document's language — default "
        + "Vietnamese unless the spec says otherwise. Return ONLY the document text: no preamble, "
        + "no commentary about your process, no placeholder sections.";

    public AIFunction Create(string userId, string model, string thinking) =>
        AIFunctionFactory.Create(
            (string spec, CancellationToken ct) => DraftAsync(userId, model, thinking, spec, ct),
            "draft_document",
            "Draft a COMPLETE document in Markdown from a detailed spec (title, purpose, audience, "
            + "required sections, length). Returns only the draft text. Call this BEFORE save_document "
            + "for any multi-section or long document, then pass the returned draft VERBATIM as "
            + "save_document's 'content'. Skip this tool for short answers you can write yourself.",
            null);

    internal virtual async Task<string> DraftAsync(
        string userId, string model, string thinking, string spec, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return "Error: draft_document needs a spec (title, purpose, audience, required sections).";

        var client = new UsageReportingChatClient(
            openAi.GetChatClient(model).AsIChatClient(),
            tracker, pricing, userId, model, "agent",
            stepLabel: $"writer: {Truncate(spec, 48)}");

        var response = await client.GetResponseAsync(
        [
            new ChatMessage(ChatRole.System, WriterInstructions),
            new ChatMessage(ChatRole.User, spec),
        ],
        new ChatOptions { Reasoning = CrewOptions.Reasoning(thinking) },
        ct);

        var text = response.Text?.Trim();
        return string.IsNullOrEmpty(text)
            ? "Error: the writer returned no draft — rephrase the spec or draft it yourself."
            : text;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
