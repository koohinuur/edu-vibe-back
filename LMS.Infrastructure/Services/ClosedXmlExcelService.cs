using ClosedXML.Excel;
using LMS.Application.Common.Abstractions;

namespace LMS.Infrastructure.Services;

/// <summary>
/// ClosedXML-backed <see cref="IExcelService"/>. Reading is tolerant (any
/// sheet-less/empty file yields an empty list); writing produces a plain,
/// header-bolded workbook that opens cleanly in Excel / LibreOffice / Sheets.
/// </summary>
public sealed class ClosedXmlExcelService : IExcelService
{
    public IReadOnlyList<string> ReadFirstColumn(Stream stream)
    {
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheets.FirstOrDefault();
        if (sheet is null) return Array.Empty<string>();

        var values = new List<string>();
        // RowsUsed() skips fully-blank rows; we still trim + skip blank first cells
        // so stray whitespace rows don't become "empty" import entries.
        foreach (var row in sheet.RowsUsed())
        {
            var text = row.Cell(1).GetString().Trim();
            if (!string.IsNullOrWhiteSpace(text)) values.Add(text);
        }
        return values;
    }

    public byte[] Build(IReadOnlyList<ExcelSheet> sheets)
    {
        using var workbook = new XLWorkbook();
        foreach (var sheet in sheets)
        {
            var ws = workbook.Worksheets.Add(SafeSheetName(sheet.Name));

            for (var c = 0; c < sheet.Headers.Count; c++)
            {
                var cell = ws.Cell(1, c + 1);
                cell.Value = sheet.Headers[c];
                cell.Style.Font.Bold = true;
            }

            for (var r = 0; r < sheet.Rows.Count; r++)
            {
                var row = sheet.Rows[r];
                for (var c = 0; c < row.Count; c++)
                    ws.Cell(r + 2, c + 1).Value = row[c] ?? string.Empty;
            }

            ws.Columns().AdjustToContents();
        }

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    // Excel sheet names are capped at 31 chars and can't contain : \ / ? * [ ].
    private static string SafeSheetName(string name)
    {
        var cleaned = new string(name.Select(ch => "\\/:?*[]".Contains(ch) ? '-' : ch).ToArray());
        return cleaned.Length <= 31 ? cleaned : cleaned[..31];
    }
}
