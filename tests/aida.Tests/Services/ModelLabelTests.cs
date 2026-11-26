using aida.Components.Pages.Chat;

namespace aida.Tests.Services;

public class ModelLabelTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_model_has_no_label(string? model)
        => Assert.Null(ModelLabel.Short(model));

    [Theory]
    [InlineData("openai/gpt-5-nano", "gpt-5-nano")]
    [InlineData("anthropic/claude-sonnet-5.5", "claude-sonnet-5.5")]
    [InlineData("google/gemini-3.1-flash-lite", "gemini-3.1-flash-…")]
    [InlineData("  openai/gpt-5-mini  ", "gpt-5-mini")]
    public void Model_label_drops_the_vendor_prefix_and_truncates(string model, string expected)
        => Assert.Equal(expected, ModelLabel.Short(model));

    [Fact]
    public void Label_of_a_long_unprefixed_id_fits_the_header()
    {
        var label = ModelLabel.Short("some-really-long-model-name")!;

        Assert.Equal(18, label.Length);
        Assert.EndsWith("…", label);
    }

    [Fact]
    public void Trailing_slash_keeps_the_full_id()
        => Assert.Equal("openai/", ModelLabel.Short("openai/"));
}
