using System.IO.Compression;
using System.Xml.Linq;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>The import templates that ship with the Desktop, filled in the way a player fills them.</summary>
internal static class ImportTemplateFiles
{
    /// <summary>
    /// Copies the shipped personal-records.xlsx to <paramref name="path"/>, keeps only the header row of its sheet
    /// and adds one row per job name: <paramref name="duty"/>, then the name with two spaces on each side.
    /// </summary>
    /// <param name="path">Where to write the workbook.</param>
    /// <param name="duty">The duty every row names.</param>
    /// <param name="jobNames">One job name per row, in order.</param>
    public static void FillPersonalRecords(string path, string duty, IReadOnlyList<string> jobNames)
    {
        File.Copy(RepositoryFiles.Find("src", "Desktop", "resources", "import-templates", "personal-records.xlsx"), path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = zip.GetEntry("xl/worksheets/sheet1.xml")!;
        XDocument document;
        using (var stream = entry.Open())
        {
            document = XDocument.Load(stream);
        }

        var ns = document.Root!.Name.Namespace;
        var sheetData = document.Root.Element(ns + "sheetData")!;
        sheetData.Elements(ns + "row").Where(row => row.Attribute("r")?.Value != "1").Remove();
        for (var i = 0; i < jobNames.Count; i++)
        {
            sheetData.Add(new XElement(ns + "row", new XAttribute("r", i + 2),
                new[] { duty, "  " + jobNames[i] + "  " }.Select((value, column) =>
                    new XElement(ns + "c", new XAttribute("r", ((char)('A' + column)).ToString() + (i + 2)),
                        new XAttribute("t", "inlineStr"), new XElement(ns + "is", new XElement(ns + "t", value))))));
        }

        entry.Delete();
        using var output = zip.CreateEntry("xl/worksheets/sheet1.xml").Open();
        document.Save(output);
    }
}
