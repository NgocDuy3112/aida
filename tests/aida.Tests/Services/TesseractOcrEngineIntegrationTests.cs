using aida.Services.Ingestion;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using UglyToad.PdfPig;

namespace aida.Tests.Services;

[Trait("Integration", "Ocr")]
public class TesseractOcrEngineIntegrationTests
{
    static TesseractOcrEngineIntegrationTests()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        QuestPDF.Settings.ThrowOnMissingFontFamilies = false;
    }

    [Fact]
    public async Task Reads_vietnamese_text_from_scanned_page()
    {
        if (Environment.GetEnvironmentVariable("AIDA_OCR_IT") != "1")
        {
            return;
        }

        var png = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "ocr-scan-vi.png"));
        var pdfBytes = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1, Unit.Centimetre);
                page.Content().Image(png).FitArea();
            });
        }).GeneratePdf();

        using var pdf = new MemoryStream(pdfBytes);
        using var engine = new TesseractOcrEngine(new OcrOptions());
        var reader = new PdfPigReader(engine);

        var document = await reader.ReadAsync(pdf, "scan.pdf", "application/pdf");

        var text = string.Join("\n", document.EnumerateContent().Select(c => c.Text));
        Assert.Contains("HÓA ĐƠN", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1.250.000", text);
    }
}
