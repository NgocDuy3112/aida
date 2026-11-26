namespace aida.Components.Pages.Chat;

/// <summary>Compact model name for the chat header; the full id stays in the tooltip.</summary>
internal static class ModelLabel
{
    private const int MaxLength = 18;

    public static string? Short(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;

        var name = model.Trim();
        var slash = name.LastIndexOf('/');
        if (slash >= 0 && slash < name.Length - 1)
            name = name[(slash + 1)..];

        return name.Length <= MaxLength ? name : name[..(MaxLength - 1)] + "…";
    }
}
