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
    private static readonly byte[] ScanJpeg = File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "TestData", "ocr-scan-vi.jpg"));

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
    public async Task Too_many_pages_throws_before_any_ocr_runs()
    {
        using var pdf = BuildImageOnlyPdf(pages: 2);
        var engine = new RecordingOcrEngine("text");
        var reader = new PdfPigReader(engine, new OcrOptions { MaxPages = 1 });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(pdf, "big.pdf", PdfMediaType));

        Assert.Contains("MaxPages", exception.Message);
        Assert.Equal(0, engine.Calls);
    }

    [Fact]
    public async Task Ocr_running_past_the_timeout_throws_a_described_error()
    {
        using var pdf = BuildImageOnlyPdf();
        var reader = new PdfPigReader(new SlowOcrEngine(), new OcrOptions { OcrTimeoutSeconds = 1 });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.ReadAsync(pdf, "slow.pdf", PdfMediaType));

        Assert.Contains("timed out", exception.Message);
    }

    [Fact]
    public async Task Cancellation_between_pages_stops_reading()
    {
        using var pdf = BuildImageOnlyPdf(pages: 2);
        using var cancellation = new CancellationTokenSource();
        var reader = new PdfPigReader(new CancellingOcrEngine(cancellation));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadAsync(pdf, "two-pages.pdf", PdfMediaType, cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
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

    private static MemoryStream BuildImageOnlyPdf(int pages = 1)
    {
        var bytes = Document.Create(container =>
        {
            for (var page = 0; page < pages; page++)
            {
                container.Page(sheet =>
                {
                    sheet.Size(PageSizes.A4);
                    sheet.Margin(1, Unit.Centimetre);
                    sheet.Content().Image(ScanPng).FitArea();
                });
            }
        }).GeneratePdf();

        return new MemoryStream(bytes);
    }

    [Fact]
    public async Task Jpeg_scan_is_ocrd_without_png_conversion()
    {
        using var pdf = BuildJpegOnlyPdf();
        var engine = new RecordingOcrEngine("HÓA ĐƠN THANH TOÁN\nSố tiền: 1.250.000 VND");
        var reader = new PdfPigReader(engine);

        var document = await reader.ReadAsync(pdf, "jpeg-scan.pdf", PdfMediaType);

        Assert.Equal(1, engine.Calls);
        // TryGetPng cannot convert JPEG, so the engine must receive the encoded JPEG itself.
        Assert.Equal((byte)0xFF, engine.LastImage[0]);
        Assert.Equal((byte)0xD8, engine.LastImage[1]);
        var text = string.Join("\n", document.EnumerateContent().Select(c => c.Text));
        Assert.Contains("1.250.000 VND", text);
    }

    [Fact]
    public async Task Thin_text_layer_page_is_kept_when_ocr_finds_no_image()
    {
        using var pdf = BuildThinTextPdf();
        var engine = new RecordingOcrEngine("should not be used");
        var reader = new PdfPigReader(engine);

        var document = await reader.ReadAsync(pdf, "cover.pdf", PdfMediaType);

        Assert.Equal(0, engine.Calls);
        var text = string.Join("\n", document.EnumerateContent().Select(c => c.Text));
        Assert.Contains("Report 2024", text);
    }

    [Fact]
    public void Thin_text_fixture_is_below_the_ocr_threshold_but_not_empty()
    {
        using var pdf = BuildThinTextPdf();
        using var document = PdfDocument.Open(pdf);
        var letters = document.GetPages().Single().Letters.Count;

        Assert.InRange(letters, 1, new OcrOptions().MinLettersPerPage - 1);
    }

    [Fact]
    public void Jpeg_fixture_has_no_text_layer()
    {
        using var pdf = BuildJpegOnlyPdf();
        using var document = PdfDocument.Open(pdf);

        Assert.Empty(document.GetPages().Single().Letters);
    }

    private static MemoryStream BuildJpegOnlyPdf()
    {
        var bytes = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1, Unit.Centimetre);
                page.Content().Image(ScanJpeg).FitArea();
            });
        }).GeneratePdf();

        return new MemoryStream(bytes);
    }

    private static MemoryStream BuildThinTextPdf()
    {
        var bytes = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1, Unit.Centimetre);
                page.Content().Text("Report 2024");
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

        public byte[] LastImage { get; private set; } = [];

        public Task<string> RecognizeAsync(Stream image, string language, CancellationToken cancellationToken)
        {
            Calls++;
            LastLanguage = language;
            using var buffer = new MemoryStream();
            image.CopyTo(buffer);
            LastImage = buffer.ToArray();
            return Task.FromResult(text);
        }
    }

    private sealed class ThrowingOcrEngine : IPdfOcrEngine
    {
        public Task<string> RecognizeAsync(Stream image, string language, CancellationToken cancellationToken)
            => throw new InvalidOperationException("ocr exploded");
    }

    /// <summary>Blocks until the token fires, modelling Tesseract on a heavy scan.</summary>
    private sealed class SlowOcrEngine : IPdfOcrEngine
    {
        public async Task<string> RecognizeAsync(
            Stream image, string language, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return string.Empty;
        }
    }

    /// <summary>Cancels the caller's token on the first page, so the next page sees a cancelled read.</summary>
    private sealed class CancellingOcrEngine(CancellationTokenSource cancellation) : IPdfOcrEngine
    {
        public Task<string> RecognizeAsync(Stream image, string language, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.FromResult("trang một");
        }
    }
}
