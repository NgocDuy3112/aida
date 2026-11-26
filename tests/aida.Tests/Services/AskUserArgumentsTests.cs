using System.Text.Json;
using aida.Components.Pages.Chat;
using aida.Services.Agents;

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
