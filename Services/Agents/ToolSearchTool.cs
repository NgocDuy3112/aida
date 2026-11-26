using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace aida.Services.Agents;

/// <summary>One tool the agent can call, described for the tool_search catalog.</summary>
internal sealed record ToolCatalogEntry(string Name, string Domain, string Description, string Parameters);

/// <summary>
/// Meta-tool that lets the model look up which tools it has before calling one. The catalog is static
/// documentation of the tools registered for the agent — it never grants new capabilities.
/// </summary>
internal static class ToolSearchTool
{
    internal const string Name = "tool_search";

    internal const string Description =
        "Tra cứu danh mục tool của bạn khi chưa chắc tool nào làm được việc cần làm. " +
        "Truyền từ khoá (hoặc regex) và domain tuỳ chọn; kết quả là JSON các tool khớp kèm tham số và cách " +
        "dùng — gọi tool đó ngay trong lượt này theo schema trả về.";

    internal static readonly IReadOnlyList<ToolCatalogEntry> AllEntries =
    [
        new("read_document", "documents",
            "Read the full text of a stored document (uploaded by the user or saved earlier).",
            "\"filename\": string — bare file name, e.g. \"report.docx\""),
        new("save_document", "documents",
            "Create or replace a downloadable document (Word or PDF).",
            "\"filename\": string, \"format\": \"docx\"|\"pdf\", \"content\": string (complete Markdown)"),
        new("draft_document", "writer",
            "Draft a complete long document in Markdown from a detailed spec; returns the draft only.",
            "\"spec\": string (title, purpose, audience, required sections, length)"),
        new("parallel_research", "research",
            "Run up to 3 independent research sub-tasks concurrently and merge their findings.",
            "\"tasks\": string[] (1-3 independent task descriptions)"),
        new("web_search", "web",
            "Search the public web for current external information.",
            "\"query\": string"),
        new(AskUserTool.Name, "meta",
            "Ask the user one clarifying question; the answer comes back inside the same turn.",
            "\"question\": string, \"options\": string[] (1-4, optional), \"allow_free_text\": boolean (optional, default true)"),
        new(ToolSearchTool.Name, "meta",
            "Search this catalog of tools.",
            "\"query\": string, \"domain\": \"documents\"|\"research\"|\"web\"|\"writer\"|\"meta\"|\"all\" (optional)"),
    ];

    /// <summary>
    /// The tools the agent actually has. <c>web_search</c> is only registered when a web search key is
    /// configured, so it must not be advertised otherwise.
    /// </summary>
    internal static IReadOnlyList<ToolCatalogEntry> Catalog(bool includeWebSearch) =>
        includeWebSearch
            ? AllEntries
            : [.. AllEntries.Where(entry => entry.Name != WebSearchName)];

    private const string WebSearchName = "web_search";

    /// <summary>
    /// Tools whose name, domain, description or parameters match <paramref name="query"/> as a
    /// case-insensitive regular expression (literal keyword when it is not a valid pattern).
    /// </summary>
    internal static IReadOnlyList<ToolCatalogEntry> Search(string? query, string? domain, bool includeWebSearch = true)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var catalog = Catalog(includeWebSearch);
        var domainFilter = NormalizeDomain(domain, catalog);
        var filtered = domainFilter is null
            ? catalog
            : catalog.Where(entry => entry.Domain == domainFilter).ToList();

        return filtered.Where(entry => Matches(Haystack(entry), query)).ToList();
    }

    /// <summary>JSON array of the matching tools; an empty array when nothing matches.</summary>
    internal static string SearchJson(string? query, string? domain, bool includeWebSearch = true) =>
        JsonSerializer.Serialize(Search(query, domain, includeWebSearch).Select(entry => new
        {
            name = entry.Name,
            domain = entry.Domain,
            description = entry.Description,
            parameters = entry.Parameters,
        }));

    internal static AIFunction Create(bool includeWebSearch)
    {
        // domain has a default so the generated schema keeps it optional.
        Task<string> SearchAsync(string query, string? domain = null, CancellationToken ct = default) =>
            Task.FromResult(SearchJson(query, domain, includeWebSearch));

        return AIFunctionFactory.Create(SearchAsync, Name, Description, null);
    }

    private static string Haystack(ToolCatalogEntry entry) =>
        $"{entry.Name}\n{entry.Domain}\n{entry.Description}\n{entry.Parameters}";

    /// <summary>
    /// Null means "search every domain". Unknown domains are ignored rather than returning nothing,
    /// so a misspelt filter cannot hide a tool that exists.
    /// </summary>
    private static string? NormalizeDomain(string? domain, IReadOnlyList<ToolCatalogEntry> catalog)
    {
        var value = domain?.Trim().ToLowerInvariant();
        if (value is null or "" or "all") return null;
        return catalog.Any(entry => entry.Domain == value) ? value : null;
    }

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    private const RegexOptions MatchOptions =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Matches <paramref name="haystack"/> against <paramref name="query"/>; the timeout is a
    /// parameter so the catastrophic-backtracking fallback can be tested.</summary>
    internal static bool Matches(string haystack, string query, TimeSpan? timeout = null)
    {
        var pattern = query.Trim();
        try
        {
            return Regex.IsMatch(haystack, pattern, MatchOptions, timeout ?? RegexTimeout);
        }
        catch (ArgumentException)
        {
            // Not a valid pattern — fall back to a plain keyword search.
            return haystack.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
