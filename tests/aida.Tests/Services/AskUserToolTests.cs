using System.Text.Json;
using aida.Services.Agents;
using Microsoft.Extensions.AI;

namespace aida.Tests.Services;

public class AskUserToolTests
{
    private static AIFunctionArguments Arguments(Dictionary<string, object?> values) => new(values);

    [Fact]
    public async Task Invoke_returns_the_answer_that_clears_the_broker()
    {
        var broker = new AskUserBroker();
        var tool = AskUserTool.Create(broker);

        broker.Changed += question =>
        {
            if (question is not null) broker.TryAnswer("Tóm tắt");
        };

        var result = await tool.InvokeAsync(
            Arguments(new() { ["question"] = "Bạn muốn gì?", ["options"] = new[] { "Tóm tắt" } }));

        Assert.Equal("Tóm tắt", result);
    }

    [Fact]
    public async Task Invoke_returns_the_declined_result_when_the_user_skips()
    {
        var broker = new AskUserBroker();
        var tool = AskUserTool.Create(broker);

        broker.Changed += question =>
        {
            if (question is not null) broker.TryDecline();
        };

        var result = await tool.InvokeAsync(Arguments(new() { ["question"] = "Bạn muốn gì?" }));

        Assert.Equal(AskUserBroker.DeclinedAnswer, result);
    }

    [Fact]
    public async Task Invoke_without_a_question_never_waits_on_the_user()
    {
        var broker = new AskUserBroker();
        var tool = AskUserTool.Create(broker);

        var result = await tool.InvokeAsync(Arguments(new() { ["options"] = new[] { "a", "b" } }));

        Assert.Equal(AskUserTool.MissingQuestionResult, result);
        Assert.Null(broker.Pending);
    }

    [Fact]
    public async Task Invoke_reads_json_element_arguments_and_keeps_at_most_four_options()
    {
        var broker = new AskUserBroker();
        var tool = AskUserTool.Create(broker);
        AskUserQuestion? asked = null;
        broker.Changed += question =>
        {
            if (question is null) return;
            asked = question;
            broker.TryAnswer("x");
        };

        var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>("""
            {"question":"Chọn đi","options":["a","b","c","d","e"],"allow_free_text":false}
            """)!;

        await tool.InvokeAsync(Arguments(arguments));

        Assert.Equal("Chọn đi", asked?.Question);
        Assert.Equal(["a", "b", "c", "d"], asked?.Options);
        Assert.False(asked?.AllowFreeText);
    }

    [Fact]
    public async Task Allow_free_text_defaults_to_true()
    {
        var broker = new AskUserBroker();
        var tool = AskUserTool.Create(broker);
        AskUserQuestion? asked = null;
        broker.Changed += question =>
        {
            if (question is null) return;
            asked = question;
            broker.TryAnswer("x");
        };

        await tool.InvokeAsync(Arguments(new() { ["question"] = "Bạn muốn gì?" }));

        Assert.True(asked?.AllowFreeText);
        Assert.Empty(asked!.Options);
    }

    [Fact]
    public void Tool_name_and_description_are_stable()
    {
        var tool = AskUserTool.Create(new AskUserBroker());

        Assert.Equal("ask_user", tool.Name);
        Assert.False(string.IsNullOrWhiteSpace(tool.Description));
    }

    [Fact]
    public void Schema_requires_only_the_question_and_makes_options_optional()
    {
        var schema = AskUserTool.Create(new AskUserBroker()).JsonSchema;
        var root = schema.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);

        Assert.Equal("object", root["type"].GetString());
        Assert.Equal(["question"], root["required"].EnumerateArray().Select(e => e.GetString()));

        var properties = root["properties"].EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        Assert.Equal(["allow_free_text", "options", "question"], properties.Keys.Order());

        var options = properties["options"];
        Assert.Equal("array", options.GetProperty("type").GetString());
        Assert.Equal(4, options.GetProperty("maxItems").GetInt32());
        Assert.False(options.TryGetProperty("minItems", out _));

        Assert.Equal("boolean", properties["allow_free_text"].GetProperty("type").GetString());
    }

    [Fact]
    public void Instructions_allow_more_than_one_question_per_turn()
    {
        Assert.DoesNotContain("tối đa một lần", AskUserTool.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nhiều câu", AskUserTool.Instructions, StringComparison.OrdinalIgnoreCase);
    }
}
