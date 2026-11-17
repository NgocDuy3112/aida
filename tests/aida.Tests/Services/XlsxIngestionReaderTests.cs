using aida.Services.Ingestion;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace aida.Tests.Services;

public class XlsxIngestionReaderTests
{
    private const string SheetContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    [Fact]
    public async Task Reads_inline_strings_and_numbers()
    {
        using var stream = BuildWorkbook();
        var reader = new XlsxIngestionReader();

        var document = await reader.ReadAsync(stream, "test.xlsx", SheetContentType, CancellationToken.None);

        var text = string.Join("\n", document.EnumerateContent().Select(c => c.Text));
        Assert.Contains("Tháng", text);
        Assert.Contains("Doanh thu", text);
        Assert.Contains("Chi phí", text);
        Assert.Contains("12000000", text);
        Assert.Contains("17800000", text);
    }

    private static MemoryStream BuildWorkbook()
    {
        var stream = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, true))
        {
            var workbookPart = doc.AddWorkbookPart();
            var workbook = new Workbook();
            workbookPart.Workbook = workbook;

            var sheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData();

            sheetData.Append(
                new Row(
                    Cell("A1", "Tháng"),
                    Cell("B1", "Doanh thu"),
                    Cell("C1", "Chi phí")),
                new Row(
                    Cell("A2", "Tháng 1"),
                    new Cell { CellReference = "B2", DataType = CellValues.Number, CellValue = new CellValue("12000000") },
                    new Cell { CellReference = "C2", DataType = CellValues.Number, CellValue = new CellValue("8000000") }),
                new Row(
                    Cell("A4", "Tháng 3"),
                    new Cell { CellReference = "B4", DataType = CellValues.Number, CellValue = new CellValue("17800000") },
                    new Cell { CellReference = "C4", DataType = CellValues.Number, CellValue = new CellValue("10100000") }));

            sheetPart.Worksheet = new Worksheet(sheetData);
            workbook.Append(new Sheets(
                new Sheet
                {
                    Id = workbookPart.GetIdOfPart(sheetPart),
                    SheetId = 1,
                    Name = "Doanh thu",
                }));
            doc.Save();
        }

        stream.Position = 0;
        return stream;
    }

    private static Cell Cell(string reference, string value) => new()
    {
        CellReference = reference,
        DataType = CellValues.InlineString,
        InlineString = new InlineString(new Text(value)),
    };
}
