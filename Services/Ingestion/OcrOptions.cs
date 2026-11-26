namespace aida.Services.Ingestion;

internal sealed class OcrOptions
{
    public const string SectionName = "Ocr";
    public const string DefaultLanguage = "vie+eng";
    private const int DefaultMinLettersPerPage = 20;

    public bool Enabled { get; init; } = true;

    public string Language { get; init; } = DefaultLanguage;

    /// <summary>Pages with fewer extracted letters than this are treated as scanned images.</summary>
    public int MinLettersPerPage { get; init; } = DefaultMinLettersPerPage;

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
            TessDataPath = section.GetValue<string?>("TessDataPath") is { Length: > 0 } tessDataPath
                ? tessDataPath
                : null,
        };
    }
}
