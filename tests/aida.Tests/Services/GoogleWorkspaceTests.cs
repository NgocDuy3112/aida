using System.Net;
using System.Text;
using aida.Data;
using aida.Services.Documents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace aida.Tests.Services;

public class GoogleWorkspaceTests
{
    private sealed class ScriptedHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public List<(string Method, string Url, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method.Method, request.RequestUri!.ToString(), body));
            return respond(request, body);
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static GoogleWorkspaceService Service(HttpMessageHandler handler)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Google:ClientId"] = "cid",
            ["Authentication:Google:ClientSecret"] = "sec",
        }).Build();

        return new GoogleWorkspaceService(
            new SingleClientFactory(handler), config, NullLogger<GoogleWorkspaceService>.Instance);
    }

    [Fact]
    public void Html_renders_headings_inline_and_escaping()
    {
        var html = GoogleHtml.FromMarkdown(
            "# Tiêu đề\nĐoạn **mạnh** và *nghiêng* và `mã` hơn cả <bút>");

        Assert.Contains("<h1>Tiêu đề</h1>", html);
        Assert.Contains("<b>mạnh</b>", html);
        Assert.Contains("<i>nghiêng</i>", html);
        Assert.Contains("<code>mã</code>", html);
        Assert.Contains("&lt;bút&gt;", html);      
        Assert.DoesNotContain("<bút>", html);
    }

    [Fact]
    public void Html_groups_consecutive_bullets_into_one_list()
    {
        var html = GoogleHtml.FromMarkdown("trước\n- một\n- hai\n\nsau");

        Assert.Contains("<ul><li>một</li><li>hai</li></ul>", html);
        Assert.Contains("<p>trước</p>", html);
        Assert.Contains("<p>sau</p>", html);
    }

    [Fact]
    public void Html_renders_table_with_header_and_keeps_document_order()
    {
        var html = GoogleHtml.FromMarkdown(
            "giới thiệu\n\n| A | B |\n|---|---|\n| 1 | 2 |\n\nkết thúc");

        Assert.Contains(
            "<table><tr><th>A</th><th>B</th></tr><tr><td>1</td><td>2</td></tr></table>", html);
        Assert.True(html.IndexOf("giới thiệu", StringComparison.Ordinal)
                    < html.IndexOf("<table>", StringComparison.Ordinal));
        Assert.True(html.IndexOf("</table>", StringComparison.Ordinal)
                    < html.IndexOf("kết thúc", StringComparison.Ordinal));
    }

    [Fact]
    public void Html_table_without_separator_has_no_header_row()
    {
        var html = GoogleHtml.FromMarkdown("| a |\n| b |");

        Assert.DoesNotContain("<th>", html);
        Assert.Contains("<td>a</td>", html);
        Assert.Contains("<td>b</td>", html);
    }

    [Fact]
    public void Html_code_fence_becomes_plain_paragraphs_without_fence_markers()
    {
        var html = GoogleHtml.FromMarkdown("```csharp\nvar x = 1 < 2;\n```");

        Assert.DoesNotContain("```", html);
        Assert.Contains("<p>var x = 1 &lt; 2;</p>", html);
    }

    [Fact]
    public void Sheets_rows_table_cells_and_plain_lines_in_order()
    {
        var rows = GoogleSheetValues.FromMarkdown(
            "# Doanh thu\n| **Tháng** | Số liệu |\n|---|---|\n| T1 | 10 |\n\nghi chú");

        Assert.Equal(new[]
        {
            new[] { "Doanh thu" },
            new[] { "Tháng", "Số liệu" },   
            new[] { "T1", "10" },
            new[] { "ghi chú" },
        }, rows);
    }

    [Fact]
    public void Sheets_rows_never_contain_fence_markers()
    {
        var rows = GoogleSheetValues.FromMarkdown("```\ncode\n```");

        Assert.All(rows, r => Assert.DoesNotContain("```", r[0]));
    }

    [Theory]
    [InlineData("report", "gdoc", "report.gdoc")]
    [InlineData("report", "gsheet", "report.gsheet")]
    [InlineData("report.pdf", "GDOC", "report.gdoc")]   
    public void NormalizeFileName_accepts_google_formats(string name, string format, string expected)
        => Assert.Equal(expected, ChatDocumentTools.NormalizeFileName(name, format));

    [Fact]
    public async Task Save_gdoc_without_service_returns_clear_error()
    {
        var msg = await ChatDocumentTools.SaveAsync(
            "user-1", null!, google: null, users: null, "bao-cao", "gdoc", "nội dung");

        Assert.Contains("not configured", msg, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Error:", msg);
    }

    [Fact]
    public async Task Save_rejects_unknown_format_with_all_four_names()
    {
        var msg = await ChatDocumentTools.SaveAsync(
            "user-1", null!, null, null, "x", "txt", "nội dung");

        Assert.Contains("docx", msg);
        Assert.Contains("gdoc", msg);
        Assert.Contains("gsheet", msg);
    }

    [Fact]
    public async Task CreateDoc_without_refresh_token_fails_with_signin_hint()
    {
        var svc = Service(new ScriptedHandler((_, _) => Json("{}")));

        var result = await svc.CreateDocAsync(null, "Báo cáo", "# nội dung");

        Assert.Null(result.Url);
        Assert.Contains("sign in with Google again", result.Error);
    }

    [Fact]
    public async Task CreateDoc_refreshes_token_then_uploads_html_as_native_doc()
    {
        var handler = new ScriptedHandler((req, _) =>
            req.RequestUri!.Host == "oauth2.googleapis.com"
                ? Json("""{"access_token":"tok-1","expires_in":3599}""")
                : Json("""{"id":"abc123","name":"Báo cáo"}"""));
        var svc = Service(handler);

        var result = await svc.CreateDocAsync("refresh-rt", "Báo cáo", "# Tiêu đề\n- một");

        Assert.Equal("https://docs.google.com/document/d/abc123/edit", result.Url);
        Assert.Null(result.Error);

        var (token, upload) = (handler.Requests[0], handler.Requests[1]);
        Assert.Contains("grant_type=refresh_token", token.Body);
        Assert.Contains("refresh-rt", token.Body);
        Assert.Equal("POST", upload.Method);
        Assert.Contains("uploadType=multipart", upload.Url);
        Assert.Contains("application/vnd.google-apps.document", upload.Body);  
        Assert.Contains("<h1>Tiêu đề</h1>", upload.Body);
    }

    [Fact]
    public async Task CreateDoc_upload_failure_surfaces_status_and_body()
    {
        var handler = new ScriptedHandler((req, _) =>
            req.RequestUri!.Host == "oauth2.googleapis.com"
                ? Json("""{"access_token":"tok"}""")
                : Json("""{"error":{"message":"The user does not have permission"}}""",
                    HttpStatusCode.Forbidden));
        var svc = Service(handler);

        var result = await svc.CreateDocAsync("rt", "t", "nội dung");

        Assert.Null(result.Url);
        Assert.Contains("403", result.Error);
        Assert.Contains("permission", result.Error);
    }

    [Fact]
    public async Task Invalid_refresh_token_reports_signin_instead_of_raw_failure()
    {
        var handler = new ScriptedHandler((_, _) =>
            Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest));
        var svc = Service(handler);

        var result = await svc.CreateDocAsync("expired", "t", "nội dung");

        Assert.Null(result.Url);
        Assert.Contains("sign in with Google again", result.Error);
        Assert.Single(handler.Requests);   
    }

    [Fact]
    public async Task CreateSheet_creates_then_fills_with_raw_values()
    {
        string? created = null;
        var handler = new ScriptedHandler((req, body) =>
        {
            if (req.RequestUri!.Host == "oauth2.googleapis.com")
                return Json("""{"access_token":"tok"}""");
            if (req.Method == HttpMethod.Post)
            {
                created = body;
                return Json("""{"spreadsheetId":"sheet-9"}""");
            }
            Assert.Contains("valueInputOption=RAW", req.RequestUri.ToString());
            Assert.Contains("""[["Tháng","Số liệu"],["T1","10"]]""", body);
            return Json("{}");
        });
        var svc = Service(handler);

        var result = await svc.CreateSheetAsync(
            "rt", "Doanh thu", "| Tháng | Số liệu |\n|---|---|\n| T1 | 10 |");

        Assert.Equal("https://docs.google.com/spreadsheets/d/sheet-9/edit", result.Url);
        Assert.Contains("Doanh thu", created);
        Assert.Equal(3, handler.Requests.Count);   
    }

    [Fact]
    public async Task CreateSheet_markdown_without_table_still_creates_single_column()
    {
        var handler = new ScriptedHandler((req, body) =>
            req.RequestUri!.Host == "oauth2.googleapis.com"
                ? Json("""{"access_token":"tok"}""")
                : req.Method == HttpMethod.Post
                    ? Json("""{"spreadsheetId":"s2"}""")
                    : Json("{}"));
        var svc = Service(handler);

        var result = await svc.CreateSheetAsync("rt", "Ghi chú", "# Việc cần làm\n- mua sữa");

        Assert.NotNull(result.Url);
        var fill = handler.Requests[2];
        Assert.Contains("""[["Việc cần làm"]""", fill.Body);
        Assert.Contains("mua sữa", fill.Body);
    }
}
