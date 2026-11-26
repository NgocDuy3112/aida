namespace aida.Components.Pages.Chat;

/// <summary>
/// Tool usage of one finished assistant turn, kept so the transcript can show what ran
/// after the live process panel disappears.
/// </summary>
public sealed record ProcessTrace(int ToolCalls, IReadOnlyList<string> Steps);
