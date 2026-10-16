using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NPOI.HSSF.UserModel;
using NPOI.POIFS.FileSystem;
using NPOI.POIFS.Storage;
using NPOI.SS.UserModel;
using MentorRecorder.Collector.Contracts.Texts;

namespace MentorRecorder.Collector.Import;

public static partial class RunImportSourceParser
{
    /// <summary>
    /// 读取无宏、未加密的 BIFF8；先限制 OLE 流和 BIFF 记录，再由 HSSF 解码。
    /// 仅首个可见表投影为候选；公式不读取缓存，日期统一交给 XLSX 的精度规则。
    /// </summary>
    private static IReadOnlyList<ImportSourceRow> ParseBinaryWorkbook(string path,
        IReadOnlyDictionary<string, string>? mapping)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaxInputBytes) throw Invalid(CollectorTexts.ImportSourceFileTooLarge());
        var inputBytes = file.Length;
        Span<byte> signature = stackalloc byte[8];
        if (file.Read(signature) != signature.Length || !signature.SequenceEqual(new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }))
            throw Invalid(CollectorTexts.ImportXlsNotAWorkbook());
        file.Position = 0;
        // Stream 构造器按 OLE FAT 声明预分配；先校验声明，不能让短文件请求数 GiB。
        NPOIFSFileSystem? filesystem = null;
        try
        {
            var bytes = new byte[(int)inputBytes];
            file.ReadExactly(bytes);
            ValidateBinaryOleContainer(bytes);
            file.Position = 0;
            var header = new HeaderBlock(file);
            if (BATBlock.CalculateMaximumSize(header.BigBlockSize, header.BATCount) > MaxInflatedZipBytes)
                throw Invalid(CollectorTexts.ImportXlsTooLarge());
            file.Position = 0;
            filesystem = new NPOIFSFileSystem((Stream)file);
            var entries = 0;
            long streamBytes = 0;
            ValidateBinaryDirectory(filesystem.Root, 0, inputBytes, ref entries, ref streamBytes);
            if (!filesystem.Root.HasEntry("Workbook"))
                throw Invalid(CollectorTexts.ImportXlsWorkbookMissing());
            if (filesystem.Root.GetEntry("Workbook") is not DocumentEntry document)
                throw Invalid(CollectorTexts.ImportXlsWorkbookDamaged());
            using (var stream = new DocumentInputStream(document))
            {
                var workbookBytes = new byte[document.Size];
                stream.ReadExactly(workbookBytes);
                ValidateBinaryRecords(workbookBytes);
            }
            using var workbook = new HSSFWorkbook(filesystem.Root, preserveNodes: false);
            if (workbook.NumberOfSheets > 1024) throw Invalid(CollectorTexts.ImportXlsTooManySheets());
            var sheetIndex = Enumerable.Range(0, workbook.NumberOfSheets)
                .FirstOrDefault(index => !workbook.IsSheetHidden(index) && !workbook.IsSheetVeryHidden(index), -1);
            if (sheetIndex < 0) throw Invalid(CollectorTexts.ImportXlsNoVisibleSheet());
            var sheet = workbook.GetSheetAt(sheetIndex);
            if (sheet.PhysicalNumberOfRows > MaxRows + 1)
                throw Invalid(CollectorTexts.ImportSheetTooManyRows());
            var dateStyles = ReadBinaryDateStyles(workbook);
            var records = new List<TableRow>();
            var worksheetRows = 0;
            long expandedTextBytes = 0;
            foreach (IRow row in sheet)
            {
                if (++worksheetRows > MaxRows + 1)
                    throw Invalid(CollectorTexts.ImportSheetTooManyRows());
                var number = row.RowNum + 1;
                if (row.LastCellNum > MaxColumns || row.PhysicalNumberOfCells > MaxColumns)
                    throw Invalid(CollectorTexts.ImportSheetRowTooWide(number));
                var cells = new Dictionary<int, Cell>();
                var errors = new List<UserText>();
                foreach (ICell cell in row)
                {
                    var value = ReadBinaryCell(cell, dateStyles, workbook.IsDate1904(), errors);
                    expandedTextBytes += StrictUtf8.GetByteCount(value);
                    if (expandedTextBytes > MaxInputBytes)
                        throw Invalid(CollectorTexts.ImportSheetTextTooLarge());
                    cells.Add(cell.ColumnIndex, new Cell(value));
                }
                if (cells.Count == 0) continue;
                var ordered = Enumerable.Range(0, cells.Keys.Max() + 1)
                    .Select(index => cells.GetValueOrDefault(index, new Cell(string.Empty))).ToArray();
                if (ordered.Any(cell => cell.Text.Length != 0) || errors.Count != 0)
                    records.Add(new TableRow(number, ordered, errors));
            }
            if (records.Count == 0) return Array.Empty<ImportSourceRow>();
            var headerWidth = records[0].Cells.Count;
            var padded = records.Select((record, index) => index == 0 || record.Cells.Count >= headerWidth ? record :
                record with { Cells = record.Cells.Concat(Enumerable.Repeat(new Cell(string.Empty), headerWidth - record.Cells.Count)).ToArray() }).ToArray();
            return ProjectTable(padded, mapping, nativeCsv: false);
        }
        catch (Exception error) when (error is not InvalidDataException &&
            (error is IOException or ArgumentException or InvalidOperationException or IndexOutOfRangeException or InvalidCastException or
             OverflowException or NotSupportedException or RegexMatchTimeoutException or EncoderFallbackException ||
             error.GetType().Namespace?.StartsWith("NPOI", StringComparison.Ordinal) == true))
        {
            throw Invalid(CollectorTexts.ImportXlsUnreadable(), error);
        }
        finally { filesystem?.Close(); }
    }

    private static void ValidateBinaryDirectory(DirectoryEntry directory, int depth, long inputBytes,
        ref int entries, ref long streamBytes)
    {
        if (depth > 16) throw Invalid(CollectorTexts.ImportXlsDirectoryTooDeep());
        using var iterator = directory.Entries;
        while (iterator.MoveNext())
        {
            var entry = iterator.Current;
            if (++entries > 1024) throw Invalid(CollectorTexts.ImportXlsTooManyEntries());
            if (entry.Name.Equals("EncryptionInfo", StringComparison.OrdinalIgnoreCase) ||
                entry.Name.Equals("EncryptedPackage", StringComparison.OrdinalIgnoreCase))
                throw Invalid(CollectorTexts.ImportXlsEncrypted());
            if (entry.Name.Equals("VBA", StringComparison.OrdinalIgnoreCase) ||
                entry.Name.Equals("_VBA_PROJECT", StringComparison.OrdinalIgnoreCase) ||
                entry.Name.Equals("_VBA_PROJECT_CUR", StringComparison.OrdinalIgnoreCase) ||
                entry.Name.Equals("Macros", StringComparison.OrdinalIgnoreCase))
                throw Invalid(CollectorTexts.ImportXlsMacro());
            if (entry is DirectoryEntry child)
                ValidateBinaryDirectory(child, depth + 1, inputBytes, ref entries, ref streamBytes);
            else if (entry is DocumentEntry document)
            {
                streamBytes += document.Size;
                if (document.Size < 0 || document.Size > inputBytes || streamBytes > MaxInflatedZipBytes)
                    throw Invalid(CollectorTexts.ImportXlsStreamSizeInvalid());
            }
            else throw Invalid(CollectorTexts.ImportXlsEntryTypeInvalid());
        }
    }

    /// <summary>在 HSSF 分配共享字符串和记录对象之前拒绝加密、XLM 宏及伪造计数。</summary>
    private static void ValidateBinaryRecords(ReadOnlySpan<byte> bytes)
    {
        var position = 0;
        var count = 0;
        var sawEof = false;
        var depth = 0;
        while (position < bytes.Length)
        {
            // BIFF Workbook 流允许末尾零填充；不能把被截断的正文当填充接受。
            if (bytes.Length - position < 4)
            {
                if (sawEof && depth == 0 && bytes[position..].IndexOfAnyExcept((byte)0) < 0) break;
                throw Invalid(CollectorTexts.ImportXlsRecordsTruncated());
            }
            if (bytes.Length - position >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(bytes[position..]) == 0)
            {
                if (sawEof && depth == 0 && bytes[position..].IndexOfAnyExcept((byte)0) < 0) break;
                throw Invalid(CollectorTexts.ImportXlsRecordsMalformed());
            }
            if (bytes.Length - position < 4 || ++count > 1_000_000)
                throw Invalid(CollectorTexts.ImportXlsRecordsTooMany());
            var id = BinaryPrimitives.ReadUInt16LittleEndian(bytes[position..]);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(position + 2)..]);
            position += 4;
            if (length > 8224 || length > bytes.Length - position)
                throw Invalid(CollectorTexts.ImportXlsRecordLengthInvalid());
            var data = bytes.Slice(position, length);
            if (count == 1 && (id != 0x0809 || length < 4 || BinaryPrimitives.ReadUInt16LittleEndian(data) != 0x0600 ||
                BinaryPrimitives.ReadUInt16LittleEndian(data[2..]) != 0x0005))
                throw Invalid(CollectorTexts.ImportXlsVersionUnsupported());
            if (id == 0x002f) throw Invalid(CollectorTexts.ImportXlsEncrypted());
            if (id == 0x0809)
            {
                if (length < 4 || ++depth > 16) throw Invalid(CollectorTexts.ImportXlsSubstreamInvalid());
            }
            else if (id == 0x000a)
            {
                if (length != 0 || depth == 0) throw Invalid(CollectorTexts.ImportXlsEndMarkerInvalid());
                depth--;
            }
            // BoundSheet8 的 type=1、BOF 的 type=0x0040 都表示 Excel 4.0 宏工作表。
            if ((id == 0x0085 && length >= 6 && data[5] == 1) ||
                (id == 0x0809 && length >= 4 && BinaryPrimitives.ReadUInt16LittleEndian(data[2..]) == 0x0040))
                throw Invalid(CollectorTexts.ImportXlsMacro());
            if (id == 0x00fc && (length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(data) > 1_000_000 ||
                BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) > 100_000))
                throw Invalid(CollectorTexts.ImportXlsTooManyStrings());
            sawEof |= id == 0x000a;
            position += length;
        }
        if (count == 0 || !sawEof || depth != 0) throw Invalid(CollectorTexts.ImportXlsWorkbookIncomplete());
    }

    private static IReadOnlyList<ExcelCellStyle> ReadBinaryDateStyles(HSSFWorkbook workbook)
    {
        if (workbook.NumCellStyles > 10_000) throw Invalid(CollectorTexts.ImportXlsTooManyStyles());
        // 使用同一 formatCode 分类，避免 HSSF 的 DateCellValue 将日期精度自动补成午夜。
        var formats = workbook.Workbook.Formats;
        if (formats.Count > 10_000) throw Invalid(CollectorTexts.ImportXlsTooManyFormats());
        var styles = new XDocument(new XElement("styleSheet",
            new XElement("numFmts", formats.Select(format => new XElement("numFmt",
                new XAttribute("numFmtId", format.IndexCode), new XAttribute("formatCode", format.FormatString)))),
            new XElement("cellXfs", Enumerable.Range(0, workbook.NumCellStyles).Select(index => new XElement("xf",
                new XAttribute("numFmtId", workbook.GetCellStyleAt(index).DataFormat))))));
        return ReadDateStyles(styles);
    }

    private static string ReadBinaryCell(ICell cell, IReadOnlyList<ExcelCellStyle> dateStyles,
        bool date1904, List<UserText> errors)
    {
        if (cell.CellType == CellType.String)
        {
            // BIFF 字符串是字面 Unicode，不解释 OOXML 的 _xNNNN_ 转义。
            var text = cell.StringCellValue;
            try { _ = StrictUtf8.GetByteCount(text); }
            catch (EncoderFallbackException) { AddError(errors, CollectorTexts.ImportCellTextUnreadable()); return string.Empty; }
            return BoundCell(text, errors);
        }
        if (cell.CellType == CellType.Formula)
            return ReadExcelCell(new XElement("c", new XElement("f")), Array.Empty<string>(), dateStyles, date1904, errors);
        short style;
        try { style = cell.CellStyle.Index; }
        catch (Exception error) when (error is InvalidCastException or IndexOutOfRangeException or ArgumentException)
        { AddError(errors, CollectorTexts.ImportCellStyleInvalid()); return string.Empty; }
        var element = new XElement("c", new XAttribute("s", style));
        switch (cell.CellType)
        {
            case CellType.Numeric: element.Add(new XElement("v", cell.NumericCellValue.ToString("R", CultureInfo.InvariantCulture))); break;
            case CellType.Boolean: element.Add(new XAttribute("t", "b"), new XElement("v", cell.BooleanCellValue ? "1" : "0")); break;
            case CellType.Error: element.Add(new XAttribute("t", "e")); break;
            case CellType.Blank: return string.Empty;
            default: AddError(errors, CollectorTexts.ImportCellTypeUnsupported()); return string.Empty;
        }
        return ReadExcelCell(element, Array.Empty<string>(), dateStyles, date1904, errors);
    }
}
