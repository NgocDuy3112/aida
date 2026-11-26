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
            using var pdf = PdfDocument.Open(source);
            var document = new IngestionDocument(identifier);
            foreach (var page in pdf.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                document.Sections.Add(await GetPageSectionAsync(page, identifier, engine, cancellationToken));
            }
            return document;
        }
        finally
        {
            if (ownsEngine)
            {
                (engine as IDisposable)?.Dispose();
            }
        }
    }

    private async Task<IngestionDocumentSection> GetPageSectionAsync(
        Page pdfPage, string identifier, IPdfOcrEngine? engine, CancellationToken cancellationToken)
    {
        var section = new IngestionDocumentSection
        {
            PageNumber = pdfPage.Number,
        };

        if (engine is not null && pdfPage.Letters.Count < _ocrOptions.MinLettersPerPage)
        {
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
