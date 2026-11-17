using Microsoft.Extensions.AI;

namespace aida.Services.Agents;

internal static class AskUserTool
{
    internal const string Instructions =
        "- IMPORTANT — asking the user: whenever you need to ask the user anything (clarification, " +
        "missing details, choosing among options), you MUST call the ask_user tool with the question. " +
        "Never put questions to the user in your reply text. Call ask_user at most once per turn; " +
        "after the tool returns, end your turn immediately and wait — the user's answer arrives in " +
        "the next turn. Do not use ask_user for requests you can answer directly.";

    internal static AIFunction Create()
    {
        return AIFunctionFactory.Create(
            (string question) => Task.FromResult(
                "The question is now displayed to the user. End your turn immediately and wait for their reply."),
            "ask_user",
            "Ask the user a clarifying question when their request is ambiguous or incomplete. " +
            "'question' must be a short, specific question written in Vietnamese.",
            null);
    }
}
