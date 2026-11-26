using aida.Services.Documents;
using aida.Services.Ingestion;
using aida.Tests.Components;
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

    private static (DocumentService Service, string Root) CreateService()
    {
        var root = Path.Combine(Path.GetTempPath(), "aida-documents", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var ingestor = new DocumentIngestor(
            new NoScopeFactory(),
            new StubEmbeddingGenerator(),
            // The fixtures are read without OCR, so a missing Tesseract never decides the test.
            new OcrOptions { Enabled = false },
            NullLogger<DocumentIngestor>.Instance);

        return (new DocumentService(
            new FakeWebHostEnvironment(root),
            ingestor,
            NullLogger<DocumentService>.Instance), root);
    }

    private static MemoryStream BlankPdf()
    {
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
