using aida.Data;
using aida.Services.Ingestion;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.AI;

namespace aida.Services.Documents;

internal static class ChatDocumentTools
{
    internal const int MaxReadChars = 60_000;

    internal static AIFunction CreateReadDocument(string ownerId, IWebHostEnvironment env)
    {
        Func<string, Task<string>> read = filename => ReadAsync(ownerId, env, filename);
        return AIFunctionFactory.Create(
            read,
            "read_document",
            "Read the full text of one of the user's stored documents (uploaded earlier or saved by you). " +
            "Call this before editing, extending or rewriting an existing document so no original content is lost. " +
            "The filename is the bare file name, e.g. \"report.docx\".",
            null);
    }

    internal static AIFunction CreateSaveDocument(
        string ownerId, DocumentService documents,
        GoogleWorkspaceService? google = null, UserManager<AppUser>? users = null)
    {
        Func<string, string, string, Task<string>> save =
            (filename, format, content) =>
                SaveAsync(ownerId, documents, google, users, filename, format, content);
        return AIFunctionFactory.Create(
            save,
            "save_document",
            "Create or replace a document the user can download or open. 'format' must be exactly \"docx\" " +
            "(Word), \"pdf\", \"gdoc\" (Google Doc) or \"gsheet\" (Google Sheet). 'content' is the COMPLETE " +
            "document in Markdown (headings, bullets, **bold**, `code`, ```fences and pipe tables are " +
            "supported). 'filename' is the bare file name; its extension is corrected to match the format. " +
            "To update an existing document, pass its exact file name to replace it. For gdoc/gsheet (use " +
            "them whenever the user wants a Google Doc/Sheet or a shareable online document) the result " +
            "contains a https://docs.google.com/... link — quote it verbatim in your reply; Google files are " +
            "not stored locally, so they have no /documents/ link and read_document cannot read them back. " +
            "For docx/pdf, mention the file in your reply with a Markdown download link: [filename](/documents/filename).",
            null);
    }

    internal static string? NormalizeFileName(string? filename, string? format)
    {
        var extension = format?.Trim().ToLowerInvariant();
        if (extension is not ("docx" or "pdf" or "gdoc" or "gsheet")) return null;

        var safe = Path.GetFileName(filename ?? string.Empty);
        if (string.IsNullOrWhiteSpace(safe)) return null;

        return Path.ChangeExtension(safe, extension);
    }

    private static async Task<string> ReadAsync(
        string ownerId, IWebHostEnvironment env, string filename)
    {
        var path = DocumentPaths.Resolve(env.ContentRootPath, ownerId, filename);
        if (path is null || !File.Exists(path))
            return $"Error: no document named '{filename}' belongs to this user.";

        var file = new FileInfo(path);
        try
        {
            var reader = new DocumentReader(file.Directory!);
            var document = await reader.ReadAsync(file, file.Name, cancellationToken: CancellationToken.None);
            var text = string.Join("\n\n",
                document.EnumerateContent()
                    .Select(c => c.Text)
                    .Where(t => !string.IsNullOrWhiteSpace(t)));

            if (text.Length > MaxReadChars)
                text = text[..MaxReadChars]
                    + "\n\n[truncated — the document continues beyond this point]";

            return string.IsNullOrWhiteSpace(text)
                ? "Error: the document has no extractable text (it may be scanned images)."
                : text;
        }
        catch (Exception ex)
        {
            return $"Error: could not read '{filename}': {ex.Message}";
        }
    }

    internal static async Task<string> SaveAsync(
        string ownerId, DocumentService documents,
        GoogleWorkspaceService? google, UserManager<AppUser>? users,
        string? filename, string? format, string? content)
    {
        var safe = NormalizeFileName(filename, format);
        if (safe is null)
            return "Error: filename is required and format must be exactly \"docx\", \"pdf\", \"gdoc\" or \"gsheet\".";
        if (string.IsNullOrWhiteSpace(content))
            return "Error: content is empty — provide the full document in Markdown.";

        var normalized = format!.Trim().ToLowerInvariant();
        if (normalized is "gdoc" or "gsheet")
            return await SaveToGoogleAsync(google, users, ownerId, safe, normalized, content);

        try
        {
            var title = Path.GetFileNameWithoutExtension(safe);
            var bytes = safe.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                ? PdfWriter.Write(title, content)
                : DocxWriter.Write(title, content);

            await using var stream = new MemoryStream(bytes);
            var saved = await documents.SaveAndIngestAsync(stream, safe, ownerId, CancellationToken.None);

            return $"Saved '{saved}' ({bytes.Length:N0} bytes) and made it searchable. " +
                   $"In your reply, include the download link exactly as: [{saved}](/documents/{saved})";
        }
        catch (Exception ex)
        {
            return $"Error: could not save '{safe}': {ex.Message}";
        }
    }

    private static async Task<string> SaveToGoogleAsync(
        GoogleWorkspaceService? google, UserManager<AppUser>? users,
        string ownerId, string safe, string format, string content)
    {
        if (google is null || users is null)
            return "Error: Google Workspace saving is not configured on this server.";

        var title = Path.GetFileNameWithoutExtension(safe);
        try
        {
            var user = await users.FindByIdAsync(ownerId);
            var result = format == "gdoc"
                ? await google.CreateDocAsync(user?.GoogleRefreshToken, title, content)
                : await google.CreateSheetAsync(user?.GoogleRefreshToken, title, content);

            if (result.Error is not null)
                return $"Error: {result.Error}";

            var kind = format == "gdoc" ? "Google Doc" : "Google Sheet";
            return $"Saved '{title}' as a {kind} (private to the owner; not stored locally, so " +
                   "read_document cannot read it back). " +
                   $"In your reply, include the link exactly as: [{title}]({result.Url})";
        }
        catch (Exception ex)
        {
            return $"Error: could not save '{title}' to Google: {ex.Message}";
        }
    }
}
