using System.Text.Json;
using aida.Services.Agents;

namespace aida.Tests.Services;

public class ToolSearchToolTests
{
    private static string[] Names(string? query, string? domain = null) =>
        [.. ToolSearchTool.Search(query, domain).Select(entry => entry.Name)];

    [Fact]
    public void Catalog_covers_every_tool_the_agent_has()
    {
        Assert.Equal(
            ["ask_user", "draft_document", "parallel_research", "read_document", "save_document", "tool_search", "web_search"],
            ToolSearchTool.Catalog(includeWebSearch: true).Select(entry => entry.Name).Order());
        Assert.All(ToolSearchTool.Catalog(includeWebSearch: true), entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Description));
            Assert.False(string.IsNullOrWhiteSpace(entry.Parameters));
            Assert.False(string.IsNullOrWhiteSpace(entry.Domain));
        });
    }

    [Fact]
    public void Catalog_omits_web_search_when_the_tool_is_not_registered()
    {
        var names = ToolSearchTool.Catalog(includeWebSearch: false).Select(entry => entry.Name).ToList();

        Assert.DoesNotContain("web_search", names);
        Assert.Contains("tool_search", names);
        Assert.Empty(ToolSearchTool.Search("public web", null, includeWebSearch: false));
        Assert.DoesNotContain("web_search", ToolSearchTool.SearchJson("web", null, includeWebSearch: false));
        Assert.Contains("web_search", ToolSearchTool.SearchJson("web", null, includeWebSearch: true));
    }

    [Fact]
    public void A_catastrophic_pattern_times_out_instead_of_hanging()
    {
        var haystack = new string('a', 40) + "!";

        Assert.False(ToolSearchTool.Matches(haystack, "(a+)+$", TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public void Query_matches_names_and_descriptions()
    {
        Assert.Equal(
            ["draft_document", "read_document", "save_document", "tool_search"],
            Names("document").Order());

        var toolSearch = Assert.Single(ToolSearchTool.Search("catalog", null));
        Assert.Equal("tool_search", toolSearch.Name);
    }

    [Fact]
    public void Query_is_a_case_insensitive_regex()
    {
        Assert.Equal(["save_document"], Names("SAVE_DO.*"));
        Assert.Equal(["parallel_research", "tool_search", "web_search"], Names("search").Order());
    }

    [Fact]
    public void Domain_filter_narrows_the_results()
    {
        Assert.Equal(["draft_document"], Names("document", "writer"));
        Assert.Equal(["read_document", "save_document"], Names("document", "documents"));
        Assert.Equal([], Names("document", "web"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("all")]
    public void Missing_or_all_domain_searches_the_whole_catalog(string? domain)
    {
        Assert.Equal(Names("document"), Names("document", domain));
    }

    [Fact]
    public void Unknown_domain_falls_back_to_the_whole_catalog()
    {
        Assert.Equal(Names("document"), Names("document", "unknown-domain"));
    }

    [Fact]
    public void No_match_returns_an_empty_list_and_empty_json()
    {
        Assert.Empty(ToolSearchTool.Search("không-có-tool-nào-như-vậy", null));
        Assert.Equal("[]", ToolSearchTool.SearchJson("không-có-tool-nào-như-vậy", null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_query_returns_nothing(string? query)
    {
        Assert.Empty(ToolSearchTool.Search(query, null));
    }

    [Fact]
    public void An_invalid_regex_is_treated_as_a_literal_keyword()
    {
        Assert.Empty(ToolSearchTool.Search("(unclosed", null));
        Assert.Empty(ToolSearchTool.Search("[a-", null));
    }

    [Fact]
    public void Search_json_carries_the_parameters_needed_to_call_the_tool()
    {
        var json = ToolSearchTool.SearchJson("draft", null);

        using var document = JsonDocument.Parse(json);
        var entry = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("draft_document", entry.GetProperty("name").GetString());
        Assert.Equal("writer", entry.GetProperty("domain").GetString());
        Assert.Contains("spec", entry.GetProperty("parameters").GetString());
        Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("description").GetString()));
    }

    [Fact]
    public void Tool_declaration_is_named_tool_search()
    {
        var tool = ToolSearchTool.Create(includeWebSearch: true);

        Assert.Equal("tool_search", tool.Name);
        Assert.False(string.IsNullOrWhiteSpace(tool.Description));
        var schema = tool.JsonSchema;
        Assert.Equal("string", schema.GetProperty("properties")
            .GetProperty("query").GetProperty("type").GetString());
        // Only the query is required; the domain filter stays optional.
        Assert.Equal(["query"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }
}
