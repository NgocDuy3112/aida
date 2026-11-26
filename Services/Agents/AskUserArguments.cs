using System.Text.Json;

namespace aida.Services.Agents;

/// <summary>
/// ask_user arguments arrive as plain objects from the local model and as <see cref="JsonElement"/>
/// values from the streaming response; read both shapes here.
/// </summary>
internal static class AskUserArguments
{
    public static string? Question(IDictionary<string, object?> arguments) =>
        ReadString(arguments, "question");

    public static List<string> Options(IDictionary<string, object?> arguments) =>
        ReadStrings(arguments, "options");

    public static bool AllowFreeText(IDictionary<string, object?> arguments) =>
        ReadBool(arguments, "allow_free_text") ?? true;

    public static string? ReadString(IDictionary<string, object?> arguments, string key) =>
        arguments.TryGetValue(key, out var value)
            ? value switch
            {
                string text => text,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                _ => null
            }
            : null;

    public static bool? ReadBool(IDictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value)) return null;

        return value switch
        {
            bool flag => flag,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ => null
        };
    }

    public static List<string> ReadStrings(IDictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value)) return [];

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.Array } element =>
                element.EnumerateArray().Select(item => item.GetString()).OfType<string>().ToList(),
            System.Collections.IEnumerable items when items is not string =>
                items.Cast<object?>().Select(item => item?.ToString()).OfType<string>().ToList(),
            _ => []
        };
    }
}
