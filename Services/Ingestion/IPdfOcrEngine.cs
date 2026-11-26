namespace aida.Services.Ingestion;

/// <summary>Recognizes the text of a single page image extracted from a PDF.</summary>
internal interface IPdfOcrEngine
{
    Task<string> RecognizeAsync(Stream image, string language, CancellationToken cancellationToken);
}
