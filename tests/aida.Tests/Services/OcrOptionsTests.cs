using aida.Services.Ingestion;
using Microsoft.Extensions.Configuration;

namespace aida.Tests.Services;

public class OcrOptionsTests
{
    [Fact]
    public void Defaults_when_configuration_is_missing()
    {
        var options = OcrOptions.FromConfiguration(null);

        Assert.True(options.Enabled);
        Assert.Equal("vie+eng", options.Language);
        Assert.Equal(20, options.MinLettersPerPage);
        Assert.Null(options.TessDataPath);
    }

    [Fact]
    public void Defaults_when_section_is_absent()
    {
        var configuration = new ConfigurationBuilder().Build();

        var options = OcrOptions.FromConfiguration(configuration);

        Assert.True(options.Enabled);
        Assert.Equal("vie+eng", options.Language);
        Assert.Equal(20, options.MinLettersPerPage);
        Assert.Null(options.TessDataPath);
    }

    [Fact]
    public void Reads_values_from_ocr_section()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ocr:Enabled"] = "false",
                ["Ocr:Language"] = "eng",
                ["Ocr:MinLettersPerPage"] = "50",
                ["Ocr:TessDataPath"] = "/opt/tessdata",
            })
            .Build();

        var options = OcrOptions.FromConfiguration(configuration);

        Assert.False(options.Enabled);
        Assert.Equal("eng", options.Language);
        Assert.Equal(50, options.MinLettersPerPage);
        Assert.Equal("/opt/tessdata", options.TessDataPath);
    }

    [Fact]
    public void Empty_language_falls_back_to_default()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ocr:Language"] = "",
                ["Ocr:TessDataPath"] = "",
            })
            .Build();

        var options = OcrOptions.FromConfiguration(configuration);

        Assert.Equal("vie+eng", options.Language);
        Assert.Null(options.TessDataPath);
    }
}
