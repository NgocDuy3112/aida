namespace aida.Services;

public enum ChatMode
{
    Ask,

    Agent,
}

internal static class ChatModePolicy
{
    internal static bool UsesTools(ChatMode mode) => mode == ChatMode.Agent;

    internal static string? ForcedModel(ChatMode mode, string? pinnedModel, string? strongModel) =>
        pinnedModel ?? (mode == ChatMode.Agent && !string.IsNullOrWhiteSpace(strongModel)
            ? strongModel
            : null);
}
