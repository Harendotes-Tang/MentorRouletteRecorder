using System.Buffers.Binary;
using System.Text;
using MentorRecorder.Collector.Import;
using NPOI.HSSF.Record;
using NPOI.HSSF.UserModel;
using NPOI.POIFS.FileSystem;
using NPOI.SS.UserModel;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>用实际 HSSF 生成 OLE/BIFF8 文件，验证导入边界而不是模拟解析成功。</summary>
public sealed class RunImportXlsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MentorRecorder.XlsTests", Guid.NewGuid().ToString("N"));
    public RunImportXlsTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void FirstVisibleSheetUsesFirstNonemptyHeaderAndSparseChineseColumns()
    {
        var path = Write(workbook =>
        {
            var hidden = workbook.CreateSheet("隐藏");
            Set(hidden.CreateRow(0), "副本"); Set(hidden.CreateRow(1), "不得读取");
            workbook.SetSheetVisibility(0, SheetVisibility.Hidden);
            var data = workbook.CreateSheet("本人记录");
            data.CreateRow(0);
            Set(data.CreateRow(2), "副本", "职业", "心得", "备注");
            var row = data.CreateRow(5);
            row.CreateCell(0).SetCellValue("合成副本");
            row.CreateCell(1).SetCellValue("骑士");
            row.CreateCell(2).SetCellValue("字面_x000A_\n正文");
        });
        var row = Assert.Single(RunImportSourceParser.Parse("XLS", path));
        Assert.Equal(6, row.RowNumber);
        Assert.Equal("合成副本", row.Values["duty_name"]!.GetValue<string>());
        Assert.Equal("字面_x000A_\n正文", row.Values["reflection_text"]!.GetValue<string>());
        Assert.Equal("", row.Values["note"]!.GetValue<string>());
        Assert.Empty(row.Errors);
    }

    [Fact]
    public void ExplicitColumnMappingAndRemarksReuseKnownFields()
    {
        var path = Write(workbook =>
        {
            var sheet = workbook.CreateSheet();
            Set(sheet.CreateRow(0), "名称", "备注"); Set(sheet.CreateRow(1), "合成副本", "合成备注");
        });
        var row = Assert.Single(RunImportSourceParser.Parse("XLS", path,
            columnMapping: new Dictionary<string, string> { ["名称"] = "duty_name" }));
        Assert.Empty(row.Errors);
        Assert.Equal("合成备注", row.Values["note"]!.GetValue<string>());
    }

    [Fact]
    public void FormulaCacheIsNeverImportedAndOtherRowsRemainUsable()
    {
        var path = Write(workbook =>
        {
            var sheet = workbook.CreateSheet();
            Set(sheet.CreateRow(0), "副本", "耗时");
            Set(sheet.CreateRow(1), "有公式", "");
            var formula = sheet.GetRow(1).GetCell(1); formula.SetCellFormula("1+2");
            new HSSFFormulaEvaluator(workbook).EvaluateFormulaCell(formula);
            Assert.Equal(3, formula.NumericCellValue);
            Set(sheet.CreateRow(2), "有效副本", "120000");
        });
        var rows = RunImportSourceParser.Parse("XLS", path);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows[0].Errors, error => error.Contains("公式", StringComparison.Ordinal));
        Assert.Equal("", rows[0].Values["duration_ms"]!.GetValue<string>());
        Assert.Empty(rows[1].Errors);
    }

    [Theory]
    [InlineData("yyyy-mm-dd", 1, false, "1900-01-01")]
    [InlineData("yyyy-mm-dd hh:mm", 61.5, false, "1900-03-01T12:00:00.000")]
    [InlineData("hh:mm:ss", 0.5, false, "12:00:00.000")]
    [InlineData("[h]:mm:ss", 1.5, false, "36:00:00.000")]
    [InlineData("yyyy-mm-dd hh:mm", 0.5, true, "1904-01-01T12:00:00.000")]
    public void DatesKeepExistingPrecisionAndBothDateSystems(string format, double serial, bool date1904, string expected)
    {
        var row = Assert.Single(RunImportSourceParser.Parse("XLS", DateWorkbook(format, serial, date1904)));
        Assert.Empty(row.Errors);
        Assert.Equal(expected, row.Values["entered_at_utc"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("yyyy-mm-dd", 60)]
    [InlineData("yyyy-mm-dd hh:mm", 60.5)]
    [InlineData("yyyy-mm-dd hh", 61.5)]
    [InlineData("[>=1]yyyy-mm-dd hh:mm;hh:mm", 61.5)]
    public void Invented1900DateAndAmbiguousPrecisionAreRowErrors(string format, double serial)
    {
        var row = Assert.Single(RunImportSourceParser.Parse("XLS", DateWorkbook(format, serial)));
        Assert.NotEmpty(row.Errors);
    }

    [Fact]
    public void AllHiddenSheetsAreRejected()
    {
        var path = Write(workbook => { workbook.CreateSheet(); workbook.CreateSheet(); workbook.SetSheetVisibility(0, SheetVisibility.Hidden); workbook.SetSheetVisibility(1, SheetVisibility.VeryHidden); });
        Assert.Contains("可见", Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("XLS", path)).Message);
    }

    [Theory]
    [InlineData("<html><table><tr><td>副本</td></tr></table></html>")]
    [InlineData("<?xml version=\"1.0\"?><Workbook />")]
    [InlineData("PK\u0003\u0004fake-xlsx")]
    public void RenamedTextAndZipAreRejected(string text)
    {
        var path = Path.Combine(_directory, "renamed.xls"); File.WriteAllText(path, text, new UTF8Encoding(false));
        Assert.Contains("真实", Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("XLS", path)).Message);
    }

    [Fact]
    public void OleMacroDirectoryIsRejectedBeforeWorkbookDecoding()
    {
        var path = Repackage(WorkbookBytes(), directory => directory.CreateDirectory("_VBA_PROJECT_CUR").CreateDirectory("VBA"));
        Assert.Contains("宏", Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("XLS", path)).Message);
    }

    [Fact]
    public void BiffFilePassIsRejectedWithoutTryingTheDefaultPassword()
    {
        var original = WorkbookBytes();
        var firstLength = BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(2));
        var split = firstLength + 4;
        var body = original[..split].Concat(new byte[] { 0x2f, 0x00, 0x02, 0x00, 0x00, 0x00 }).Concat(original[split..]).ToArray();
        Assert.Contains("加密", Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("XLS", Repackage(body))).Message);
    }

    [Fact]
    public void BiffMacroSheetIsRejected()
    {
        var bytes = WorkbookBytes();
        MutateRecord(bytes, 0x0085, record => record[5] = 1);
        Assert.Contains("宏", Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("XLS", Repackage(bytes))).Message);
    }

    [Fact]
    public void OldBiffAndTruncatedRecordAreRejected()
    {
        var bytes = WorkbookBytes(); bytes[4] = 0; bytes[5] = 5;
        Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("XLS", Repackage(bytes)));
        bytes = WorkbookBytes(); bytes[2] = 0xff; bytes[3] = 0xff;
        Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("XLS", Repackage(bytes)));
    }

    [Fact]
    public void BogusSharedStringCountAndOleAllocationBudgetAreRejected()
    {
        var bytes = WorkbookBytes();
        MutateRecord(bytes, 0x00fc, record => { record[4] = 0xff; record[5] = 0xff; record[6] = 0xff; record[7] = 0x7f; });
        Assert.Contains("共享字符串", Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("XLS", Repackage(bytes))).Message);
        var path = Write(workbook => workbook.CreateSheet());
        bytes = File.ReadAllBytes(path); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(44), 4096); File.WriteAllBytes(path, bytes);
        Assert.Contains("64 MiB", Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("XLS", path)).Message);
    }

    [Fact]
    public void TooManyRowsColumnsAndInputBytesAreRejected()
    {
        var rows = Write(workbook => { var sheet = workbook.CreateSheet(); for (var i = 0; i < 5002; i++) sheet.CreateRow(i); });
        Assert.Contains("5000", Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("XLS", rows)).Message);
        var columns = Write(workbook => { var sheet = workbook.CreateSheet(); sheet.CreateRow(0).CreateCell(200).SetCellValue("副本"); });
        Assert.Contains("200", Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("XLS", columns)).Message);
        var large = Path.Combine(_directory, "oversize.xls"); using (var file = File.Create(large)) file.SetLength(20 * 1024 * 1024 + 1);
        Assert.Contains("20 MiB", Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("XLS", large)).Message);
    }

    [Fact]
    public void LongCellIsBoundedAndRepeatedSharedTextHasAggregateBudget()
    {
        var path = Write(workbook => { var sheet = workbook.CreateSheet(); Set(sheet.CreateRow(0), "副本"); Set(sheet.CreateRow(1), new string('中', 8001)); });
        var row = Assert.Single(RunImportSourceParser.Parse("XLS", path));
        Assert.Equal(8000, row.Values["duty_name"]!.GetValue<string>().Length);
        Assert.Contains(row.Errors, error => error.Contains("8000", StringComparison.Ordinal));
        path = Write(workbook => { var sheet = workbook.CreateSheet(); Set(sheet.CreateRow(0), "副本"); for (var i = 1; i <= 1000; i++) Set(sheet.CreateRow(i), new string('中', 8000)); });
        Assert.Contains("展开文本", Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("XLS", path)).Message);
    }

    [Fact]
    public void UnicodeTruncationKeepsValidSurrogatesAndOtherRows()
    {
        var path = Write(workbook => { var sheet = workbook.CreateSheet(); Set(sheet.CreateRow(0), "副本"); Set(sheet.CreateRow(1), new string('a', 7999) + char.ConvertFromUtf32(0x1f600)); Set(sheet.CreateRow(2), "有效副本"); });
        var rows = RunImportSourceParser.Parse("XLS", path);
        Assert.Equal(2, rows.Count); Assert.NotEmpty(rows[0].Errors);
        Assert.Equal(7999, rows[0].Values["duty_name"]!.GetValue<string>().Length);
        Assert.Empty(rows[1].Errors);
    }

    [Fact]
    public void DamagedNumericStyleIndexIsARowErrorAndOtherRowsSurvive()
    {
        var path = Write(workbook => { var sheet = workbook.CreateSheet(); Set(sheet.CreateRow(0), "副本", "耗时"); Set(sheet.CreateRow(1), "损坏行", ""); sheet.GetRow(1).GetCell(1).SetCellValue(120000); Set(sheet.CreateRow(2), "有效副本", "120000"); });
        var filesystem = new NPOIFSFileSystem((Stream)File.OpenRead(path)); byte[] bytes;
        try { using var input = filesystem.Root.CreateDocumentInputStream("Workbook"); using var output = new MemoryStream(); input.CopyTo(output); bytes = output.ToArray(); }
        finally { filesystem.Close(); }
        MutateRecord(bytes, 0x0203, body => BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), 21));
        var rows = RunImportSourceParser.Parse("XLS", Repackage(bytes));
        Assert.Equal(2, rows.Count); Assert.Contains(rows[0].Errors, error => error.Contains("格式索引", StringComparison.Ordinal));
        Assert.Empty(rows[1].Errors);
    }

    [Fact]
    public void EmbeddedPictureCanBeIgnoredWithoutLoadingImageSharp()
    {
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "SixLabors.ImageSharp.dll")));
        // 固定样本由 HSSF 正常写入 1×1 PNG；生成图片与只读导入的依赖路径不同。
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Import", "picture.xls");
        var row = Assert.Single(RunImportSourceParser.Parse("XLS", path));
        Assert.Empty(row.Errors); Assert.Equal("带图片的合成副本", row.Values["duty_name"]!.GetValue<string>());
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly => assembly.GetName().Name == "SixLabors.ImageSharp");
    }

    [Fact]
    public void MissingWorksheetEofRejectsTheFileDespiteAValidGlobalEof()
    {
        var bytes = WorkbookBytes();
        Assert.Equal(new byte[] { 0x0a, 0, 0, 0 }, bytes[^4..]);
        Assert.Contains("不完整", Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("XLS", Repackage(bytes[..^4]))).Message);
    }

    [Fact]
    public void NonzeroDataAfterZeroPaddingIsRejectedWithoutRescanningEveryZeroRecord()
    {
        var bytes = WorkbookBytes().Concat(new byte[64 * 1024]).Concat(new byte[] { 1 }).ToArray();
        Assert.Contains("零填充", Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("XLS", Repackage(bytes))).Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void CompleteBiffWorkbookAcceptsShortZeroPadding(int count)
    {
        var bytes = WorkbookBytes().Concat(new byte[count]).ToArray();
        var row = Assert.Single(RunImportSourceParser.Parse("XLS", Repackage(bytes)));
        Assert.Empty(row.Errors);
    }

    [Theory]
    [InlineData(17, "深度")]
    [InlineData(1025, "1024")]
    [InlineData(10000, "1024")]
    public void RawOleDirectoryIsBoundedBeforeNpoiRecursion(int depth, string error)
    {
        var path = WriteRawOle(depth);
        Assert.Contains(error, Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("XLS", path)).Message);
    }

    [Fact]
    public void RawOleDirectoryCycleIsRejectedBeforeNpoiRecursion()
    {
        var path = WriteRawOle(4); var bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(512 + 4 * 128 + 76), 1); File.WriteAllBytes(path, bytes);
        Assert.Contains("循环", Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("XLS", path)).Message);
    }

    private string WriteRawOle(int depth)
    {
        var sectors = (depth + 4) / 4; var fatCount = 1;
        while ((sectors + fatCount + 127) / 128 != fatCount) fatCount = (sectors + fatCount + 127) / 128;
        var bytes = new byte[(1 + sectors + fatCount) * 512];
        new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }.CopyTo(bytes, 0);
        void Word(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
        void Integer(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
        Word(24, 0x3e); Word(26, 3); Word(28, 0xfffe); Word(30, 9); Word(32, 6);
        Integer(44, fatCount); Integer(48, 0); Integer(56, 4096); Integer(60, -2); Integer(68, -2);
        for (var i = 0; i < 109; i++) Integer(76 + i * 4, i < fatCount ? sectors + i : -1);
        for (var i = 0; i <= depth; i++)
        {
            var offset = 512 + i * 128; var name = Encoding.Unicode.GetBytes((i == 0 ? "Root Entry" : "d" + i) + '\0'); name.CopyTo(bytes, offset);
            Word(offset + 64, (ushort)name.Length); bytes[offset + 66] = (byte)(i == 0 ? 5 : 1); bytes[offset + 67] = 1;
            Integer(offset + 68, -1); Integer(offset + 72, -1); Integer(offset + 76, i == depth ? -1 : i + 1); Integer(offset + 116, -2);
        }
        for (var i = 0; i < fatCount * 128; i++)
            Integer((sectors + 1) * 512 + i * 4, i < sectors - 1 ? i + 1 : i == sectors - 1 ? -2 : i < sectors + fatCount ? -3 : -1);
        var path = Path.Combine(_directory, "deep-" + depth + ".xls"); File.WriteAllBytes(path, bytes); return path;
    }

    private string DateWorkbook(string format, double serial, bool date1904 = false) => Write(workbook =>
    {
        if (date1904) workbook.Workbook.Records.Cast<object>().OfType<DateWindow1904Record>().Single().Windowing = 1;
        var sheet = workbook.CreateSheet(); Set(sheet.CreateRow(0), "副本", "开始时间");
        var row = sheet.CreateRow(1); row.CreateCell(0).SetCellValue("合成副本");
        var cell = row.CreateCell(1); cell.SetCellValue(serial); cell.CellStyle = workbook.CreateCellStyle();
        cell.CellStyle.DataFormat = workbook.CreateDataFormat().GetFormat(format);
    });

    private string Write(Action<HSSFWorkbook> populate)
    {
        using var workbook = new HSSFWorkbook(); populate(workbook);
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".xls");
        using var file = File.Create(path); workbook.Write(file, leaveOpen: true); return path;
    }

    private byte[] WorkbookBytes()
    {
        var path = Write(workbook => { var sheet = workbook.CreateSheet(); Set(sheet.CreateRow(0), "副本"); Set(sheet.CreateRow(1), "合成副本"); });
        var filesystem = new NPOIFSFileSystem((Stream)File.OpenRead(path));
        try { using var stream = filesystem.Root.CreateDocumentInputStream("Workbook"); using var output = new MemoryStream(); stream.CopyTo(output); return output.ToArray(); }
        finally { filesystem.Close(); }
    }

    private string Repackage(byte[] workbook, Action<DirectoryEntry>? populate = null)
    {
        var filesystem = new NPOIFSFileSystem();
        try
        {
            using var data = new MemoryStream(workbook); filesystem.Root.CreateDocument("Workbook", data); populate?.Invoke(filesystem.Root);
            var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".xls");
            using var file = File.Create(path); filesystem.WriteFileSystem(file); return path;
        }
        finally { filesystem.Close(); }
    }

    private static void MutateRecord(byte[] bytes, ushort id, Action<byte[]> mutate)
    {
        for (var position = 0; position + 4 <= bytes.Length;)
        {
            var recordId = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position));
            var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 2)); position += 4;
            if (recordId == id) { var body = bytes.AsSpan(position, length).ToArray(); mutate(body); body.CopyTo(bytes, position); return; }
            position += length;
        }
        throw new InvalidOperationException("Synthetic workbook missing record " + id);
    }

    private static void Set(IRow row, params string[] cells) { for (var i = 0; i < cells.Length; i++) row.CreateCell(i).SetCellValue(cells[i]); }
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
