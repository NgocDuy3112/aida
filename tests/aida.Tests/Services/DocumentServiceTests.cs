using aida.Services.Documents;
using aida.Services.Ingestion;
using aida.Tests.Components;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace aida.Tests.Services;

public class DocumentServiceTests
{
    static DocumentServiceTests()
    {
        // QuestPDF builds the blank fixture; the tests only care about its (missing) text layer.
        QuestPDF.Settings.License = LicenseType.Community;
    }

    [Theory]
    [InlineData("report.pdf", true)]
    [InlineData("REPORT.PDF", true)]   
    [InlineData("notes.md", true)]
    [InlineData("sheet.xlsx", true)]
    [InlineData("word.docx", true)]
    [InlineData("script.txt", false)]
    [InlineData("malware.exe", false)]
    [InlineData("noextension", false)]
    [InlineData("archive.pdf.exe", false)] 
    [InlineData("../../etc/passwd", false)] 
    [InlineData("../../evil.pdf", true)]    
    public void IsSupportedFileName(string fileName, bool expected)
    {
        Assert.Equal(expected, DocumentService.IsSupportedFileName(fileName));
    }

    [Fact]
    public async Task SaveAndIngest_rejects_unsupported_files_before_touching_dependencies()
    {
        var service = new DocumentService(
            env: null!,
            ingestor: null!,
            logger: NullLogger<DocumentService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAndIngestAsync(new MemoryStream(), "malware.exe", "owner-1"));

        Assert.Contains(".exe", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveAndIngest_deletes_the_file_when_ingestion_throws()
    {
        var (service, root) = CreateService();
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                service.SaveAndIngestAsync(new MemoryStream([1, 2, 3, 4]), "broken.pdf", "owner-1"));

            Assert.False(File.Exists(StoredPath(root, "broken.pdf")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAndIngest_deletes_a_file_with_no_readable_text_and_reports_it()
    {
        var (service, root) = CreateService();
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.SaveAndIngestAsync(BlankPdf(), "blank.pdf", "owner-1"));

            Assert.Contains("no readable text", ex.Message);
            Assert.False(File.Exists(StoredPath(root, "blank.pdf")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("notes.md")]
    [InlineData("notes.docx")]
    public async Task SaveAndIngest_stores_a_readable_file_and_its_chunks(string fileName)
    {
        var (service, root, ingestor) = CreateServiceWithIngestor(new OcrOptions { Enabled = false });
        try
        {
            var bytes = fileName.EndsWith(".md", StringComparison.Ordinal)
                ? Encoding.UTF8.GetBytes("# Biên bản\n\nNội dung cuộc họp tháng 11.")
                : DocxWriter.Write("Biên bản", "# Biên bản\n\nNội dung cuộc họp tháng 11.");

            var saved = await service.SaveAndIngestAsync(new MemoryStream(bytes), fileName, "owner-1");

            Assert.Equal(fileName, saved);
            var stored = StoredPath(root, fileName);
            Assert.True(File.Exists(stored));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(stored));
            // Nothing but the stored file is left behind: the temp upload was renamed, not copied.
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(stored)!, "~upload-*"));
            Assert.Contains("Nội dung cuộc họp", Assert.Single(ingestor.StoredChunks));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAndIngest_keeps_the_stored_file_when_an_overwrite_has_no_readable_text()
    {
        var (service, root, ingestor) = CreateServiceWithIngestor(new OcrOptions { Enabled = false });
        try
        {
            var original = Encoding.UTF8.GetBytes("# Gốc\n\nNội dung đã lưu.");
            await service.SaveAndIngestAsync(new MemoryStream(original), "notes.md", "owner-1");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAndIngestAsync(
                new MemoryStream(Encoding.UTF8.GetBytes("   \n\t\n")), "notes.md", "owner-1"));

            Assert.Contains("no readable text", ex.Message);
            var stored = StoredPath(root, "notes.md");
            Assert.True(File.Exists(stored));
            Assert.Equal(original, await File.ReadAllBytesAsync(stored));
            // The stored chunks described the replaced content, so retrieval must not answer with them.
            Assert.Equal(["notes.md"], ingestor.ClearedDocuments);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAndIngest_keeps_the_stored_file_and_its_chunks_when_an_overwrite_throws()
    {
        var (service, root, ingestor) = CreateServiceWithIngestor(new OcrOptions { Enabled = false });
        try
        {
            var original = DocxWriter.Write("Gốc", "# Gốc\n\nNội dung đã lưu.");
            await service.SaveAndIngestAsync(new MemoryStream(original), "notes.docx", "owner-1");
            var storedChunk = Assert.Single(ingestor.StoredChunks);

            await Assert.ThrowsAnyAsync<Exception>(() => service.SaveAndIngestAsync(
                new MemoryStream([1, 2, 3, 4]), "notes.docx", "owner-1"));

            Assert.Equal(original, await File.ReadAllBytesAsync(StoredPath(root, "notes.docx")));
            Assert.Empty(ingestor.ClearedDocuments);
            Assert.Equal(storedChunk, Assert.Single(ingestor.StoredChunks));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (DocumentService Service, string Root) CreateService()
    {
        var (service, root, _) = CreateServiceWithIngestor(new OcrOptions { Enabled = false });
        return (service, root);
    }

    private static (DocumentService Service, string Root, RecordingIngestor Ingestor) CreateServiceWithIngestor(
        OcrOptions ocrOptions)
    {
        var root = Path.Combine(Path.GetTempPath(), "aida-documents", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        // The fixtures are read without OCR by default, so a missing Tesseract never decides the test.
        var ingestor = new RecordingIngestor(
            new NoScopeFactory(),
            new FixedEmbeddingGenerator(),
            ocrOptions,
            NullLogger<DocumentIngestor>.Instance);

        return (new DocumentService(
            new FakeWebHostEnvironment(root),
            ingestor,
            NullLogger<DocumentService>.Instance), root, ingestor);
    }

    private static MemoryStream BlankPdf()    {
        var bytes = QuestPDF.Fluent.Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Content().Height(20);
            });
        }).GeneratePdf();

        return new MemoryStream(bytes);
    }

    private static string StoredPath(string root, string fileName) =>
        Path.Combine(root, "App_Data", "documents", "owner-1", fileName);
}
