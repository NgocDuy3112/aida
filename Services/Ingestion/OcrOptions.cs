namespace aida.Services.Ingestion;

public sealed class OcrOptions
{
    public const string SectionName = "Ocr";
    public const string DefaultLanguage = "vie+eng";
    private const int DefaultMinLettersPerPage = 20;
    private const int DefaultMaxPages = 50;
    private const int DefaultOcrTimeoutSeconds = 120;

    public bool Enabled { get; init; } = true;

    public string Language { get; init; } = DefaultLanguage;

    /// <summary>Pages with fewer extracted letters than this are treated as scanned images.</summary>
    public int MinLettersPerPage { get; init; } = DefaultMinLettersPerPage;

    /// <summary>Larger PDFs are refused instead of spending OCR time on a document nobody waits for.</summary>
    public int MaxPages { get; init; } = DefaultMaxPages;

    /// <summary>Upper bound for reading one PDF; OCR that runs past it fails the document.</summary>
    public int OcrTimeoutSeconds { get; init; } = DefaultOcrTimeoutSeconds;

    /// <summary>Optional tessdata directory; when null the engine searches the platform defaults.</summary>
    public string? TessDataPath { get; init; }

    public static OcrOptions FromConfiguration(IConfiguration? configuration)
    {
        var section = configuration?.GetSection(SectionName);
        if (section is null || !section.Exists())
        {
            return new OcrOptions();
        }

        return new OcrOptions
        {
            Enabled = section.GetValue("Enabled", true),
            Language = section.GetValue<string?>("Language") is { Length: > 0 } language
                ? language
                : DefaultLanguage,
            MinLettersPerPage = section.GetValue("MinLettersPerPage", DefaultMinLettersPerPage),
            MaxPages = section.GetValue("MaxPages", DefaultMaxPages),
            OcrTimeoutSeconds = section.GetValue("OcrTimeoutSeconds", DefaultOcrTimeoutSeconds),
            TessDataPath = section.GetValue<string?>("TessDataPath") is { Length: > 0 } tessDataPath
                ? tessDataPath
                : null,
        };
    }
}
