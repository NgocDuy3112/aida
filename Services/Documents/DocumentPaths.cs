namespace aida.Services.Documents;

internal static class DocumentPaths
{
    internal static string? Resolve(string contentRoot, string? ownerId, string fileName)
    {
        if (string.IsNullOrWhiteSpace(ownerId)) return null;

        if (ownerId != Path.GetFileName(ownerId)) return null;

        var safe = Path.GetFileName(fileName ?? string.Empty);
        if (string.IsNullOrEmpty(safe)) return null;

        return Path.Combine(contentRoot, "App_Data", "documents", ownerId, safe);
    }
}
