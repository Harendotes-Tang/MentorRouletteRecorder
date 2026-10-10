using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MentorRecorder.Collector.Application.Mutations;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Reference;

namespace MentorRecorder.Collector.Import;

internal sealed record ImportCandidate(int RowNumber, JsonObject Input, MentorRun? Run,
    bool HasStableId, IReadOnlySet<string> ComparedFields, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

/// <summary>Turns external cells into truthful IMPORT facts; source timestamps remain separate.</summary>
internal static class RunImportCandidateNormalizer
{
    public static ImportCandidate Normalize(ImportSourceRow row, string sourceKind, string? sourceName,
        string? timeZone, DateTimeOffset now)
    {
        var input = (JsonObject)row.Values.DeepClone();
        var errors = row.Errors.ToList();
        var warnings = new List<string>();
        var comparedFields = input.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        if (input["reflection"] is JsonObject)
            comparedFields.UnionWith(new[] { "reflection_text", "reflection_mood" });
        if (input["import_metadata"] is JsonObject)
            comparedFields.Add("source_recorded_at_utc");
        if (input.ContainsKey("source_recorded_at")) comparedFields.Add("source_recorded_at_utc");
        try
        {
            var originalId = Text(input, "run_id", 64);
            var stable = !string.IsNullOrEmpty(originalId);
            if (stable && !Guid.TryParseExact(originalId, "D", out _))
                throw Bad("run_id 必须是标准 UUID。");
            var id = stable ? Guid.Parse(originalId!).ToString("D") : Guid.NewGuid().ToString("D");
            var importedMetadata = input["import_metadata"] as JsonObject;
            var recordSourceName = Text(input, "source_name", 500)
                ?? (importedMetadata is null ? null : Text(importedMetadata, "source_name", 500)) ?? sourceName;
            var sourceRecorded = input.ContainsKey("source_recorded_at") ? Text(input, "source_recorded_at", 200)
                : importedMetadata is null ? null : Text(importedMetadata, "source_recorded_at", 200);
            var clearedSource = input.ContainsKey("source_recorded_at") && sourceRecorded is null
                && (importedMetadata is null || Text(importedMetadata, "source_recorded_at", 200) is not null);
            var sourceUtcText = clearedSource ? null : input.ContainsKey("source_recorded_at_utc")
                ? Text(input, "source_recorded_at_utc", 100)
                : importedMetadata is null ? null : Text(importedMetadata, "source_recorded_at_utc", 100);
            // An editable raw timestamp with an explicit zone owns its derived UTC cache.
            // Native exports without a zone retain their separately stored UTC provenance.
            var unchangedStoredSource = importedMetadata is not null && sourceUtcText is not null
                && string.Equals(sourceRecorded, Text(importedMetadata, "source_recorded_at", 200), StringComparison.Ordinal)
                && string.Equals(sourceUtcText, Text(importedMetadata, "source_recorded_at_utc", 100), StringComparison.Ordinal);
            var parseRawSource = !unchangedStoredSource && sourceRecorded is not null && (!string.IsNullOrWhiteSpace(timeZone)
                || Regex.IsMatch(sourceRecorded, @"(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant));
            var sourceDateOnly = sourceRecorded is not null && DateTime.TryParseExact(sourceRecorded,
                new[] { "yyyy-MM-dd", "yyyy/M/d" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
            var sourceUtc = sourceDateOnly ? null : ParseTime(parseRawSource ? sourceRecorded : sourceUtcText ?? sourceRecorded, timeZone, "原站记录时间");
            if (sourceDateOnly) warnings.Add("原站只提供日期，保留原文；具体时分和 UTC 时间未知。");
            if (!sourceDateOnly && parseRawSource && sourceUtcText is not null
                && !string.Equals(sourceUtcText, UtcTimestamp.ToTextOrNull(sourceUtc), StringComparison.Ordinal))
                warnings.Add("原站时间已修改，按明确来源时区重新计算 UTC 时间。");
            var entered = ParseTime(Text(input, "entered_at_utc", 100), timeZone, "进本时间");
            var ended = ParseTime(Text(input, "ended_at_utc", 100), timeZone, "结束时间");
            var matched = ParseTime(Text(input, "matched_at_utc", 100), timeZone, "匹配时间");
            var contentId = Number(input, "content_id", int.MaxValue);
            var jobId = Number(input, "job_id", int.MaxValue);
            var jobName = Text(input, "job_name", 200);
            if (jobId is null && jobName is not null)
            {
                var candidates = JobCatalog.Default.Jobs.Where(job =>
                    string.Equals(job.NameZh, jobName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(job.NameEn, jobName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(job.Abbreviation, jobName, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (candidates.Length == 1) jobId = candidates[0].JobId;
                else warnings.Add("职业名称未唯一匹配；保留原文，职业编号未知。");
            }
            var dutyName = Text(input, "duty_name", 500);
            var region = ParseEnum(input, "region", Region.Unknown);
            if (contentId is null && dutyName is not null)
            {
                var matches = DutyCatalog.Default.Documents.SelectMany(document => document.Rows.Values)
                    .Where(duty => string.Equals(duty.LocalizedName, dutyName, StringComparison.Ordinal))
                    .Select(duty => duty.ContentId).Distinct().ToArray();
                if (matches.Length == 1) contentId = matches[0];
            }
            var duty = DutyCatalog.Default.Find(contentId is null ? null : (int)contentId, region);
            var job = JobCatalog.Default.Find(jobId is null ? null : (int)jobId);
            var result = ParseResult(input);
            if (Text(input, "result", 64) is null)
                warnings.Add("来源未提供结果，按默认通关，可修改。");
            var duration = Number(input, "duration_ms", long.MaxValue);
            var text = Text(input, "reflection_text", ReflectionText.MaxLength);
            var moodText = Text(input, "reflection_mood", 32);
            var reflectionInput = input["reflection"] as JsonObject;
            if (!input.ContainsKey("reflection_text"))
                text = reflectionInput is null ? null : Text(reflectionInput, "text", ReflectionText.MaxLength);
            if (!input.ContainsKey("reflection_mood"))
                moodText = reflectionInput is null ? null : Text(reflectionInput, "mood", 32);
            var mood = ReflectionMood.Unknown;
            if (moodText is not null && !ReflectionText.TryParse(moodText, out mood))
                throw Bad("心得心情只能是 good、ok、bad 或 unknown。");
            var metadata = new RunImportMetadata(sourceKind, recordSourceName, sourceRecorded, sourceUtc,
                now, string.Empty, Flag(input, "mentor_confirmed") ?? true);
            var run = new MentorRun
            {
                RunId = id, Revision = 1, Source = RunSource.Import, Region = region,
                MentorRouletteId = Int(input, "mentor_roulette_id"),
                ContentId = contentId is null ? null : (int)contentId,
                TerritoryId = Int(input, "territory_id") ?? duty?.TerritoryId,
                DutyName = dutyName ?? duty?.LocalizedName,
                DutyCategory = Text(input, "duty_category", 200) ?? duty?.DutyCategory,
                DutySource = contentId is not null ? DutySource.Manual : null,
                JobId = jobId is null ? null : (int)jobId,
                JobName = jobName ?? job?.NameZh ?? JobCatalog.UnknownJobName,
                Role = job?.Role ?? Role.Unknown,
                MatchedAtUtc = matched, EnteredAtUtc = entered, EndedAtUtc = ended,
                DurationMs = duration, Result = result,
                ContributesToGoal = Flag(input, "contributes_to_goal") ?? true,
                SoftDeleted = Flag(input, "soft_deleted") ?? false,
                PendingReview = Flag(input, "pending_review") ?? false,
                Note = Text(input, "note", 1000),
                ImportMetadata = metadata,
                Reflection = text is null ? null : new RunReflection(mood, text, now, now),
                CreatedAtUtc = ParseTime(Text(input, "created_at_utc", 100), timeZone, "软件记录创建时间") ?? now,
                UpdatedAtUtc = now,
            };
            if (run.ContentId is null && run.DutyName is null && run.JobId is null
                && (jobName is null || jobName == JobCatalog.UnknownJobName)
                && matched is null && entered is null && ended is null && sourceRecorded is null && sourceUtc is null
                && text is null && run.Note is null)
                throw Bad("该行没有副本、职业、游戏时间、原站时间或心得内容，请映射或补充至少一项事实。");
            if (run.Result == RunResult.Unknown || (run.Result != RunResult.CancelledBeforeEntry
                && (run.EnteredAtUtc is null || (run.Result == RunResult.Completed && run.EndedAtUtc is null))))
                run = run with { PendingReview = true };
            run = RunMutationValidation.ValidateFinalValue(run, input.ContainsKey("duration_ms"));
            if (run.IsIncompleteImport) warnings.Add("实际游戏时间或结果未补齐；保留为待补充历史，不计统计或成就。");
            if (text is not null && mood == ReflectionMood.Unknown) warnings.Add("原来源没有记录心情，保留为未记录心情。");
            if (input["warnings"] is JsonArray sourceWarnings)
                warnings.AddRange(sourceWarnings.OfType<JsonValue>().Select(value => value.ToString()).Take(20));
            var signature = Facts(run);
            var key = Text(input, "source_key", 500);
            var fingerprint = Hash(string.Join('\n', sourceKind, recordSourceName ?? string.Empty,
                stable ? id : key ?? string.Empty, signature));
            run = run with { ImportMetadata = metadata with { SourceFingerprint = fingerprint } };
            // Canonical editable values; generated local UUID stays absent when the source had none.
            var canonical = Wire.Run(run);
            // Keep absent source fields absent: a CSV export cannot conflict over JSON-only facts.
            foreach (var field in input.Select(pair => pair.Key).ToArray())
                if (canonical.ContainsKey(field)) input[field] = canonical[field]?.DeepClone();
            input["run_id"] = stable ? id : null;
            if (sourceRecorded is not null) input["source_recorded_at"] = sourceRecorded;
            if (sourceUtc is not null || clearedSource || sourceDateOnly) input["source_recorded_at_utc"] = UtcTimestamp.ToTextOrNull(sourceUtc);
            input["source_key"] = key;
            if (text is not null)
            {
                input["reflection_text"] = text;
                if (moodText is not null) input["reflection_mood"] = ReflectionText.Format(mood);
            }
            return new ImportCandidate(row.RowNumber, input, run, stable, comparedFields, errors, warnings);
        }
        catch (Exception failure) when (failure is CollectorException or FormatException or ArgumentException or InvalidOperationException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            errors.Add(failure.Message);
            return new ImportCandidate(row.RowNumber, input, null, false, comparedFields, errors, warnings);
        }
    }

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>Captured identity and audit counters are excluded; conflicting personal facts are retained.</summary>
    public static bool SameProvidedFacts(ImportCandidate incoming, MentorRun local)
    {
        var importedFacts = FactValues(incoming.Run!);
        var localFacts = FactValues(local);
        return incoming.ComparedFields.Where(importedFacts.ContainsKey).All(field =>
            JsonNode.DeepEquals(importedFacts[field], localFacts[field]));
    }

    public static string Facts(MentorRun run) => FactValues(run).ToJsonString(Wire.JsonOptions);

    private static JsonObject FactValues(MentorRun run) => new()
    {
        ["region"] = EnumWire<Region>.Format(run.Region), ["content_id"] = run.ContentId,
        ["mentor_roulette_id"] = run.MentorRouletteId,
        ["territory_id"] = run.TerritoryId, ["duty_name"] = run.DutyName, ["duty_category"] = run.DutyCategory,
        ["job_id"] = run.JobId, ["job_name"] = run.JobName ?? JobCatalog.UnknownJobName,
        ["matched_at_utc"] = UtcTimestamp.ToTextOrNull(run.MatchedAtUtc),
        ["entered_at_utc"] = UtcTimestamp.ToTextOrNull(run.EnteredAtUtc),
        ["ended_at_utc"] = UtcTimestamp.ToTextOrNull(run.EndedAtUtc), ["duration_ms"] = run.DurationMs,
        ["result"] = EnumWire<RunResult>.Format(run.Result), ["contributes_to_goal"] = run.ContributesToGoal,
        ["soft_deleted"] = run.SoftDeleted, ["note"] = run.Note,
        ["reflection_text"] = run.Reflection?.Text,
        ["reflection_mood"] = run.Reflection is null ? null : ReflectionText.Format(run.Reflection.Mood),
        ["source_recorded_at"] = run.ImportMetadata?.SourceRecordedAt,
        ["source_recorded_at_utc"] = UtcTimestamp.ToTextOrNull(run.ImportMetadata?.SourceRecordedAtUtc),
    };

    private static string? Text(JsonObject input, string key, int max)
    {
        if (input[key] is null) return null;
        if (input[key] is not JsonValue value) throw Bad(key + " 必须是文本或数字。");
        var text = value.TryGetValue<string>(out var raw) ? raw.Trim() : value.ToString();
        if (text.Length > max) throw Bad(key + " 超过 " + max + " 字符。");
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static long? Number(JsonObject input, string key, long max)
    {
        var text = Text(input, key, 100);
        if (text is null) return null;
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 0 || value > max)
            throw Bad(key + " 必须是范围内的非负整数；耗时单位为毫秒，含冒号的时间须先明确单位。");
        return value;
    }
    private static int? Int(JsonObject input, string key) => Number(input, key, int.MaxValue) is { } value ? (int)value : null;
    private static bool? Flag(JsonObject input, string key)
    {
        var text = Text(input, key, 20);
        return text?.ToLowerInvariant() switch
        {
            null => null, "true" or "1" or "是" => true, "false" or "0" or "否" => false,
            _ => throw Bad(key + " 必须为 true/false 或 1/0。"),
        };
    }
    private static T ParseEnum<T>(JsonObject input, string key, T fallback) where T : struct, Enum =>
        Text(input, key, 64) is { } text
            ? EnumWire<T>.TryParse(text, out var parsed) ? parsed : throw Bad(key + " 不是支持的枚举值。")
            : fallback;
    private static RunResult ParseResult(JsonObject input)
    {
        var text = Text(input, "result", 64);
        return text switch
        {
            "完成" or "通关" or "已完成" => RunResult.Completed,
            "未知" => RunResult.Unknown,
            null => RunResult.Completed,
            "离开" or "放弃" => RunResult.LeftOrAbandoned,
            "取消" => RunResult.CancelledBeforeEntry,
            "中断" => RunResult.Interrupted,
            "掉线" => RunResult.Disconnected,
            _ => EnumWire<RunResult>.TryParse(text, out var result) ? result : throw Bad("结果字段无法识别；不会从删除按钮或心得猜测通关。"),
        };
    }
    private static DateTimeOffset? ParseTime(string? text, string? zone, string field)
    {
        if (text is null) return null;
        if (!Regex.IsMatch(text, @"\d{1,2}:\d{2}", RegexOptions.CultureInvariant))
            throw Bad(field + " 缺少明确的时分，不能用日期虚构午夜。");
        if (Regex.IsMatch(text, @"(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant))
        {
            // TryParse fills missing calendar fields from the host date. Require the same
            // complete numeric date as local imports before accepting an explicit offset.
            if (!Regex.IsMatch(text, @"^\d{4}[-/]\d{1,2}[-/]\d{1,2}(?:T|\s)", RegexOptions.CultureInvariant))
                throw Bad(field + " 缺少完整日期，请在预览中补充年月日与时间。");
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var offsetTime))
                return UtcTimestamp.Truncate(offsetTime);
        }
        var formats = new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy/M/d H:mm:ss", "yyyy/M/d H:mm",
            "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF", "yyyy-MM-ddTHH:mm" };
        if (!DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            throw Bad(field + " 格式不明确，请在预览中修正完整日期与时间。");
        if (string.IsNullOrWhiteSpace(zone)) throw Bad(field + " 是本地时间，需明确来源时区。");
        if (Regex.IsMatch(zone, @"^[+-]\d{2}:\d{2}$", RegexOptions.CultureInvariant))
        {
            var sign = zone[0] == '-' ? -1 : 1;
            var offset = sign * TimeSpan.ParseExact(zone[1..], @"hh\:mm", CultureInfo.InvariantCulture);
            return UtcTimestamp.Truncate(new DateTimeOffset(local, offset));
        }
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(zone);
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(local) || timeZone.IsAmbiguousTime(local))
            throw Bad(field + " 落在夏令时跳变或重复时段，请填写带偏移的时间。");
        return UtcTimestamp.Truncate(new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timeZone)));
    }
    private static CollectorException Bad(string message) => CollectorException.BadRequest(message);
}
