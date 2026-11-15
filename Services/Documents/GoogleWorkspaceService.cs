using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace aida.Services.Documents;

internal sealed record GoogleSaveResult(string? Url, string? Error)
{
    internal static GoogleSaveResult Ok(string url) => new(url, null);
    internal static GoogleSaveResult Fail(string error) => new(null, error);
}

internal sealed class GoogleWorkspaceService(
    IHttpClientFactory httpClientFactory,
    IConfiguration config,
    ILogger<GoogleWorkspaceService> logger)
{
    internal const string HttpClientName = "google-workspace";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private const string TokenUrl = "https://oauth2.googleapis.com/token";
    private const string DriveUploadUrl =
        "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&fields=id";
    private const string SheetsUrl = "https://sheets.googleapis.com/v4/spreadsheets";

    internal async Task<GoogleSaveResult> CreateDocAsync(
        string? refreshToken, string title, string markdown)
    {
        var access = await GetAccessTokenAsync(refreshToken);
        if (access is null) return NotAuthorized();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, DriveUploadUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            request.Content = BuildMultipart(title, GoogleHtml.FromMarkdown(markdown));

            using var response = await httpClientFactory.CreateClient(HttpClientName)
                .SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return GoogleSaveResult.Fail(
                    $"Google Drive upload failed ({(int)response.StatusCode}): {Truncate(body)}");

            using var doc = JsonDocument.Parse(body);
            var id = doc.RootElement.GetProperty("id").GetString();
            return string.IsNullOrEmpty(id)
                ? GoogleSaveResult.Fail("Google Drive upload returned no file id.")
                : GoogleSaveResult.Ok($"https://docs.google.com/document/d/{id}/edit");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Google Doc creation failed for '{Title}'", title);
            return GoogleSaveResult.Fail($"could not create the Google Doc: {ex.Message}");
        }
    }

    internal async Task<GoogleSaveResult> CreateSheetAsync(
        string? refreshToken, string title, string markdown)
    {
        var access = await GetAccessTokenAsync(refreshToken);
        if (access is null) return NotAuthorized();

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);

            using (var create = new HttpRequestMessage(HttpMethod.Post, SheetsUrl))
            {
                create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
                create.Content = JsonContent(
                    new { properties = new { title } });

                using var created = await client.SendAsync(create);
                var body = await created.Content.ReadAsStringAsync();
                if (!created.IsSuccessStatusCode)
                    return GoogleSaveResult.Fail(
                        $"Google Sheet creation failed ({(int)created.StatusCode}): {Truncate(body)}");

                using var doc = JsonDocument.Parse(body);
                var id = doc.RootElement.GetProperty("spreadsheetId").GetString();
                if (string.IsNullOrEmpty(id))
                    return GoogleSaveResult.Fail("Google Sheet creation returned no spreadsheet id.");

                var rows = GoogleSheetValues.FromMarkdown(markdown);
                if (rows.Length > 0)
                {
                    using var put = new HttpRequestMessage(HttpMethod.Put,
                        $"{SheetsUrl}/{id}/values/Sheet1?valueInputOption=RAW");
                    put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
                    put.Content = JsonContent(new
                    {
                        range = "Sheet1",
                        majorDimension = "ROWS",
                        values = rows,
                    });

                    using var filled = await client.SendAsync(put);
                    if (!filled.IsSuccessStatusCode)
                    {
                        var fillBody = await filled.Content.ReadAsStringAsync();
                        logger.LogWarning("Sheet created but its cells could not be filled " +
                            "({Status}): {Body} — sheet id {Id}",
                            (int)filled.StatusCode, fillBody, id);
                    }
                }

                return GoogleSaveResult.Ok($"https://docs.google.com/spreadsheets/d/{id}/edit");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Google Sheet creation failed for '{Title}'", title);
            return GoogleSaveResult.Fail($"could not create the Google Sheet: {ex.Message}");
        }
    }

    internal async Task<string?> GetAccessTokenAsync(string? refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return null;

        var clientId = config["Authentication:Google:ClientId"];
        var secret = config["Authentication:Google:ClientSecret"];
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(secret))
            return null;

        try
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = secret,
                ["refresh_token"] = refreshToken,
                ["grant_type"] = "refresh_token",
            });
            using var response = await httpClientFactory.CreateClient(HttpClientName)
                .PostAsync(TokenUrl, form);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Google token refresh failed ({Status}): {Body}",
                    (int)response.StatusCode, Truncate(body));
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("access_token", out var token)
                ? token.GetString()
                : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Google token refresh threw");
            return null;
        }
    }

    private static GoogleSaveResult NotAuthorized() => GoogleSaveResult.Fail(
        "Google access is unavailable — the user has no saved Google authorization " +
        "or it expired; ask them to sign in with Google again, then retry.");

    private static MultipartContent BuildMultipart(string title, string html)
    {
        var metadata = JsonSerializer.Serialize(new
        {
            name = title,
            mimeType = "application/vnd.google-apps.document",
        }, JsonOptions);
        var content = new MultipartContent("related");
        content.Add(new StringContent(metadata, Encoding.UTF8, "application/json"));
        content.Add(new StringContent(html, Encoding.UTF8, "text/html"));
        return content;
    }

    private static StringContent JsonContent(object payload) =>
        new(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

    private static string Truncate(string s) =>
        s.Length <= 300 ? s : s[..300] + "…";
}

