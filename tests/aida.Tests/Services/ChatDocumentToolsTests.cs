using aida.Services.Documents;

namespace aida.Tests.Services;

public class ChatDocumentToolsTests
{
    [Theory]
    [InlineData("report", "docx", "report.docx")]
    [InlineData("report", "pdf", "report.pdf")]
    [InlineData("report.docx", "PDF", "report.pdf")]
    public void NormalizeFileName_accepts_local_formats(string name, string format, string expected)
        => Assert.Equal(expected, ChatDocumentTools.NormalizeFileName(name, format));

    [Theory]
    [InlineData("report", "gdoc")]
    [InlineData("report", "gsheet")]
    public void NormalizeFileName_rejects_google_formats(string name, string format)
        => Assert.Null(ChatDocumentTools.NormalizeFileName(name, format));

    [Fact]
    public async Task Save_rejects_google_format_with_the_accepted_names()
    {
        var msg = await ChatDocumentTools.SaveAsync(
            "user-1", null!, "bao-cao", "gdoc", "nội dung");

        Assert.StartsWith("Error:", msg);
        Assert.Contains("docx", msg);
        Assert.Contains("pdf", msg);
        Assert.DoesNotContain("gdoc", msg);
    }

    [Fact]
    public async Task Save_rejects_unknown_format_with_the_accepted_names()
    {
        var msg = await ChatDocumentTools.SaveAsync(
            "user-1", null!, "x", "txt", "nội dung");

        Assert.StartsWith("Error:", msg);
        Assert.Contains("docx", msg);
        Assert.Contains("pdf", msg);
    }
}
