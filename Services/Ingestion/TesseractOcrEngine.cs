using Tesseract;

namespace aida.Services.Ingestion;

/// <summary>Local Tesseract OCR engine used for pages that have no PDF text layer.</summary>
internal sealed class TesseractOcrEngine(OcrOptions options) : IPdfOcrEngine, IDisposable
{
    private static readonly string[] DefaultTessDataDirectories =
    [
        "/usr/share/tesseract-ocr/5/tessdata",
        "/usr/share/tesseract-ocr/4.00/tessdata",
        "/usr/share/tessdata",
        "/usr/local/share/tessdata",
        "/opt/homebrew/share/tessdata",
    ];

    private readonly Lock _gate = new();
    private TesseractEngine? _engine;
    private string? _engineLanguage;

    public Task<string> RecognizeAsync(Stream image, string language, CancellationToken cancellationToken)
    {
        // Tesseract cannot be interrupted inside Process(), so cancellation is honoured before the
        // synchronous work starts; PdfPigReader re-checks the token between pages and enforces the timeout.
        cancellationToken.ThrowIfCancellationRequested();

        using var buffer = new MemoryStream();
        image.CopyTo(buffer);
        var png = buffer.ToArray();

        return Task.Run(() => Recognize(png, language), cancellationToken);
    }

    private string Recognize(byte[] png, string language)
    {
        lock (_gate)
        {
            if (_engine is not null && _engineLanguage != language)
            {
                _engine.Dispose();
                _engine = null;
            }

            try
            {
                _engine ??= CreateEngine(language);
                _engineLanguage = language;
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to initialize Tesseract OCR for language '{language}': {ex.Message}", ex);
            }

            try
            {
                using var pix = Pix.LoadFromMemory(png);
                using var page = _engine.Process(pix);
                return page.GetText()?.Trim() ?? string.Empty;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Tesseract OCR failed for language '{language}': {ex.Message}", ex);
            }
        }
    }

    private TesseractEngine CreateEngine(string language)
    {
        var tessDataPath = ResolveTessDataPath()
            ?? throw new InvalidOperationException(
                "Tesseract tessdata directory not found. Install tesseract-ocr (or set Ocr:TessDataPath / TESSDATA_PREFIX).");

        return new TesseractEngine(tessDataPath, language, EngineMode.Default);
    }

    private string? ResolveTessDataPath()
    {
        if (!string.IsNullOrWhiteSpace(options.TessDataPath))
        {
            return options.TessDataPath;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable("TESSDATA_PREFIX");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && Directory.Exists(fromEnvironment))
        {
            return fromEnvironment;
        }

        return DefaultTessDataDirectories.FirstOrDefault(Directory.Exists);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _engine?.Dispose();
            _engine = null;
        }
    }
}
