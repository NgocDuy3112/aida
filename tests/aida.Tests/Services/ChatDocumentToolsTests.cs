using aida.Services.Documents;
using aida.Services.Ingestion;
using aida.Tests.Components;
using Microsoft.Extensions.Logging.Abstractions;

namespace aida.Tests.Services;

public class ChatDocumentToolsTests
{
    [Theory]
    [InlineData("report", "docx", "report.docx")]
    [InlineData("report", "pdf", "report.pdf")]
    [InlineData("report.docx", "PDF", "report.pdf")]
    public void NormalizeFileName_accepts_local_formats(string name, string format, string expected)
        => Assert.Equal(expected, ChatDocumentTools.NormalizeFileName(name, format));

    [Theory]
    [InlineData("report", "gdoc")]
    [InlineData("report", "gsheet")]
    public void NormalizeFileName_rejects_google_formats(string name, string format)
        => Assert.Null(ChatDocumentTools.NormalizeFileName(name, format));

    [Fact]
    public async Task Save_writes_a_document_that_is_stored_and_searchable()
    {
        var root = Path.Combine(Path.GetTempPath(), "aida-save", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var ingestor = new RecordingIngestor(
                new NoScopeFactory(), new FixedEmbeddingGenerator(), new OcrOptions { Enabled = false },
                NullLogger<DocumentIngestor>.Instance);
            var documents = new DocumentService(
                new FakeWebHostEnvironment(root), ingestor, NullLogger<DocumentService>.Instance);

            var message = await ChatDocumentTools.SaveAsync(
                "user-1", documents, "bao-cao", "docx", "# Báo cáo\n\nDoanh thu quý 3 tăng 12%.");

            Assert.StartsWith("Saved 'bao-cao.docx'", message);
            Assert.Contains("(/documents/bao-cao.docx)", message);
            Assert.True(File.Exists(Path.Combine(
                root, "App_Data", "documents", "user-1", "bao-cao.docx")));
            Assert.NotEmpty(ingestor.StoredChunks);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Save_rejects_google_format_with_the_accepted_names()
    {
        var msg = await ChatDocumentTools.SaveAsync(
            "user-1", null!, "bao-cao", "gdoc", "nội dung");

        Assert.StartsWith("Error:", msg);
        Assert.Contains("docx", msg);
        Assert.Contains("pdf", msg);
        Assert.DoesNotContain("gdoc", msg);
    }

    [Fact]
    public async Task Save_rejects_unknown_format_with_the_accepted_names()
    {
        var msg = await ChatDocumentTools.SaveAsync(
            "user-1", null!, "x", "txt", "nội dung");

        Assert.StartsWith("Error:", msg);
        Assert.Contains("docx", msg);
        Assert.Contains("pdf", msg);
    }
}
