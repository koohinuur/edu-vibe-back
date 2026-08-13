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

    public IReadOnlyList<ExcelImportRow> ReadNameEmailRows(Stream stream)
    {
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheets.FirstOrDefault();
        if (sheet is null) return Array.Empty<ExcelImportRow>();

        var rows = new List<ExcelImportRow>();
        foreach (var row in sheet.RowsUsed())
        {
            var a = row.Cell(1).GetString().Trim();
            var b = row.Cell(2).GetString().Trim();
            if (string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b)) continue;

            // The email is whichever cell has an '@'; the other is the name. This
            // tolerates both column orders and old email-only (single column)
            // files. When neither has an '@' (e.g. a "FIO | Email" header row),
            // column A is treated as the email so the caller's header check drops it.
            string email, name;
            if (b.Contains('@')) { email = b; name = a; }
            else if (a.Contains('@')) { email = a; name = b; }
            else { email = a; name = b; }

            rows.Add(new ExcelImportRow(string.IsNullOrWhiteSpace(name) ? null : name, email));
        }
        return rows;
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
