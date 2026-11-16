using System.ClientModel;
using aida.Data;
using aida.Services.Agents;
using aida.Services.Usage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;

namespace aida.Tests.Services;

public class CrewOptionsTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values!).Build();

    private static readonly Dictionary<string, string?> Empty = new();

    [Fact]
    public void Crew_section_reads_all_role_models()
    {
        var config = Config(new()
        {
            ["Crew:ResearcherModel"] = "openai/gpt-5-nano",
            ["Crew:DirectorModel"] = "anthropic/claude-sonnet-5.5",
        });

        Assert.Equal("anthropic/claude-sonnet-5.5", CrewOptions.DirectorModel(config));
        Assert.Equal("openai/gpt-5-nano", CrewOptions.ResearcherModel(config));
    }

    [Fact]
    public void Missing_crew_config_fails_fast_with_clear_message()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CrewOptions.DirectorModel(Config(Empty)));

        Assert.Contains("Crew:DirectorModel", ex.Message);
    }

    [Fact]
    public void Blank_crew_value_fails_instead_of_falling_back()
    {
        var config = Config(new()
        {
            ["Crew:DirectorModel"] = "   ",
            ["Models:StrongModel"] = "custom/strong",
        });

        var ex = Assert.Throws<InvalidOperationException>(
            () => CrewOptions.DirectorModel(config));

        Assert.Contains("Crew:DirectorModel", ex.Message);
    }

    [Theory]
    [InlineData("high", "high")]
    [InlineData(" Medium ", "medium")]
    [InlineData("EXTRAHIGH", "extrahigh")]
    [InlineData("nonsense", "off")]
    [InlineData(null, "off")]
    public void Thinking_config_is_normalized(string? raw, string expected)
    {
        var values = new Dictionary<string, string?>();
        if (raw is not null) values["Crew:DirectorThinking"] = raw;

        Assert.Equal(expected, CrewOptions.Thinking(Config(values), "Director"));
    }

    [Theory]
    [InlineData("off")]
    [InlineData(null)]
    [InlineData("garbage")]
    public void Thinking_off_sends_no_reasoning_options(string? level)
        => Assert.Null(CrewOptions.Reasoning(level));

    [Theory]
    [InlineData("low", ReasoningEffort.Low)]
    [InlineData("medium", ReasoningEffort.Medium)]
    [InlineData("high", ReasoningEffort.High)]
    [InlineData("extrahigh", ReasoningEffort.ExtraHigh)]
    public void Thinking_levels_map_to_reasoning_effort(string level, ReasoningEffort expected)
        => Assert.Equal(expected, CrewOptions.Reasoning(level)?.Effort);

    [Fact]
    public void FromConfig_reads_all_six_values()
    {
        var config = Config(new()
        {
            ["Crew:DirectorModel"] = "dir-model",
            ["Crew:ResearcherModel"] = "res-model",
            ["Crew:WriterModel"] = "wr-model",
            ["Crew:DirectorThinking"] = "high",
            ["Crew:ResearcherThinking"] = "off",
            ["Crew:WriterThinking"] = "low",
        });

        var crew = CrewState.FromConfig(config);

        Assert.Equal(new CrewState("dir-model", "res-model", "wr-model",
            "high", "off", "low"), crew);
        Assert.Equal(new CrewState("dir-model", "res-model", "wr-model",
            "high", "off", "low"), crew);   
    }
}

public class CrewWriterToolTests
{
    private static CrewWriterTool Tool()
    {
        var client = new OpenAIClient(
            new ApiKeyCredential("test-key"),
            new OpenAIClientOptions { Endpoint = new Uri("https://example.invalid/") });
        return new CrewWriterTool(
            client, new NoopTracker(), new PricingTable());
    }

    private sealed class NoopTracker : IUsageTracker
    {
        public Task RecordAsync(UsageEvent evt, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    [Fact]
    public void Create_exposes_the_draft_document_tool()
    {
        var tool = Tool().Create("user-1", "google/gemini-3.1-flash-lite", "off");

        Assert.Equal("draft_document", tool.Name);
        Assert.Contains("save_document", tool.Description);
    }

    [Fact]
    public async Task Draft_without_spec_returns_clear_error_without_network()
    {
        var draft = await Tool().DraftAsync(
            "user-1", "google/gemini-3.1-flash-lite", "off", "   ", CancellationToken.None);

        Assert.StartsWith("Error:", draft);
        Assert.Contains("spec", draft);
    }

    [Fact]
    public void Writer_instructions_demand_verbatim_markdown_document()
    {
        Assert.Contains("Markdown", CrewWriterTool.WriterInstructions);
        Assert.Contains("Vietnamese", CrewWriterTool.WriterInstructions);
    }
}
