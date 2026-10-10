using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Export;

namespace MentorRecorder.Collector.Import;

/// <summary>一条独立候选；RowNumber 是来源行号，Values 归解析器所有，Errors 只描述本行的读取错误。</summary>
public sealed record ImportSourceRow(int RowNumber, JsonObject Values, IReadOnlyList<string> Errors);

/// <summary>
/// 只读取本地来源并建立候选，不验证游戏事实、不写入数据库，也不执行公式、宏或外部链接。
/// 文件损坏和资源超限抛出 InvalidDataException；可定位的单元格错误留在对应候选中供预览修正。
/// </summary>
public static partial class RunImportSourceParser
{
    private const int MaxInputBytes = 20 * 1024 * 1024;
    private const int MaxRows = 5000;
    private const int MaxColumns = 200;
    private const int MaxCellCharacters = 8000;
    private const int MaxXmlCharacters = 16 * 1024 * 1024;
    private const long MaxInflatedZipBytes = 64L * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "run_id", "revision", "region", "game_build", "mentor_roulette_id", "content_id",
        "territory_id", "duty_name", "duty_category", "duty_source", "job_id", "job_name", "role",
        "matched_at_utc", "entered_at_utc", "ended_at_utc", "duration_ms", "result",
        "detection_confidence", "source", "contributes_to_goal", "manually_created",
        "manually_corrected", "soft_deleted", "pending_review", "note", "created_at_utc",
        "updated_at_utc", "reflection", "reflection_text", "reflection_mood", "source_recorded_at",
        "source_recorded_at_utc", "source_name", "source_key", "incomplete", "mentor_confirmed", "import_metadata",
    };

    private static readonly Dictionary<string, string> HeaderAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["副本"] = "duty_name", ["副本名称"] = "duty_name", ["职业"] = "job_name",
        ["心得"] = "reflection_text", ["心情"] = "reflection_mood", ["记录时间"] = "source_recorded_at",
        ["开始时间"] = "entered_at_utc", ["进本时间"] = "entered_at_utc", ["结束时间"] = "ended_at_utc",
        ["结果"] = "result", ["耗时"] = "duration_ms", ["时长"] = "duration_ms", ["备注"] = "note",
        ["matched_at"] = "matched_at_utc", ["entered_at"] = "entered_at_utc",
        ["ended_at"] = "ended_at_utc", ["duration"] = "duration_ms",
    };

    /// <summary>
    /// 读取 CSV/XLS/XLSX/JSON/BACKUP/PASTE；传入 rows 时优先深拷贝已编辑候选，保留预览证据字段。
    /// SCREENSHOT/ROWS 必须提供 rows。表格映射的键是原表头，值是已知规范字段；未知表头不导入。
    /// </summary>
    public static IReadOnlyList<ImportSourceRow> Parse(
        string sourceKind, string? filePath = null, string? text = null, JsonArray? rows = null,
        IReadOnlyDictionary<string, string>? columnMapping = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKind);
        var kind = sourceKind.Trim().ToUpperInvariant();
        if (kind is not ("CSV" or "XLS" or "XLSX" or "JSON" or "BACKUP" or "PASTE" or "SCREENSHOT" or "ROWS"))
            throw new InvalidDataException("不支持的导入来源。 ");
        if (rows is not null) return ParseJsonRows(rows, preserveCandidateFields: true);

        return kind switch
        {
            "CSV" => ParseTable(ReadText(filePath, text, allowGb18030: true), ',', columnMapping, nativeCsv: true),
            "PASTE" => ParsePastedTable(ReadText(null, text, allowGb18030: false), columnMapping),
            "JSON" => ParseJson(ReadText(filePath, text, allowGb18030: false)),
            "XLSX" => ParseWorkbook(RequireFile(filePath), columnMapping),
            "XLS" => ParseBinaryWorkbook(RequireFile(filePath), columnMapping),
            "BACKUP" => ParseBackup(RequireFile(filePath)),
            _ => throw new InvalidDataException("该来源必须提供识别或编辑后的候选行。"),
        };
    }

    private static string RequireFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new InvalidDataException("请选择本地来源文件。");
        var path = Path.GetFullPath(filePath);
        var info = new FileInfo(path);
        if (!info.Exists) throw new InvalidDataException("来源文件不存在。");
        if (info.Length > MaxInputBytes) throw new InvalidDataException("来源文件超过 20 MiB 上限。");
        return path;
    }

    private static string ReadText(string? filePath, string? text, bool allowGb18030)
    {
        if (text is not null)
        {
            try
            {
                if (text.Length > MaxInputBytes || StrictUtf8.GetByteCount(text) > MaxInputBytes)
                    throw new InvalidDataException("来源文本超过 20 MiB 上限。");
            }
            catch (EncoderFallbackException error) { throw new InvalidDataException("来源文本包含非法 Unicode 字符。", error); }
            return text.TrimStart('\uFEFF');
        }

        var path = RequireFile(filePath);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int read;
        while ((read = file.Read(bytes, 0, bytes.Length)) != 0)
        {
            if (buffer.Length + read > MaxInputBytes) throw new InvalidDataException("来源文件超过 20 MiB 上限。");
            buffer.Write(bytes, 0, read);
        }
        var body = buffer.ToArray();
        var offset = body.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        try { return StrictUtf8.GetString(body, offset, body.Length - offset); }
        catch (DecoderFallbackException error)
        {
            // UTF-8 BOM 宣告了编码，出现非法字节时不能换编码掩盖文件损坏。
            if (!allowGb18030 || offset != 0) throw new InvalidDataException("来源不是有效 UTF-8 文本。", error);
            try
            {
                // 框架已有 provider 时才提供旧中文 CSV 回退；不下载或添加编码包。
                var providerType = Type.GetType("System.Text.CodePagesEncodingProvider, System.Text.Encoding.CodePages");
                if (providerType?.GetProperty("Instance")?.GetValue(null) is EncodingProvider provider)
                    Encoding.RegisterProvider(provider);
                return Encoding.GetEncoding("GB18030", EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback).GetString(body);
            }
            catch (Exception fallbackError) when (fallbackError is ArgumentException or NotSupportedException or DecoderFallbackException)
            {
                throw new InvalidDataException("来源不是有效 UTF-8，当前运行环境也无法按 GB18030 读取。", fallbackError);
            }
        }
    }

    private static IReadOnlyList<ImportSourceRow> ParseJson(string text)
    {
        try
        {
            var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 16 });
            if (node is not JsonArray array) throw new InvalidDataException("JSON 来源必须是 Run 数组。");
            return ParseJsonRows(array, preserveCandidateFields: false);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        { throw new InvalidDataException("JSON 来源结构损坏或包含重复字段。", error); }
    }

    private static IReadOnlyList<ImportSourceRow> ParseJsonRows(JsonArray rows, bool preserveCandidateFields)
    {
        if (rows.Count > MaxRows) throw new InvalidDataException("候选超过 5000 行上限。");
        var output = new List<ImportSourceRow>(rows.Count);
        long budget = 0;
        var nodeCount = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var errors = new List<string>();
            if (rows[i] is not JsonObject original)
            {
                output.Add(new ImportSourceRow(i + 1, new JsonObject(), new[] { "候选必须是 JSON 对象。" }));
                continue;
            }
            var clone = (JsonObject)CloneBounded(original, errors, ref budget, ref nodeCount, 0)!;
            if (!preserveCandidateFields)
            {
                foreach (var key in clone.Select(pair => pair.Key).Where(key => !Fields.Contains(key)).ToArray())
                    clone.Remove(key);
            }
            FlattenReflection(clone);
            FlattenImportMetadata(clone);
            output.Add(new ImportSourceRow(i + 1, clone, errors));
        }
        return output;
    }

    private static JsonNode? CloneBounded(JsonNode? node, List<string> errors, ref long budget, ref int count, int depth)
    {
        if (++count > 1_000_000 || depth > 16) throw new InvalidDataException("候选 JSON 结构超过资源上限。");
        budget += 16;
        if (node is JsonObject obj)
        {
            if (obj.Count > MaxColumns) throw new InvalidDataException("候选对象超过 200 个字段上限。");
            var copy = new JsonObject();
            foreach (var (key, value) in obj)
            {
                try { budget += StrictUtf8.GetByteCount(key); }
                catch (EncoderFallbackException error) { throw new InvalidDataException("候选字段名包含非法 Unicode。", error); }
                if (key.Length > MaxCellCharacters || budget > MaxInputBytes)
                    throw new InvalidDataException("候选 JSON 超过资源上限。");
                copy[key] = CloneBounded(value, errors, ref budget, ref count, depth + 1);
            }
            return copy;
        }
        if (node is JsonArray array)
        {
            if (array.Count > MaxRows) throw new InvalidDataException("候选数组超过资源上限。");
            var copy = new JsonArray();
            foreach (var value in array) copy.Add(CloneBounded(value, errors, ref budget, ref count, depth + 1));
            return copy;
        }
        if (node is JsonValue scalar && scalar.TryGetValue<string>(out var valueText))
        {
            try { budget += StrictUtf8.GetByteCount(valueText); }
            catch (EncoderFallbackException)
            {
                AddError(errors, "候选文本包含非法 Unicode，须修正后再导入。");
                return null;
            }
            if (budget > MaxInputBytes) throw new InvalidDataException("候选 JSON 超过 20 MiB 上限。");
            return JsonValue.Create(BoundCell(valueText, errors));
        }
        if (node is JsonValue otherScalar)
        {
            var raw = otherScalar.ToJsonString();
            budget += StrictUtf8.GetByteCount(raw);
            if (raw.Length > MaxCellCharacters)
            {
                AddError(errors, "JSON 单值超过 8000 字符上限，须修正后再导入。");
                if (budget > MaxInputBytes) throw new InvalidDataException("候选 JSON 超过 20 MiB 上限。");
                return null;
            }
        }
        if (budget > MaxInputBytes) throw new InvalidDataException("候选 JSON 超过 20 MiB 上限。");
        return node?.DeepClone();
    }

    private static void FlattenReflection(JsonObject values)
    {
        if (values["reflection"] is not JsonObject reflection) return;
        if (!values.ContainsKey("reflection_text")) values["reflection_text"] = reflection["text"]?.DeepClone();
        if (!values.ContainsKey("reflection_mood")) values["reflection_mood"] = reflection["mood"]?.DeepClone();
    }

    private static void FlattenImportMetadata(JsonObject values)
    {
        if (values["import_metadata"] is not JsonObject metadata) return;
        foreach (var field in new[] { "source_name", "source_recorded_at", "source_recorded_at_utc", "mentor_confirmed" })
            if (!values.ContainsKey(field) && metadata.ContainsKey(field)) values[field] = metadata[field]?.DeepClone();
    }

    private sealed record Cell(string Text, bool Quoted = false);
    private sealed record TableRow(int Number, IReadOnlyList<Cell> Cells, IReadOnlyList<string> Errors);

    private static IReadOnlyList<ImportSourceRow> ParsePastedTable(string text,
        IReadOnlyDictionary<string, string>? mapping)
    {
        // 剪贴板表格一般为 TSV；只检查首个逻辑记录中的非引号分隔符。
        var quoted = false;
        var delimiter = ',';
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"') i++;
                else quoted = !quoted;
            }
            else if (!quoted && text[i] == '\t') { delimiter = '\t'; break; }
            else if (!quoted && text[i] is '\r' or '\n') break;
        }
        return ParseTable(text, delimiter, mapping, nativeCsv: false);
    }

    private static IReadOnlyList<ImportSourceRow> ParseTable(string text, char delimiter,
        IReadOnlyDictionary<string, string>? mapping, bool nativeCsv)
    {
        var records = ReadDelimited(text, delimiter);
        if (records.Count == 0) return Array.Empty<ImportSourceRow>();
        return ProjectTable(records, mapping, nativeCsv);
    }

    private static IReadOnlyList<TableRow> ReadDelimited(string text, char delimiter)
    {
        var records = new List<TableRow>();
        var cells = new List<Cell>();
        var errors = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        var wasQuoted = false;
        var afterQuote = false;
        var recordStarted = false;
        var line = 1;
        var recordLine = 1;

        void AddCharacter(char character)
        {
            if (value.Length < MaxCellCharacters) value.Append(character);
            else AddError(errors, "单元格超过 8000 字符上限，须修正后再导入。");
        }
        void EndCell()
        {
            if (cells.Count >= MaxColumns) throw new InvalidDataException($"第 {recordLine} 行超过 200 列上限。");
            cells.Add(new Cell(value.ToString(), wasQuoted));
            value.Clear(); wasQuoted = false; afterQuote = false;
        }
        void EndRecord()
        {
            EndCell();
            if (cells.Any(cell => cell.Text.Length != 0) || errors.Count != 0)
            {
                if (records.Count >= MaxRows + 1) throw new InvalidDataException("表格超过 5000 数据行上限。");
                records.Add(new TableRow(recordLine, cells.ToArray(), errors.ToArray()));
            }
            cells.Clear(); errors.Clear(); recordStarted = false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];
            recordStarted = true;
            if (quoted)
            {
                if (character == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { AddCharacter('"'); i++; }
                    else { quoted = false; afterQuote = true; }
                }
                else
                {
                    AddCharacter(character);
                    if (character == '\n' || character == '\r' && (i + 1 == text.Length || text[i + 1] != '\n')) line++;
                }
                continue;
            }
            if (character == delimiter) { EndCell(); continue; }
            if (character is '\r' or '\n')
            {
                EndRecord();
                if (character == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                line++; recordLine = line;
                continue;
            }
            if (character == '"' && value.Length == 0 && !afterQuote) { quoted = true; wasQuoted = true; continue; }
            if (character == '"' || afterQuote) AddError(errors, "引号或引号后的内容不符合 CSV/TSV 格式。");
            AddCharacter(character);
        }
        if (quoted) AddError(errors, "引号单元格未闭合。");
        if (recordStarted || cells.Count > 0 || value.Length > 0 || errors.Count > 0) EndRecord();
        return records;
    }

    private static IReadOnlyList<ImportSourceRow> ProjectTable(IReadOnlyList<TableRow> records,
        IReadOnlyDictionary<string, string>? mapping, bool nativeCsv)
    {
        var header = records[0];
        if (header.Errors.Count != 0) throw new InvalidDataException("表头格式错误：" + string.Join("；", header.Errors));
        if (mapping is not null && mapping.Any(pair => !Fields.Contains(pair.Value)))
            throw new InvalidDataException("列映射包含未知目标字段。");
        var headers = header.Cells.Select(cell => cell.Text.Trim().TrimStart('\uFEFF')).ToArray();
        var native = nativeCsv && mapping is null && headers.Length == RunExporter.CsvHeader.Count &&
            header.Cells.Select(cell => cell.Text).ToHashSet(StringComparer.Ordinal).SetEquals(RunExporter.CsvHeader);
        var fields = headers.Select(name => ResolveHeader(name, mapping)).ToArray();
        if (fields.All(field => field is null)) throw new InvalidDataException("表格没有可识别的记录列，请提供列映射。");
        var duplicates = fields.Where(field => field is not null).GroupBy(field => field, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        var output = new List<ImportSourceRow>(Math.Max(0, records.Count - 1));
        foreach (var record in records.Skip(1))
        {
            if (output.Count >= MaxRows) throw new InvalidDataException("表格超过 5000 数据行上限。");
            var errors = record.Errors.ToList();
            if (record.Cells.Count != headers.Length) errors.Add("数据列数与表头不一致。");
            if (duplicates.Length != 0) errors.Add("多个来源列映射到同一字段：" + string.Join("、", duplicates));
            var values = new JsonObject();
            for (var i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                if (field is null || values.ContainsKey(field)) continue;
                var cell = i < record.Cells.Count ? record.Cells[i] : new Cell(string.Empty);
                var value = cell.Text;
                // Exporter 只在四个自由文本列、危险首字符前加 apostrophe，并总是使用引号。
                // 未引号的真实 "'=..." 和 "''=..." 不剥离。原本就以 "'=..." 开头且因逗号/换行
                // 被引号包裹的文本与保护前缀在原格式中无法区别；此有损歧义仅限完整原生表头。
                if (native && cell.Quoted && (field is "duty_name" or "duty_category" or "job_name" or "reflection_text") &&
                    value.Length > 1 && value[0] == '\'' && "=+-@\t\r".Contains(value[1]))
                    value = value[1..];
                values[field] = value;
            }
            output.Add(new ImportSourceRow(record.Number, values, errors));
        }
        return output;
    }

    private static string? ResolveHeader(string header, IReadOnlyDictionary<string, string>? mapping)
    {
        if (mapping is not null && mapping.TryGetValue(header, out var mapped)) return mapped;
        if (Fields.Contains(header)) return header;
        return HeaderAliases.GetValueOrDefault(header);
    }

    private static IReadOnlyList<ImportSourceRow> ParseWorkbook(string path, IReadOnlyDictionary<string, string>? mapping)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaxInputBytes) throw new InvalidDataException("来源文件超过 20 MiB 上限。");
        try
        {
            using var zip = new ZipArchive(file, ZipArchiveMode.Read);
            if (zip.Entries.Count > 1024) throw new InvalidDataException("XLSX 包含过多 ZIP 部件。");
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            long inflated = 0;
            foreach (var entry in zip.Entries)
            {
                inflated = checked(inflated + entry.Length);
                if (inflated > MaxInflatedZipBytes) throw new InvalidDataException("XLSX 解压大小超过 64 MiB 上限。");
                if (!entries.TryAdd(entry.FullName, entry)) throw new InvalidDataException("XLSX 包含重名 ZIP 部件。");
                if (entry.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("不支持包含宏的工作簿，请先另存为无宏 XLSX。");
            }
            var contentTypes = LoadXml(entries, "[Content_Types].xml", required: true)!;
            if (contentTypes.Descendants().Attributes().Any(attribute => attribute.Value.Contains("macroEnabled", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("不支持包含宏的工作簿。");
            var workbook = LoadXml(entries, "xl/workbook.xml", required: true)!;
            var date1904Value = workbook.Descendants().FirstOrDefault(element => element.Name.LocalName == "workbookPr")?.Attribute("date1904")?.Value;
            if (date1904Value is not (null or "0" or "1" or "false" or "true"))
                throw new InvalidDataException("XLSX 日期系统标记无效。");
            var date1904 = date1904Value is "1" or "true";
            var sheets = workbook.Descendants().Where(element => element.Name.LocalName == "sheet");
            var sheet = sheets.FirstOrDefault(element => element.Attribute("state")?.Value is not ("hidden" or "veryHidden"));
            if (sheet is null) throw new InvalidDataException("XLSX 没有可见工作表。");
            var relationshipId = sheet.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "id")?.Value;
            if (string.IsNullOrEmpty(relationshipId)) throw new InvalidDataException("XLSX 工作表缺少关系编号。");
            var relations = LoadXml(entries, "xl/_rels/workbook.xml.rels", required: true)!;
            var relation = relations.Descendants().FirstOrDefault(element => element.Name.LocalName == "Relationship" &&
                element.Attribute("Id")?.Value == relationshipId);
            if (relation is null || relation.Attribute("TargetMode")?.Value == "External" ||
                relation.Attribute("Type")?.Value.EndsWith("/worksheet", StringComparison.Ordinal) != true)
                throw new InvalidDataException("XLSX 工作表关系无效或指向外部资源。");
            var worksheetPath = ResolveZipTarget("xl", relation.Attribute("Target")?.Value);
            var strings = ReadSharedStrings(LoadXml(entries, "xl/sharedStrings.xml", required: false));
            var dateStyles = ReadDateStyles(LoadXml(entries, "xl/styles.xml", required: false));
            var worksheet = LoadXml(entries, worksheetPath, required: true)!;
            var records = new List<TableRow>();
            var previousRow = 0;
            long expandedTextBytes = 0;
            var worksheetRows = 0;
            foreach (var row in worksheet.Descendants().Where(element => element.Name.LocalName == "row"))
            {
                if (++worksheetRows > MaxRows + 1) throw new InvalidDataException("工作表超过 5000 数据行上限（含格式空行）。");
                var number = row.Attribute("r") is { } rowAttribute && int.TryParse(rowAttribute.Value, out var parsedRow) ? parsedRow : previousRow + 1;
                if (number <= previousRow || number > 1_048_576) throw new InvalidDataException("工作表行号无效或顺序错误。");
                previousRow = number;
                var cells = new Dictionary<int, Cell>();
                var errors = new List<string>();
                var previousColumn = -1;
                var worksheetCells = 0;
                foreach (var element in row.Elements().Where(element => element.Name.LocalName == "c"))
                {
                    if (++worksheetCells > MaxColumns) throw new InvalidDataException($"工作表第 {number} 行超过 200 个单元格上限。");
                    var reference = element.Attribute("r")?.Value;
                    var column = reference is null ? previousColumn + 1 : ColumnIndex(reference, number);
                    if (column < 0) { errors.Add("单元格坐标无效。"); continue; }
                    if (column >= MaxColumns) throw new InvalidDataException($"工作表第 {number} 行超过 200 列上限。");
                    previousColumn = column;
                    var value = ReadExcelCell(element, strings, dateStyles, date1904, errors);
                    // 一个短共享字符串索引可以重复扩张为很多长文本；限制候选总量而非仅 ZIP 部件。
                    expandedTextBytes += StrictUtf8.GetByteCount(value);
                    if (expandedTextBytes > MaxInputBytes) throw new InvalidDataException("工作表展开文本超过 20 MiB 上限。");
                    if (!cells.TryAdd(column, new Cell(value))) errors.Add("工作表含重复单元格坐标。");
                }
                if (cells.Count == 0) continue;
                var maxColumn = cells.Keys.Max();
                var ordered = Enumerable.Range(0, maxColumn + 1).Select(index => cells.GetValueOrDefault(index, new Cell(string.Empty))).ToArray();
                if (ordered.Any(cell => cell.Text.Length != 0) || errors.Count != 0)
                    records.Add(new TableRow(number, ordered, errors));
            }
            if (records.Count == 0) return Array.Empty<ImportSourceRow>();
            // XLSX 不保存尾部空单元格；稀疏行补为空值，不把正常空列报成损坏的 CSV 行。
            var headerWidth = records[0].Cells.Count;
            var padded = records.Select((record, index) => index == 0 || record.Cells.Count >= headerWidth ? record :
                record with { Cells = record.Cells.Concat(Enumerable.Repeat(new Cell(string.Empty), headerWidth - record.Cells.Count)).ToArray() }).ToArray();
            return ProjectTable(padded, mapping, nativeCsv: false);
        }
        catch (Exception error) when (error is XmlException or OverflowException or ArgumentOutOfRangeException or RegexMatchTimeoutException)
        {
            throw new InvalidDataException("XLSX ZIP/XML 结构损坏或超过资源上限。", error);
        }
    }

    private static XDocument? LoadXml(Dictionary<string, ZipArchiveEntry> entries, string name, bool required)
    {
        if (!entries.TryGetValue(name, out var entry))
        {
            if (required) throw new InvalidDataException("XLSX 缺少必要部件：" + name);
            return null;
        }
        if (entry.Length > MaxXmlCharacters) throw new InvalidDataException("XLSX XML 部件超过 16 MiB 上限。");
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = MaxXmlCharacters, MaxCharactersFromEntities = 0,
        });
        return XDocument.Load(reader);
    }

    private static string ResolveZipTarget(string directory, string? target)
    {
        if (string.IsNullOrWhiteSpace(target) || target.Contains('\\') || target.Contains('?') || target.Contains('#') || target.Contains(':'))
            throw new InvalidDataException("XLSX 工作表路径无效。");
        var path = new List<string>();
        foreach (var part in (target.StartsWith('/') ? target[1..] : directory + "/" + target).Split('/'))
        {
            if (part is "." or "") continue;
            if (part == "..")
            {
                if (path.Count == 0) throw new InvalidDataException("XLSX 工作表路径越过 ZIP 根目录。");
                path.RemoveAt(path.Count - 1);
            }
            else path.Add(part);
        }
        return string.Join('/', path);
    }

    private static IReadOnlyList<string> ReadSharedStrings(XDocument? document)
    {
        if (document is null) return Array.Empty<string>();
        var output = new List<string>();
        foreach (var item in document.Descendants().Where(element => element.Name.LocalName == "si"))
        {
            if (output.Count >= 100_000) throw new InvalidDataException("XLSX 共享字符串超过 100000 项上限。");
            output.Add(ExcelText(item));
        }
        return output;
    }

    private static string ExcelText(XElement item) => string.Concat(item.Descendants()
        .Where(element => element.Name.LocalName == "t" && !element.Ancestors().Any(ancestor => ancestor.Name.LocalName == "rPh"))
        .Select(element => element.Value));

    private enum ExcelTemporalKind { None, DateOnly, DateTime, TimeOnly, ElapsedTime, Ambiguous }

    private sealed record ExcelCellStyle(ExcelTemporalKind Positive, ExcelTemporalKind Zero);

    private static IReadOnlyList<ExcelCellStyle> ReadDateStyles(XDocument? document)
    {
        if (document is null) return Array.Empty<ExcelCellStyle>();
        var formats = new Dictionary<int, string>();
        foreach (var format in document.Descendants().Where(element => element.Name.LocalName == "numFmt"))
        {
            if (formats.Count >= 10_000) throw new InvalidDataException("XLSX 自定义格式超过资源上限。");
            if (int.TryParse(format.Attribute("numFmtId")?.Value, out var id))
                formats[id] = format.Attribute("formatCode")?.Value ?? string.Empty;
        }
        var styles = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "cellXfs")?.Elements()
            .Where(element => element.Name.LocalName == "xf").ToArray() ?? Array.Empty<XElement>();
        if (styles.Length > 10_000) throw new InvalidDataException("XLSX 格式超过资源上限。");
        return styles.Select(style =>
        {
            if (!int.TryParse(style.Attribute("numFmtId")?.Value, out var id))
                return new ExcelCellStyle(ExcelTemporalKind.None, ExcelTemporalKind.None);
            if (formats.TryGetValue(id, out var format))
            {
                // 颜色、区域和字面量不提供时分；[h]/[m]/[s] 表示累计耗时而不是日历日期。
                var conditional = Regex.IsMatch(format, @"\[[<>=]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                if (conditional) return new ExcelCellStyle(ExcelTemporalKind.Ambiguous, ExcelTemporalKind.Ambiguous);
                var stripped = Regex.Replace(format, "\"[^\"]*\"|\\\\.|[_*].|\\[[^\\]]*\\]", match =>
                    Regex.IsMatch(match.Value, @"^\[[hms]+\]$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(100)) ? "!" + match.Value[1..^1] : string.Empty,
                    RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                var sections = stripped.Split(';');
                return new ExcelCellStyle(ClassifyTemporalFormat(sections[0]),
                    ClassifyTemporalFormat(sections.Length >= 3 ? sections[2] : sections[0]));
            }
            // 34/35/52/53/55/56 随 Office UI 语言在纯日期和纯时间间变化，缺少 formatCode 时不猜。
            var kind = id switch
            {
                >= 14 and <= 17 or >= 27 and <= 31 or 36 or 50 or 51 or 54 or 57 or 58 => ExcelTemporalKind.DateOnly,
                22 => ExcelTemporalKind.DateTime,
                >= 18 and <= 21 or 32 or 33 or 45 or 47 => ExcelTemporalKind.TimeOnly,
                46 => ExcelTemporalKind.ElapsedTime,
                34 or 35 or 52 or 53 or 55 or 56 => ExcelTemporalKind.Ambiguous,
                _ => ExcelTemporalKind.None,
            };
            return new ExcelCellStyle(kind, kind);
        }).ToArray();
    }

    private static ExcelTemporalKind ClassifyTemporalFormat(string format)
    {
        var elapsed = format.Contains('!');
        var clockFormat = Regex.Replace(format, "AM/PM|A/P", string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        var tokens = Regex.Matches(clockFormat, "y+|m+|d+|h+|s+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        var hours = false;
        var minutes = false;
        var anyTime = elapsed;
        var date = false;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = char.ToLowerInvariant(tokens[i].Value[0]);
            if (token == 'h') { hours = true; anyTime = true; }
            else if (token == 's') anyTime = true;
            else if (token is 'y' or 'd') date = true;
            else if (token == 'm')
            {
                // m/mm 只有紧跟 h/hh 或紧邻 s/ss 时才是分钟；日期里的月份不能补出缺失分钟。
                var minute = tokens[i].Length <= 2 && (i > 0 && char.ToLowerInvariant(tokens[i - 1].Value[0]) == 'h' ||
                    i + 1 < tokens.Count && char.ToLowerInvariant(tokens[i + 1].Value[0]) == 's');
                if (minute) { minutes = true; anyTime = true; }
                else date = true;
            }
        }
        if (elapsed) return date ? ExcelTemporalKind.Ambiguous : ExcelTemporalKind.ElapsedTime;
        if (date && anyTime) return hours && minutes ? ExcelTemporalKind.DateTime : ExcelTemporalKind.Ambiguous;
        if (date) return ExcelTemporalKind.DateOnly;
        return anyTime ? ExcelTemporalKind.TimeOnly : ExcelTemporalKind.None;
    }

    private static int ColumnIndex(string reference, int row)
    {
        var index = 0;
        var position = 0;
        while (position < reference.Length && reference[position] is >= 'A' and <= 'Z')
        {
            if (index > MaxColumns) return MaxColumns;
            index = index * 26 + reference[position++] - 'A' + 1;
        }
        return index != 0 && int.TryParse(reference.AsSpan(position), out var referencedRow) && referencedRow == row ? index - 1 : -1;
    }

    private static string ReadExcelCell(XElement cell, IReadOnlyList<string> strings, IReadOnlyList<ExcelCellStyle> dateStyles,
        bool date1904, List<string> errors)
    {
        if (cell.Elements().Any(element => element.Name.LocalName == "f"))
        {
            errors.Add("公式单元格未导入；请转为明确的文本或数值。");
            return string.Empty;
        }
        var type = cell.Attribute("t")?.Value;
        var raw = cell.Elements().FirstOrDefault(element => element.Name.LocalName == "v")?.Value ?? string.Empty;
        if (type == "inlineStr")
            return ExcelString(ExcelText(cell), errors);
        if (type == "s")
        {
            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0 || index >= strings.Count)
            { errors.Add("共享字符串索引无效。"); return string.Empty; }
            return ExcelString(strings[index], errors);
        }
        if (type == "e") { errors.Add("工作表单元格包含错误值。 "); return string.Empty; }
        if (type is not (null or "n" or "b" or "d" or "str")) errors.Add("工作表单元格类型不受支持。");
        if (type == "b" && raw is not ("0" or "1")) errors.Add("工作表布尔单元格无效。");
        var dateStyle = new ExcelCellStyle(ExcelTemporalKind.None, ExcelTemporalKind.None);
        if (cell.Attribute("s") is { } style)
        {
            if (!int.TryParse(style.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0 || index >= dateStyles.Count)
                errors.Add("工作表格式索引无效。");
            else dateStyle = dateStyles[index];
        }
        if ((dateStyle.Positive != ExcelTemporalKind.None || dateStyle.Zero != ExcelTemporalKind.None) && (type is null or "n") && raw.Length != 0)
        {
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) || !double.IsFinite(serial) || serial < 0)
                errors.Add("Excel 日期或时间数值无效。");
            else
            {
                try
                {
                    var kind = serial == 0 ? dateStyle.Zero : dateStyle.Positive;
                    if (kind == ExcelTemporalKind.None) return BoundCell(raw, errors);
                    if (kind == ExcelTemporalKind.Ambiguous)
                    {
                        errors.Add("工作表日期/时间格式不明确；请补充完整格式后导入。");
                        return BoundCell(raw, errors);
                    }
                    if (kind == ExcelTemporalKind.ElapsedTime)
                    {
                        var elapsed = TimeSpan.FromDays(serial);
                        return Math.Floor(elapsed.TotalHours).ToString(CultureInfo.InvariantCulture) + elapsed.ToString(@"\:mm\:ss\.fff", CultureInfo.InvariantCulture);
                    }
                    if (!date1904 && (serial is >= 60 and < 61) && (kind is ExcelTemporalKind.DateOnly or ExcelTemporalKind.DateTime))
                    {
                        errors.Add("Excel 日期无效（包括不存在的 1900-02-29）。");
                        return BoundCell(raw, errors);
                    }
                    var origin = date1904 ? new DateTime(1904, 1, 1) : new DateTime(1899, 12, serial < 60 ? 31 : 30);
                    var date = origin.AddDays(serial);
                    // 只有真实格式同时显示日历日期和时分才形成完整时间，不能从 date-only 补午夜或 time-only 补日期。
                    return date.ToString(kind switch
                    {
                        ExcelTemporalKind.DateOnly => "yyyy-MM-dd",
                        ExcelTemporalKind.TimeOnly => "H:mm:ss.fff",
                        _ => "yyyy-MM-dd'T'HH:mm:ss.fff",
                    }, CultureInfo.InvariantCulture);
                }
                catch (Exception error) when (error is ArgumentOutOfRangeException or OverflowException)
                { errors.Add("Excel 日期或时间超过支持范围。"); }
            }
        }
        return type == "str" ? ExcelString(raw, errors) : BoundCell(raw, errors);
    }

    private static string ExcelString(string raw, List<string> errors)
    {
        // OOXML 使用 _xNNNN_ 存储 CR 等字符；只替换一遍，使 _x005F_x000A_ 保持字面 _x000A_。
        var text = Regex.Replace(raw, "_x([0-9A-Fa-f]{4})_", match =>
            ((char)int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString(),
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        try { _ = StrictUtf8.GetByteCount(text); }
        catch (EncoderFallbackException)
        {
            AddError(errors, "工作表文本包含非法 Unicode 转义。");
            return BoundCell(raw, errors);
        }
        return BoundCell(text, errors);
    }

    private static IReadOnlyList<ImportSourceRow> ParseBackup(string path)
    {
        // 软件备份是独立快照。immutable 防止 SQLite 创建 sidecar，也不读取活动数据库的 WAL。
        if (new FileInfo(path + "-wal") is { Exists: true, Length: > 0 })
            throw new InvalidDataException("请导入独立的软件备份，不能导入仍有 WAL 的活动数据库。");
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = new Uri(path).AbsoluteUri + "?immutable=1", Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private, Pooling = false, DefaultTimeout = 3,
            };
            using var connection = new SqliteConnection(builder.ConnectionString);
            connection.Open();
            using (var safety = connection.CreateCommand())
            {
                safety.CommandText = "PRAGMA query_only=ON; PRAGMA trusted_schema=OFF;";
                safety.ExecuteNonQuery();
            }
            var columns = BackupColumns(connection, "mentor_runs", required: true);
            var required = new[] { "run_id", "region", "content_id", "duty_name", "job_id", "job_name", "role",
                "matched_at_utc", "entered_at_utc", "ended_at_utc", "duration_ms", "result", "source", "soft_deleted", "created_at_utc", "updated_at_utc" };
            if (required.Any(column => !columns.Contains(column))) throw new InvalidDataException("数据库不是已知软件记录备份结构。");
            var reflectionColumns = BackupColumns(connection, "run_reflections", required: false);
            if (reflectionColumns.Count != 0 && new[] { "run_id", "mood", "text" }.Any(column => !reflectionColumns.Contains(column)))
                throw new InvalidDataException("备份心得结构无效。");
            var metadataColumns = BackupColumns(connection, "run_import_metadata", required: false);
            var metadataFields = new[] { "source_name", "source_recorded_at", "source_recorded_at_utc", "mentor_confirmed" };
            if (metadataColumns.Count != 0 && metadataFields.Append("run_id").Any(column => !metadataColumns.Contains(column)))
                throw new InvalidDataException("备份导入来源结构无效。");
            var selected = columns.Where(column => Fields.Contains(column) && column != "reflection").Order(StringComparer.Ordinal).ToArray();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT " + string.Join(',', selected.Select(column => "r.\"" + column + "\"")) +
                (reflectionColumns.Count == 0 ? string.Empty : ", f.text AS reflection_text, f.mood AS reflection_mood") +
                (metadataColumns.Count == 0 ? string.Empty : ", " + string.Join(',', metadataFields.Select(column => "m.\"" + column + "\" AS \"" + column + "\""))) +
                " FROM mentor_runs r" + (reflectionColumns.Count == 0 ? string.Empty : " LEFT JOIN run_reflections f ON f.run_id = r.run_id") +
                (metadataColumns.Count == 0 ? string.Empty : " LEFT JOIN run_import_metadata m ON m.run_id = r.run_id") +
                " ORDER BY r.run_id LIMIT 5001;";
            var output = new List<ImportSourceRow>();
            long projectedTextBytes = 0;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (output.Count >= MaxRows) throw new InvalidDataException("备份超过 5000 条记录上限。");
                var errors = new List<string>();
                var values = new JsonObject();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.GetValue(i);
                    if (value is string textValue)
                    {
                        projectedTextBytes += StrictUtf8.GetByteCount(textValue);
                        if (projectedTextBytes > MaxInputBytes) throw new InvalidDataException("备份展开文本超过 20 MiB 上限。");
                    }
                    values[reader.GetName(i)] = value switch
                    {
                        DBNull => null, long integer => JsonValue.Create(integer), double number => JsonValue.Create(number),
                        string valueText => JsonValue.Create(BoundCell(valueText, errors)),
                        _ => UnknownBackupValue(errors),
                    };
                }
                output.Add(new ImportSourceRow(output.Count + 1, values, errors));
            }
            return output;
        }
        catch (SqliteException error) { throw new InvalidDataException("无法只读解析软件数据库备份。", error); }
    }

    private static HashSet<string> BackupColumns(SqliteConnection connection, string table, bool required)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type FROM sqlite_master WHERE name = $name;";
        command.Parameters.AddWithValue("$name", table);
        var type = command.ExecuteScalar() as string;
        if (type is null && !required) return new HashSet<string>(StringComparer.Ordinal);
        if (type != "table") throw new InvalidDataException("备份缺少真实记录表：" + table);
        // table 仅由本类的两个固定字面量传入，外部文件名和表头不会进入 SQL。
        command.CommandText = "PRAGMA table_info(\"" + table + "\");";
        using var reader = command.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (columns.Count >= MaxColumns) throw new InvalidDataException("备份表超过 200 列上限。");
            columns.Add(reader.GetString(1));
        }
        return columns;
    }

    private static JsonNode? UnknownBackupValue(List<string> errors)
    {
        AddError(errors, "备份记录包含不支持的字段存储类型。");
        return null;
    }

    private static string BoundCell(string value, List<string> errors)
    {
        if (value.Length <= MaxCellCharacters) return value;
        AddError(errors, "单元格超过 8000 字符上限，须修正后再导入。");
        var end = MaxCellCharacters;
        if (char.IsHighSurrogate(value[end - 1]) && char.IsLowSurrogate(value[end])) end--;
        return value[..end];
    }

    private static void AddError(List<string> errors, string message)
    {
        if (!errors.Contains(message, StringComparer.Ordinal)) errors.Add(message);
    }
}
