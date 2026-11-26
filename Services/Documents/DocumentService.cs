using aida.Services.Ingestion;

namespace aida.Services.Documents;

public class DocumentService(
    IWebHostEnvironment env,
    DocumentIngestor ingestor,
    ILogger<DocumentService> logger)
{
    private static readonly HashSet<string> AllowedExtensions = [".pdf", ".md", ".docx", ".xlsx"];

    internal static bool IsSupportedFileName(string fileName)
    {
        var extension = Path.GetExtension(Path.GetFileName(fileName)).ToLowerInvariant();
        return AllowedExtensions.Contains(extension);
    }

    public async Task<string> SaveAndIngestAsync(
        Stream content, string fileName, string ownerId, CancellationToken ct = default)
    {
        var safeName = Path.GetFileName(fileName);

        if (!IsSupportedFileName(safeName))
        {
            var extension = Path.GetExtension(safeName).ToLowerInvariant();
            throw new InvalidOperationException($"Unsupported file type '{extension}'.");
        }

        var directory = Path.Combine(env.ContentRootPath, "App_Data", "documents", ownerId);
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, safeName);
        await using (var target = File.Create(path))
        {
            await content.CopyToAsync(target, ct);
        }

        int chunks;
        try
        {
            chunks = await ingestor.IngestAsync(new FileInfo(path), safeName, ownerId, ct);
        }
        catch
        {
            DeleteStoredFile(path, safeName);
            throw;
        }

        if (chunks == 0)
        {
            DeleteStoredFile(path, safeName);
            throw new InvalidOperationException(
                $"'{safeName}' has no readable text — the file may be a scanned image or an empty document; " +
                "it was not stored.");
        }

        logger.LogInformation("Stored and ingested '{FileName}' for owner {OwnerId}.", safeName, ownerId);
        return safeName;
    }

    /// <summary>Removes a file whose ingestion failed, so an unreadable upload does not linger.</summary>
    private void DeleteStoredFile(string path, string fileName)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete unreadable file '{FileName}'.", fileName);
        }
    }
}
