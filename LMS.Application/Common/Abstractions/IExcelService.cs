namespace LMS.Application.Common.Abstractions;

/// <summary>One sheet in a generated workbook: a header row + data rows.</summary>
public sealed record ExcelSheet(
    string Name,
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string?>> Rows);

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

    /// <summary>Builds an .xlsx byte array from one or more sheets.</summary>
    byte[] Build(IReadOnlyList<ExcelSheet> sheets);
}
