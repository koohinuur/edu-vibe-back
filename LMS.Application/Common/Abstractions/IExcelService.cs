namespace LMS.Application.Common.Abstractions;

/// <summary>One sheet in a generated workbook: a header row + data rows.</summary>
public sealed record ExcelSheet(
    string Name,
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string?>> Rows);

/// <summary>
/// One parsed import row: a student's full name (FIO, optional) and email. The
/// reader identifies the email as whichever of the first two columns contains an
/// '@', so it tolerates either column order and single-column (email-only) files.
/// </summary>
public sealed record ExcelImportRow(string? FullName, string Email);

/// <summary>
/// Thin .xlsx read/write port so the Application layer stays free of the concrete
/// spreadsheet library (ClosedXML lives in Infrastructure). Used by the bulk
/// student import: read the uploaded email column, then build the downloadable
/// result workbook.
/// </summary>
public interface IExcelService
{
    /// <summary>
    /// Reads the first column of the first worksheet, returning each non-empty,
    /// trimmed cell value top-to-bottom. Header detection is the caller's job.
    /// </summary>
    IReadOnlyList<string> ReadFirstColumn(Stream stream);

    /// <summary>
    /// Reads the first two columns (full name + email, in either order) of the
    /// first worksheet as import rows, top-to-bottom. Fully-blank rows are
    /// dropped; the email is whichever cell contains an '@'. Header detection is
    /// the caller's job.
    /// </summary>
    IReadOnlyList<ExcelImportRow> ReadNameEmailRows(Stream stream);

    /// <summary>Builds an .xlsx byte array from one or more sheets.</summary>
    byte[] Build(IReadOnlyList<ExcelSheet> sheets);
}
