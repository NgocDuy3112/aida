using Microsoft.Extensions.DataIngestion;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace aida.Services.Ingestion;

internal sealed class PdfPigReader(IPdfOcrEngine? ocrEngine = null, OcrOptions? ocrOptions = null) : IngestionDocumentReader
{
    private readonly OcrOptions _ocrOptions = ocrOptions ?? new OcrOptions();

    public override async Task<IngestionDocument> ReadAsync(Stream source, string identifier, string mediaType, CancellationToken cancellationToken = default)
    {
        var ownsEngine = false;
        var engine = ocrEngine;
        if (engine is null && _ocrOptions.Enabled)
        {
            engine = new TesseractOcrEngine(_ocrOptions);
            ownsEngine = true;
        }

        try
        {
            // The timeout covers the whole document: PdfPig reads pages one by one and the OCR engine
            // cannot be interrupted inside Process(), so failing between pages is the granularity we get.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_ocrOptions.OcrTimeoutSeconds));
            var token = timeout.Token;

            using var pdf = PdfDocument.Open(source);
            // A text PDF is read page by page without OCR, so the page limit only guards the OCR path:
            // it fires on the first page that needs OCR, before any OCR work is spent.
            var ocrPageLimit = new OcrPageLimit(pdf.NumberOfPages, identifier, _ocrOptions);

            var document = new IngestionDocument(identifier);
            foreach (var page in pdf.GetPages())
            {
                token.ThrowIfCancellationRequested();
                document.Sections.Add(await GetPageSectionAsync(page, identifier, engine, ocrPageLimit, token));
            }
            return document;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Reading '{identifier}' timed out after {_ocrOptions.OcrTimeoutSeconds}s " +
                "(Ocr:OcrTimeoutSeconds); the document is too large or its scans take too long to OCR.", ex);
        }
        finally
        {
            if (ownsEngine)
            {
                (engine as IDisposable)?.Dispose();
            }
        }
    }

    /// <summary>Refuses a document whose OCR would span more pages than <c>Ocr:MaxPages</c> allows.</summary>
    private sealed class OcrPageLimit(int pageCount, string identifier, OcrOptions options)
    {
        private bool _checked;

        public void ThrowIfTooManyPages()
        {
            if (_checked) return;
            _checked = true;

            if (pageCount <= options.MaxPages) return;

            throw new InvalidOperationException(
                $"'{identifier}' has {pageCount} pages but Ocr:MaxPages is {options.MaxPages}; " +
                "split the document or raise the limit.");
        }
    }

    private async Task<IngestionDocumentSection> GetPageSectionAsync(
        Page pdfPage, string identifier, IPdfOcrEngine? engine, OcrPageLimit ocrPageLimit,
        CancellationToken cancellationToken)
    {
        var section = new IngestionDocumentSection
        {
            PageNumber = pdfPage.Number,
        };

        if (engine is not null && pdfPage.Letters.Count < _ocrOptions.MinLettersPerPage)
        {
            ocrPageLimit.ThrowIfTooManyPages();
            var scanned = await TryReadScannedPageAsync(pdfPage, engine, cancellationToken);
            if (scanned is not null)
            {
                section.Elements.Add(scanned);
                return section;
            }

            // A thin text layer (a cover page, "Report 2024") is still readable, so keep it instead of
            // failing the document; only a page with no text layer at all is unreadable.
            if (pdfPage.Letters.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Page {pdfPage.Number} of '{identifier}' has no text layer and no OCR-readable image; " +
                    "the page cannot be ingested.");
            }
        }

        var letters = pdfPage.Letters;
        var words = NearestNeighbourWordExtractor.Instance.GetWords(letters);

        foreach (var textBlock in DocstrumBoundingBoxes.Instance.GetBlocks(words))
        {
            section.Elements.Add(new IngestionDocumentParagraph(textBlock.Text)
            {
                Text = textBlock.Text
            });
        }

        return section;
    }

    /// <summary>OCR text of the page images, or <c>null</c> when no image yielded any text.</summary>
    private async Task<IngestionDocumentParagraph?> TryReadScannedPageAsync(
        Page pdfPage, IPdfOcrEngine engine, CancellationToken cancellationToken)
    {
        var recognized = new List<string>();

        foreach (var image in pdfPage.GetImages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var imageStream = OcrImageBytes(image);
            if (imageStream is null)
            {
                continue;
            }

            var text = await engine.RecognizeAsync(imageStream, _ocrOptions.Language, cancellationToken);
            if (!string.IsNullOrWhiteSpace(text))
            {
                recognized.Add(text);
            }
        }

        if (recognized.Count == 0)
        {
            return null;
        }

        var pageText = string.Join("\n\n", recognized);
        return new IngestionDocumentParagraph(pageText)
        {
            Text = pageText
        };
    }

    /// <summary>
    /// PNG when PdfPig can convert the image, otherwise the encoded image bytes: <c>TryGetPng</c> cannot
    /// convert JPEG, so a DCTDecode scan keeps its bytes and the OCR engine decodes JPEG itself.
    /// </summary>
    private static MemoryStream? OcrImageBytes(IPdfImage image)
    {
        if (image.TryGetPng(out var png) && png is not null)
        {
            return new MemoryStream(png);
        }

        return image.RawMemory.IsEmpty ? null : new MemoryStream(image.RawMemory.ToArray());
    }
}
