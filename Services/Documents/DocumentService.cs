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

        // The bytes land in a temp file first and only replace the stored file once ingestion succeeded,
        // so a failed re-upload cannot destroy the copy the user already had. This is not a lock: the composer
        // uploads with a SemaphoreSlim(3) and two uploads of the same name can interleave, last move wins.
        var path = Path.Combine(directory, safeName);
        var tempPath = Path.Combine(directory, $"~upload-{Guid.NewGuid():N}-{safeName}");
        var replacesStoredFile = File.Exists(path);

        try
        {
            await using (var target = File.Create(tempPath))
            {
                await content.CopyToAsync(target, ct);
            }

            var chunks = await IngestAsync(tempPath, safeName, ownerId, replacesStoredFile, ct);

            try
            {
                File.Move(tempPath, path, overwrite: true);
            }
            catch
            {
                // The index now describes bytes that never reached the stored path; drop it rather than
                // retrieving content the user cannot open.
                await ingestor.RemoveChunksAsync(ownerId, safeName, ct);
                throw;
            }

            logger.LogInformation("Stored and ingested '{FileName}' for owner {OwnerId}.", safeName, ownerId);
            return safeName;
        }
        finally
        {
            DeleteTemporaryFile(tempPath, safeName);
        }
    }

    /// <summary>
    /// Ingests the uploaded bytes before they replace the stored file. A document with no readable text
    /// keeps a stored file it was going to overwrite, but its stale chunks are dropped so retrieval cannot
    /// return content of a document the user has just replaced.
    /// </summary>
    private async Task<int> IngestAsync(
        string tempPath, string safeName, string ownerId, bool replacesStoredFile, CancellationToken ct)
    {
        // A throw here happens before the stored chunks are replaced, so the stored file and its index stay as they were.
        var chunks = await ingestor.IngestAsync(new FileInfo(tempPath), safeName, ownerId, ct);

        if (chunks > 0)
        {
            return chunks;
        }

        if (replacesStoredFile)
        {
            await ingestor.RemoveChunksAsync(ownerId, safeName, ct);
        }

        throw new InvalidOperationException(
            $"'{safeName}' has no readable text — the file may be a scanned image or an empty document; " +
            "it was not stored.");
    }

    /// <summary>Removes the temp upload, so a failed ingestion leaves no orphan file behind.</summary>
    private void DeleteTemporaryFile(string path, string fileName)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete the temporary upload for '{FileName}'.", fileName);
        }
    }
}
