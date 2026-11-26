using aida.Services.Documents;
using aida.Services.Ingestion;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using UglyToad.PdfPig;

namespace aida.Tests.Services;

public class PdfPigReaderTests
{
    private const string PdfMediaType = "application/pdf";
    private static readonly byte[] ScanPng = File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "TestData", "ocr-scan-vi.png"));

    static PdfPigReaderTests()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        QuestPDF.Settings.ThrowOnMissingFontFamilies = false;
    }

    [Fact]
    public async Task Scanned_page_is_ocrd_and_text_is_in_document()
    {
        using var pdf = BuildImageOnlyPdf();
        var engine = new RecordingOcrEngine("HÓA ĐƠN THANH TOÁN\nSố tiền: 1.250.000 VND");
        var reader = new PdfPigReader(engine);

        var document = await reader.ReadAsync(pdf, "scan.pdf", PdfMediaType);

        Assert.Equal(1, engine.Calls);
        Assert.Equal(OcrOptions.DefaultLanguage, engine.LastLanguage);
        var text = string.Join("\n", document.EnumerateContent().Select(c => c.Text));
        Assert.Contains("HÓA ĐƠN THANH TOÁN", text);
        Assert.Contains("1.250.000 VND", text);
    }

    [Fact]
    public async Task Text_layer_page_does_not_call_ocr()
    {
        var bytes = PdfWriter.Write("Quarterly Report", """
            ## Summary

            Hello document readers.
            """);
        using var pdf = new MemoryStream(bytes);
        var engine = new RecordingOcrEngine("should not be used");
        var reader = new PdfPigReader(engine);

        var document = await reader.ReadAsync(pdf, "report.pdf", PdfMediaType);

        Assert.Equal(0, engine.Calls);
        var text = string.Join("\n", document.EnumerateContent().Select(c => c.Text));
        Assert.Contains("Hello document readers", text);
    }

    [Fact]
    public async Task Ocr_failure_is_not_swallowed()
    {
        using var pdf = BuildImageOnlyPdf();
        var reader = new PdfPigReader(new ThrowingOcrEngine());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(pdf, "scan.pdf", PdfMediaType));

        Assert.Contains("ocr exploded", exception.Message);
    }

    [Fact]
    public async Task Scanned_page_without_extractable_image_throws()
    {
        using var pdf = BuildVectorOnlyPdf();
        var engine = new RecordingOcrEngine("unused");
        var reader = new PdfPigReader(engine);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(pdf, "vector.pdf", PdfMediaType));

        Assert.Contains("no OCR-readable image", exception.Message);
    }

    [Fact]
    public async Task Image_only_fixture_has_no_text_layer()
    {
        using var pdf = BuildImageOnlyPdf();

        using var document = PdfDocument.Open(pdf);
        var letters = document.GetPages().SelectMany(p => p.Letters).Count();

        Assert.True(letters < new OcrOptions().MinLettersPerPage,
            $"fixture still has {letters} letters; it is not a scanned document");
    }

    private static MemoryStream BuildImageOnlyPdf()
    {
        var bytes = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1, Unit.Centimetre);
                page.Content().Image(ScanPng).FitArea();
            });
        }).GeneratePdf();

        return new MemoryStream(bytes);
    }

    private static MemoryStream BuildVectorOnlyPdf()
    {
        var bytes = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1, Unit.Centimetre);
                page.Content().Column(column =>
                {
                    column.Item().Height(120).LineHorizontal(2);
                    column.Item().Height(120).LineHorizontal(2);
                });
            });
        }).GeneratePdf();

        return new MemoryStream(bytes);
    }

    private sealed class RecordingOcrEngine(string text) : IPdfOcrEngine
    {
        public int Calls { get; private set; }

        public string? LastLanguage { get; private set; }

        public Task<string> RecognizeAsync(Stream image, string language, CancellationToken cancellationToken)
        {
            Calls++;
            LastLanguage = language;
            return Task.FromResult(text);
        }
    }

    private sealed class ThrowingOcrEngine : IPdfOcrEngine
    {
        public Task<string> RecognizeAsync(Stream image, string language, CancellationToken cancellationToken)
            => throw new InvalidOperationException("ocr exploded");
    }
}
