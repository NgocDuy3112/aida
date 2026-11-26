using System.Text.Json;
using aida.Components.Pages.Chat;

namespace aida.Tests.Services;

public class AskUserArgumentsTests
{
    private static Dictionary<string, object?> Arguments(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!;

    [Fact]
    public void Question_is_read_from_a_plain_string_argument()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["question"] = "Bạn muốn tóm tắt hay liệt kê?",
        };

        Assert.Equal("Bạn muốn tóm tắt hay liệt kê?", AskUserArguments.Question(arguments));
    }

    [Fact]
    public void Question_is_read_from_a_json_element_argument()
    {
        var arguments = Arguments("""{"question":"Chọn cách trình bày"}""");

        Assert.Equal("Chọn cách trình bày", AskUserArguments.Question(arguments));
    }

    [Fact]
    public void Missing_question_reads_as_null()
        => Assert.Null(AskUserArguments.Question(Arguments("""{"options":["a"]}""")));

    [Fact]
    public void Options_are_read_from_a_json_array()
    {
        var arguments = Arguments("""{"options":["Tóm tắt","Chi tiết"]}""");

        Assert.Equal(["Tóm tắt", "Chi tiết"], AskUserArguments.Options(arguments));
    }

    [Fact]
    public void Options_are_read_from_a_typed_list()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["options"] = new List<string> { "Ngắn", "Dài" },
        };

        Assert.Equal(["Ngắn", "Dài"], AskUserArguments.Options(arguments));
    }

    [Fact]
    public void Missing_options_read_as_empty()
        => Assert.Empty(AskUserArguments.Options(Arguments("""{"question":"Hỏi gì đó"}""")));

    [Fact]
    public void Non_array_options_read_as_empty()
        => Assert.Empty(AskUserArguments.Options(Arguments("""{"options":"một lựa chọn"}""")));
}

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
