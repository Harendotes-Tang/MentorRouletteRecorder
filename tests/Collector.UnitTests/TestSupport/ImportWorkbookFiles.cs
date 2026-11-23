using System.IO.Compression;
using System.Text;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>Minimal .xlsx packages written by hand, so a test controls every part the importer reads.</summary>
internal static class ImportWorkbookFiles
{
    /// <summary>
    /// Writes a one-sheet workbook into <paramref name="directory"/> under a fresh name and returns its path: content
    /// types, the workbook (named sheet 合成), its relationships, the sheet and the styles; optionally shared strings,
    /// a macro part or an external sheet target, all of which the importer must handle or refuse.
    /// </summary>
    /// <param name="directory">Folder to write into.</param>
    /// <param name="worksheet">The sheet's XML.</param>
    /// <param name="sharedStrings">The shared-strings part, if any.</param>
    /// <param name="date1904">Whether the workbook uses the 1904 date system.</param>
    /// <param name="sheetName">The sheet part's file name.</param>
    /// <param name="styles">The styles part; one plain and one date-time format by default.</param>
    /// <param name="macro">Whether to add a macro part.</param>
    /// <param name="external">Whether the sheet relationship targets an external file.</param>
    public static string Write(
        string directory, string worksheet, string? sharedStrings = null, bool date1904 = false,
        string sheetName = "sheet1.xml", string? styles = null, bool macro = false, bool external = false)
    {
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".xlsx");
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        void Add(string name, string xml)
        {
            using var stream = zip.CreateEntry(name).Open();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(xml);
        }

        Add("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/></Types>");
        Add("xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><workbookPr date1904=\"" + (date1904 ? "1" : "0") + "\"/><sheets><sheet name=\"合成\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
        Add("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/" + sheetName + "\"" + (external ? " TargetMode=\"External\"" : "") + "/></Relationships>");
        Add("xl/worksheets/" + sheetName, worksheet);
        Add("xl/styles.xml", styles ?? "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><cellXfs><xf numFmtId=\"0\"/><xf numFmtId=\"22\"/></cellXfs></styleSheet>");
        if (sharedStrings is not null)
        {
            Add("xl/sharedStrings.xml", sharedStrings);
        }

        if (macro)
        {
            Add("xl/vbaProject.bin", "synthetic macro marker, never executed");
        }

        return path;
    }
}