internal static class GoogleHtml
{
    internal readonly record struct TableData(IReadOnlyList<string[]> Rows, bool HadHeader);

    internal static string FromMarkdown(string markdown)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><body>");

        foreach (var (isTable, lines) in SplitSegments(markdown))
        {
            if (isTable)
            {
                AppendTable(sb, ParseTableLines(lines));
                continue;
            }

            var blocks = MarkdownLite.Parse(string.Join("\n", lines));
            var i = 0;
            while (i < blocks.Count)
            {
                if (blocks[i] is MdBlock.Bullet)
                {
                    sb.Append("<ul>");
                    while (i < blocks.Count && blocks[i] is MdBlock.Bullet bullet)
                    {
                        sb.Append("<li>").Append(Inline(bullet.Text)).Append("</li>");
                        i++;
                    }
                    sb.Append("</ul>");
                }
                else
                {
                    AppendBlock(sb, blocks[i]);
                    i++;
                }
            }
        }

        sb.Append("</body></html>");
        return sb.ToString();
    }

    internal static List<(bool IsTable, List<string> Lines)> SplitSegments(string markdown)
    {
        var segments = new List<(bool, List<string>)>();
        var current = new List<string>();
        var currentIsTable = false;

        void Flush()
        {
            if (current.Count == 0) return;
            segments.Add((currentIsTable, current));
            current = new List<string>();
        }

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var isTableRow = raw.TrimStart().StartsWith("|", StringComparison.Ordinal);
            if (current.Count == 0)
            {
                currentIsTable = isTableRow;
                current.Add(raw);
                continue;
            }

            if (isTableRow != currentIsTable)
            {
                Flush();
                currentIsTable = isTableRow;
            }
            current.Add(raw);
        }
        Flush();

        return segments;
    }

    internal static TableData ParseTableLines(IReadOnlyList<string> lines)
    {
        var rows = new List<string[]>();
        var hadHeader = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("|", StringComparison.Ordinal)) continue;

            var cells = trimmed.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length == 1 && cells[0].Length == 0) continue;
            if (IsSeparator(cells))
            {
                if (rows.Count == 1) hadHeader = true;
                continue;
            }
            rows.Add(cells);
        }

        return new TableData(rows, hadHeader);
    }

    private static bool IsSeparator(string[] cells)
        => cells.Length > 0 && cells.All(c => c.Length > 0 && c.Trim(':', '-').Length == 0);

    private static void AppendTable(StringBuilder sb, TableData table)
    {
        if (table.Rows.Count == 0) return;

        sb.Append("<table>");
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var header = table.HadHeader && r == 0;
            sb.Append("<tr>");
            foreach (var cell in table.Rows[r])
            {
                sb.Append(header ? "<th>" : "<td>")
                  .Append(Inline(cell))
                  .Append(header ? "</th>" : "</td>");
            }
            sb.Append("</tr>");
        }
        sb.Append("</table>");
    }

    private static void AppendBlock(StringBuilder sb, MdBlock block)
    {
        switch (block)
        {
            case MdBlock.Heading h:
                var level = Math.Clamp(h.Level, 1, 4);
                sb.Append("<h").Append(level).Append('>')
                  .Append(Inline(h.Text))
                  .Append("</h").Append(level).Append('>');
                break;

            case MdBlock.Paragraph p:
                sb.Append("<p>").Append(Inline(p.Text)).Append("</p>");
                break;

            case MdBlock.Code c:
                foreach (var line in c.Text.Replace("\r\n", "\n").Split('\n'))
                {
                    if (line.Length == 0) continue;
                    sb.Append("<p>").Append(Escape(line)).Append("</p>");
                }
                break;
        }
    }

    internal static string Inline(string text)
    {
        var sb = new StringBuilder();
        foreach (var (part, bold, italic, code) in MarkdownLite.Inline(text))
        {
            var escaped = Escape(part);
            if (code) sb.Append("<code>").Append(escaped).Append("</code>");
            else if (bold) sb.Append("<b>").Append(escaped).Append("</b>");
            else if (italic) sb.Append("<i>").Append(escaped).Append("</i>");
            else sb.Append(escaped);
        }
        return sb.ToString();
    }

    internal static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}

internal static class GoogleSheetValues
{
    internal static string[][] FromMarkdown(string markdown)
    {
        var rows = new List<string[]>();

        foreach (var (isTable, lines) in GoogleHtml.SplitSegments(markdown))
        {
            if (isTable)
            {
                foreach (var row in GoogleHtml.ParseTableLines(lines).Rows)
                    rows.Add(row.Select(MarkdownLite.Plain).ToArray());
                continue;
            }

            foreach (var raw in lines)
            {
                var t = raw.Trim();
                if (t.Length == 0) continue;
                if (t.StartsWith("```", StringComparison.Ordinal)) continue; 
                if (t.StartsWith('#')) t = t.TrimStart('#').Trim();           
                if (t.Length == 0) continue;
                rows.Add(new[] { MarkdownLite.Plain(t) });
            }
        }

        return rows.ToArray();
    }
}
