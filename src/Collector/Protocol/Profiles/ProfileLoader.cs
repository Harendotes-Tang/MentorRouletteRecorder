using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Domain.Events;

namespace MentorRecorder.Collector.Protocol.Profiles;

/// <summary>
/// Reads one protocol profile file and decides whether the parser may use it.
///
/// Four gates, in order, and a failure at any of them refuses the whole file rather than
/// degrading it: the document must satisfy <c>protocol-profiles/profile.schema.json</c>;
/// <c>profile_sha256</c> must equal the canonical hash of the document without that field;
/// the semantic rules of docs/protocol-profile-format.md must hold (an UNSUPPORTED profile
/// declares no message at all, a DUTY_RESULT declares a non-empty victory set, no field
/// reads past its own declared message length); and every referenced fixture that exists
/// must hash to what the profile claims.
///
/// The loader never invents a value. A profile with a missing or malformed constant is
/// refused; it is never patched up with a default.
/// </summary>
public static class ProfileLoader
{
    /// <summary>Resource name of the embedded copy of the profile schema.</summary>
    public const string SchemaResourceName = "MentorRecorder.Collector.Protocol.profile.schema.json";

    private static readonly IReadOnlyDictionary<string, string[]> RequiredFields =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["CONTENT_FINDER_POP"] = new[] { "roulette_id" },
            ["ZONE_INITIALIZATION"] = Array.Empty<string>(),
            ["ZONE_TERRITORY"] = new[] { "territory_id" },
            ["DUTY_RESULT"] = new[] { "outcome" },
            ["PLAYER_JOB"] = new[] { "job_id" },
            ["ZONE_LEFT"] = Array.Empty<string>(),
            ["INSTANCE_LEFT"] = Array.Empty<string>(),
            ["MATCH_CANCELLED"] = Array.Empty<string>(),
        };

    // DUTY_RESULT is optional: a profile without it can still record a run from pop to
    // exit, but every exit closes as UNKNOWN pending the user's confirmation, because the
    // state machine refuses to infer completion from anything but a verified victory.
    //
    // Public so the calibration observer checks readiness against this list rather than its own.
    public static readonly string[] MessagesRequiredWhenUsable =
    {
        "CONTENT_FINDER_POP", "ZONE_INITIALIZATION",
    };

    /// <summary>Evidence field a VERIFIED profile must cite when it carries a calibration template.</summary>
    public const string CalibrationEvidenceKey = "calibration.finder_request.roulette_field";

    private static readonly Lazy<JsonDocument> Schema = new(LoadSchema, isThreadSafe: true);

    /// <summary>The profile schema this build validates against.</summary>
    public static JsonElement SchemaDocument => Schema.Value.RootElement;

    /// <summary>Canonical hash a profile document must declare as its <c>profile_sha256</c>.</summary>
    /// <param name="json">Complete profile JSON text.</param>
    public static string ComputeProfileHash(string json)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
        using var document = JsonDocument.Parse(json);
        return ComputeProfileHash(document.RootElement);
    }

    /// <summary>Canonical hash of an already parsed profile document.</summary>
    /// <param name="root">Document root.</param>
    public static string ComputeProfileHash(JsonElement root)
    {
        var canonical = CanonicalJson.SerializeWithout(root, "profile_sha256");
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    /// <summary>Validates one profile file and, when it is clean, returns the loaded profile.</summary>
    /// <param name="path">Profile file path.</param>
    public static ProfileValidationReport Validate(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string fullPath;
        string text;
        try
        {
            fullPath = Path.GetFullPath(path);
            text = File.ReadAllText(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return ProfileValidationReport.Failure(path, "E_PROFILE_UNREADABLE", ex.Message);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            return ProfileValidationReport.Failure(fullPath, "E_PROFILE_PARSE", ex.Message);
        }

        using (document)
        {
            return Validate(fullPath, document.RootElement);
        }
    }

    /// <summary>Validates an already parsed profile document.</summary>
    /// <param name="fullPath">Path the document came from; used for the file-name and directory rules.</param>
    /// <param name="root">Document root.</param>
    public static ProfileValidationReport Validate(string fullPath, JsonElement root)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullPath);
        var errors = new List<ProfileIssue>();
        var warnings = new List<ProfileIssue>();

        foreach (var message in JsonSchemaValidator.Validate(root, SchemaDocument))
        {
            errors.Add(new ProfileIssue("E_PROFILE_SCHEMA", "$", message));
        }

        if (errors.Count > 0)
        {
            return new ProfileValidationReport(
                fullPath, null, null, null, null, 0, false, errors, warnings, null);
        }

        var profileId = root.GetProperty("profile_id").GetString()!;
        var regionText = root.GetProperty("region").GetString()!;
        var gameBuild = root.GetProperty("game_build").GetString()!;
        var statusText = root.GetProperty("compatibility_status").GetString()!;

        string expectedHash;
        try
        {
            expectedHash = ComputeProfileHash(root);
        }
        catch (InvalidDataException ex)
        {
            errors.Add(new ProfileIssue("E_PROFILE_CANONICAL", "$", ex.Message));
            return new ProfileValidationReport(
                fullPath, profileId, regionText, gameBuild, statusText, 0, false, errors, warnings, null);
        }

        if (!string.Equals(root.GetProperty("profile_sha256").GetString(), expectedHash, StringComparison.Ordinal))
        {
            errors.Add(new ProfileIssue(
                "E_PROFILE_HASH", "$.profile_sha256", "profile_sha256 does not match the document"));
        }

        // The schema checks the spelling, but dates such as February 30 still match it.
        // Refuse that one profile before construction so catalogue loading can continue.
        if (!UtcTimestamp.TryParse(root.GetProperty("generated_at").GetString(), out var generatedAt))
        {
            errors.Add(new ProfileIssue(
                "E_PROFILE_TIMESTAMP", "$.generated_at", "generated_at is not a valid UTC timestamp"));
        }

        CheckIdentity(fullPath, profileId, regionText, statusText, errors);
        var messages = ReadMessages(root, statusText, errors);
        var hypotheses = ReadHypotheses(root, statusText, errors);
        var calibration = ReadCalibration(root, statusText, errors);
        var obfuscated = ReadObfuscatedOpcodes(root, messages, errors);
        CheckStatusRules(root, statusText, messages, hypotheses, errors);
        var fixtures = ReadFixtures(root);
        var fixturesVerified = CheckFixtures(fullPath, fixtures, errors, warnings);

        if (errors.Count > 0)
        {
            return new ProfileValidationReport(
                fullPath, profileId, regionText, gameBuild, statusText,
                messages.Count, fixturesVerified, errors, warnings, null);
        }

        var profile = new ProtocolProfile(
            profileId,
            EnumWire<Region>.Parse(regionText),
            gameBuild,
            generatedAt,
            root.GetProperty("mentor_roulette_id").ValueKind == JsonValueKind.Null
                ? null
                : root.GetProperty("mentor_roulette_id").GetInt32(),
            ParseStatus(statusText),
            TimeSpan.FromSeconds(
                root.TryGetProperty("match_window_seconds", out var window) ? window.GetInt32() : 45),
            messages,
            fixtures,
            root.GetProperty("provenance").GetProperty("summary").GetString()!,
            expectedHash,
            fullPath,
            fixturesVerified,
            obfuscated)
        {
            Hypotheses = hypotheses,
            Calibration = calibration,
        };

        return new ProfileValidationReport(
            fullPath, profileId, regionText, gameBuild, statusText,
            messages.Count, fixturesVerified, errors, warnings, profile);
    }

    /// <summary>Loads a profile, throwing when it is refused.</summary>
    /// <param name="path">Profile file path.</param>
    public static ProtocolProfile Load(string path)
    {
        var report = Validate(path);
        if (report.Profile is null)
        {
            var first = report.Errors.Count > 0 ? report.Errors[0] : new ProfileIssue("E_PROFILE", "$", "refused");
            throw new InvalidDataException($"profile refused ({first.Code}): {first.Message}");
        }

        return report.Profile;
    }

    /// <summary>
    /// Reads <c>obfuscated_opcodes</c> and refuses a profile that both declares an opcode as
    /// scrambled and claims to parse it. Contradicting yourself in one document is the one
    /// case where guessing which half is right would be actively harmful.
    /// </summary>
    private static IReadOnlyList<int> ReadObfuscatedOpcodes(
        JsonElement root, IReadOnlyList<ProfileMessage> messages, List<ProfileIssue> errors)
    {
        if (!root.TryGetProperty("obfuscated_opcodes", out var declared))
        {
            return Array.Empty<int>();
        }

        var opcodes = new List<int>(declared.GetArrayLength());
        foreach (var element in declared.EnumerateArray())
        {
            opcodes.Add(element.GetInt32());
        }

        foreach (var message in messages)
        {
            if (opcodes.Contains(message.Opcode))
            {
                errors.Add(new ProfileIssue(
                    "E_PROFILE_OBFUSCATED_MESSAGE",
                    "$.obfuscated_opcodes",
                    $"opcode {message.Opcode} is declared obfuscated and also declared as message "
                        + message.Name));
            }
        }

        return opcodes;
    }

    private static void CheckIdentity(
        string fullPath, string profileId, string regionText, string statusText, List<ProfileIssue> errors)
    {
        var stem = Path.GetFileNameWithoutExtension(fullPath);
        if (!string.Equals(stem, profileId, StringComparison.Ordinal))
        {
            errors.Add(new ProfileIssue(
                "E_PROFILE_ID", "$.profile_id", $"profile_id must equal the file name stem '{stem}'"));
        }

        var directory = Path.GetFileName(Path.GetDirectoryName(fullPath) ?? string.Empty).ToLowerInvariant();
        var expectedRegion = directory switch
        {
            "cn" => "CN",
            "global" => "GLOBAL",
            "synthetic" => "UNKNOWN",
            _ => null,
        };
        if (expectedRegion is not null && !string.Equals(regionText, expectedRegion, StringComparison.Ordinal))
        {
            errors.Add(new ProfileIssue(
                "E_PROFILE_REGION", "$.region", $"region must be {expectedRegion} in directory '{directory}'"));
        }

        var isSynthetic = string.Equals(statusText, "SYNTHETIC", StringComparison.Ordinal);
        var inSyntheticDirectory = string.Equals(directory, "synthetic", StringComparison.Ordinal);
        if (isSynthetic != inSyntheticDirectory && expectedRegion is not null)
        {
            errors.Add(new ProfileIssue(
                "E_PROFILE_SYNTHETIC_LOCATION",
                "$.compatibility_status",
                "SYNTHETIC profiles live in protocol-profiles/synthetic/ and nowhere else"));
        }
    }

    private static void CheckStatusRules(
        JsonElement root,
        string statusText,
        IReadOnlyList<ProfileMessage> messages,
        IReadOnlyList<ProfileHypothesis> hypotheses,
        List<ProfileIssue> errors)
    {
        var rouletteIsNull = root.GetProperty("mentor_roulette_id").ValueKind == JsonValueKind.Null;
        if (string.Equals(statusText, "UNSUPPORTED", StringComparison.Ordinal))
        {
            if (messages.Count > 0)
            {
                errors.Add(new ProfileIssue(
                    "E_PROFILE_UNSUPPORTED_MESSAGES", "$.messages",
                    "an UNSUPPORTED profile must not declare any message"));
            }

            if (!rouletteIsNull)
            {
                errors.Add(new ProfileIssue(
                    "E_PROFILE_UNSUPPORTED_ROULETTE", "$.mentor_roulette_id",
                    "an UNSUPPORTED profile must declare mentor_roulette_id: null"));
            }

            return;
        }

        if (statusText == "CANDIDATE" && messages.Count == 0 && hypotheses.Count > 0)
        {
            if (!rouletteIsNull)
            {
                errors.Add(new ProfileIssue(
                    "E_PROFILE_CANDIDATE_ROULETTE", "$.mentor_roulette_id",
                    "an opcode-only CANDIDATE profile must declare mentor_roulette_id: null"));
            }

            return;
        }

        if (rouletteIsNull)
        {
            errors.Add(new ProfileIssue(
                "E_PROFILE_NO_ROULETTE", "$.mentor_roulette_id",
                $"a {statusText} profile must declare a mentor_roulette_id"));
        }

        foreach (var required in MessagesRequiredWhenUsable)
        {
            if (!messages.Any(message => string.Equals(message.Name, required, StringComparison.Ordinal)))
            {
                errors.Add(new ProfileIssue(
                    "E_PROFILE_MISSING_MESSAGE", "$.messages", "missing required message " + required));
            }
        }

        if (!string.Equals(statusText, "VERIFIED", StringComparison.Ordinal))
        {
            return;
        }

        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in root.GetProperty("provenance").GetProperty("evidence").EnumerateArray())
        {
            if (!string.Equals(item.GetProperty("method").GetString(), "SYNTHETIC", StringComparison.Ordinal))
            {
                covered.Add(item.GetProperty("field").GetString() ?? string.Empty);
            }
        }

        foreach (var message in messages)
        {
            var key = "messages." + message.Name + ".opcode";
            if (!covered.Contains(key))
            {
                errors.Add(new ProfileIssue(
                    "E_PROFILE_NO_EVIDENCE", "$.provenance.evidence",
                    "a VERIFIED profile needs an evidence entry for " + key));
            }
        }

        if (root.TryGetProperty("calibration", out _) && !covered.Contains(CalibrationEvidenceKey))
        {
            errors.Add(new ProfileIssue(
                "E_PROFILE_NO_EVIDENCE", "$.provenance.evidence",
                "a VERIFIED template needs an evidence entry for " + CalibrationEvidenceKey));
        }
    }

    /// <summary>
    /// Reads the optional <c>calibration</c> template section. Only a VERIFIED profile may
    /// carry one: it is a template lent to the calibration observer on a client build with no
    /// profile of its own, and a CANDIDATE, UNSUPPORTED or SYNTHETIC profile has nothing
    /// verified to lend.
    /// </summary>
    private static ProfileCalibration? ReadCalibration(
        JsonElement root, string statusText, List<ProfileIssue> errors)
    {
        if (!root.TryGetProperty("calibration", out var section))
        {
            return null;
        }

        if (!string.Equals(statusText, "VERIFIED", StringComparison.Ordinal))
        {
            errors.Add(new ProfileIssue(
                "E_PROFILE_CALIBRATION_STATUS", "$.calibration",
                "only a VERIFIED profile may carry a calibration template"));
            return null;
        }

        var request = section.GetProperty("finder_request");
        var requestLength = request.GetProperty("expected_length").GetInt32();
        var wrapper = new Dictionary<string, object> { ["fields"] = new[] { request.GetProperty("roulette_field") } };
        var fields = ReadFields(
            JsonSerializer.SerializeToElement(wrapper), "calibration.finder_request", requestLength, errors);
        var rouletteField = fields.Count == 1 ? fields[0] : null;
        if (rouletteField is null)
        {
            return null;
        }

        // bytes carries no numeric value to compare against an echoed roulette id, so the
        // shape the calibration observer needs is narrower than an ordinary field.
        if (rouletteField.Type is not (ProfileFieldType.U8 or ProfileFieldType.U16 or ProfileFieldType.U32))
        {
            errors.Add(new ProfileIssue(
                "E_PROFILE_CALIBRATION_FIELD", "$.calibration.finder_request.roulette_field",
                "the calibration roulette field must be u8, u16 or u32"));
            return null;
        }

        return new ProfileCalibration(
            new CalibrationFinderRequest(
                string.Equals(
                    request.GetProperty("direction").GetString(), "SERVER_TO_CLIENT", StringComparison.Ordinal)
                    ? PacketDirection.ServerToClient
                    : PacketDirection.ClientToServer,
                requestLength,
                rouletteField),
            TimeSpan.FromMilliseconds(section.GetProperty("finder_reply_max_ms").GetInt32()));
    }

    private static IReadOnlyList<ProfileHypothesis> ReadHypotheses(
        JsonElement root, string statusText, List<ProfileIssue> errors)
    {
        if (!root.TryGetProperty("hypotheses", out var declared))
        {
            return Array.Empty<ProfileHypothesis>();
        }

        if (statusText != "CANDIDATE")
        {
            errors.Add(new ProfileIssue(
                "E_PROFILE_HYPOTHESES_STATUS", "$.hypotheses",
                "only a CANDIDATE profile may declare hypotheses"));
        }

        var hypotheses = new List<ProfileHypothesis>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var identities = new HashSet<(PacketDirection, ushort)>();
        foreach (var element in declared.EnumerateArray())
        {
            var name = element.GetProperty("name").GetString()!;
            var path = "$.hypotheses." + name;
            var direction = element.GetProperty("direction").GetString() == "SERVER_TO_CLIENT"
                ? PacketDirection.ServerToClient : PacketDirection.ClientToServer;
            var opcode = (ushort)element.GetProperty("opcode").GetInt32();
            if (!names.Add(name))
                errors.Add(new ProfileIssue("E_PROFILE_DUPLICATE_HYPOTHESIS", path, "duplicate hypothesis name"));
            if (!identities.Add((direction, opcode)))
                errors.Add(new ProfileIssue("E_PROFILE_DUPLICATE_HYPOTHESIS_OPCODE", path,
                    "another hypothesis already claims this direction and opcode"));

            var expected = element.TryGetProperty("expected_length", out var exact) ? exact.GetInt32() : (int?)null;
            var min = element.TryGetProperty("min_length", out var lower) ? lower.GetInt32() : (int?)null;
            var max = element.TryGetProperty("max_length", out var upper) ? upper.GetInt32() : (int?)null;
            if ((expected is not null) == (min is not null || max is not null))
                errors.Add(new ProfileIssue("E_PROFILE_LENGTH_RULE", path,
                    "declare either expected_length or min_length/max_length, not both and not neither"));
            if (min is { } minimum && max is { } maximum && maximum < minimum)
                errors.Add(new ProfileIssue("E_PROFILE_LENGTH_RULE", path, "max_length is below min_length"));

            hypotheses.Add(new ProfileHypothesis(name, opcode, direction, expected, min, max,
                element.GetProperty("note").GetString()!,
                element.TryGetProperty("group", out var group) ? group.GetString() : null,
                element.TryGetProperty("label", out var label) ? label.GetString() : null));
        }

        return hypotheses;
    }

    private static IReadOnlyList<ProfileMessage> ReadMessages(
        JsonElement root, string statusText, List<ProfileIssue> errors)
    {
        var messages = new List<ProfileMessage>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var opcodes = new HashSet<(PacketDirection, ushort)>();

        foreach (var element in root.GetProperty("messages").EnumerateArray())
        {
            var name = element.GetProperty("name").GetString()!;
            var path = "$.messages." + name;
            if (!names.Add(name))
            {
                errors.Add(new ProfileIssue("E_PROFILE_DUPLICATE_MESSAGE", path, "duplicate message name"));
            }

            var direction = string.Equals(
                element.GetProperty("direction").GetString(), "SERVER_TO_CLIENT", StringComparison.Ordinal)
                ? PacketDirection.ServerToClient
                : PacketDirection.ClientToServer;
            var opcode = (ushort)element.GetProperty("opcode").GetInt32();
            if (!opcodes.Add((direction, opcode)))
            {
                errors.Add(new ProfileIssue(
                    "E_PROFILE_DUPLICATE_OPCODE", path, "another message already claims this opcode"));
            }

            var expected = element.TryGetProperty("expected_length", out var exact) ? exact.GetInt32() : (int?)null;
            var min = element.TryGetProperty("min_length", out var lower) ? lower.GetInt32() : (int?)null;
            var max = element.TryGetProperty("max_length", out var upper) ? upper.GetInt32() : (int?)null;
            if ((expected is not null) == (min is not null || max is not null))
            {
                errors.Add(new ProfileIssue(
                    "E_PROFILE_LENGTH_RULE", path,
                    "declare either expected_length or min_length/max_length, not both and not neither"));
            }

            if (min is { } minimum && max is { } maximum && maximum < minimum)
            {
                errors.Add(new ProfileIssue("E_PROFILE_LENGTH_RULE", path, "max_length is below min_length"));
            }

            var victoryValues = ReadVictoryValues(element);
            if (string.Equals(name, "DUTY_RESULT", StringComparison.Ordinal) && victoryValues.Count == 0 &&
                !string.Equals(statusText, "UNSUPPORTED", StringComparison.Ordinal))
            {
                errors.Add(new ProfileIssue(
                    "E_PROFILE_NO_VICTORY", path + ".victory_values",
                    "DUTY_RESULT must declare a non-empty victory_values array"));
            }

            var fields = ReadFields(element, name, expected ?? max, errors);
            foreach (var required in RequiredFields.GetValueOrDefault(name, Array.Empty<string>()))
            {
                if (!fields.Any(field => string.Equals(field.Name, required, StringComparison.Ordinal)))
                {
                    errors.Add(new ProfileIssue(
                        "E_PROFILE_MISSING_FIELD", path, $"{name} must declare the field '{required}'"));
                }
            }

            messages.Add(new ProfileMessage(
                name,
                opcode,
                direction,
                element.TryGetProperty("segment_type", out var segment)
                    ? (ushort)segment.GetInt32()
                    : null,
                expected,
                min,
                max,
                victoryValues,
                fields));
        }

        return messages;
    }

    private static IReadOnlyList<long> ReadVictoryValues(JsonElement message)
    {
        if (!message.TryGetProperty("victory_values", out var values))
        {
            return Array.Empty<long>();
        }

        var result = new List<long>();
        foreach (var value in values.EnumerateArray())
        {
            result.Add(value.GetInt64());
        }

        return result;
    }

    private static IReadOnlyList<ProfileField> ReadFields(
        JsonElement message, string messageName, int? upperLength, List<ProfileIssue> errors)
    {
        var fields = new List<ProfileField>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in message.GetProperty("fields").EnumerateArray())
        {
            var name = element.GetProperty("name").GetString()!;
            var path = string.Create(CultureInfo.InvariantCulture, $"$.messages.{messageName}.{name}");
            if (!names.Add(name))
            {
                errors.Add(new ProfileIssue("E_PROFILE_DUPLICATE_FIELD", path, "duplicate field name"));
            }

            var type = ParseFieldType(element.GetProperty("type").GetString());
            var declaredLength = element.TryGetProperty("length", out var length) ? length.GetInt32() : (int?)null;
            if (type == ProfileFieldType.Bytes && declaredLength is null)
            {
                errors.Add(new ProfileIssue("E_PROFILE_FIELD_LENGTH", path, "a bytes field requires a length"));
                continue;
            }

            if (type != ProfileFieldType.Bytes && declaredLength is not null)
            {
                errors.Add(new ProfileIssue(
                    "E_PROFILE_FIELD_LENGTH", path, "length applies to bytes fields only"));
            }

            var field = new ProfileField(
                name,
                element.GetProperty("offset").GetInt32(),
                type,
                declaredLength ?? 0,
                element.TryGetProperty("endian", out var endian) &&
                string.Equals(endian.GetString(), "big", StringComparison.Ordinal)
                    ? ProfileEndian.Big
                    : ProfileEndian.Little,
                ReadConstraints(element, path, errors),
                element.TryGetProperty("role", out var role) &&
                string.Equals(role.GetString(), "selector", StringComparison.Ordinal)
                    ? ProfileFieldRole.Selector
                    : ProfileFieldRole.Value);

            if (upperLength is { } limit && field.Offset + field.Size > limit)
            {
                errors.Add(new ProfileIssue(
                    "E_PROFILE_FIELD_OOB", path, "the field reads past the declared message length"));
            }

            fields.Add(field);
        }

        return fields;
    }

    private static ProfileFieldConstraints ReadConstraints(
        JsonElement field, string path, List<ProfileIssue> errors)
    {
        if (!field.TryGetProperty("constraints", out var constraints))
        {
            return ProfileFieldConstraints.None;
        }

        var min = constraints.TryGetProperty("min", out var minimum) ? minimum.GetInt64() : (long?)null;
        var max = constraints.TryGetProperty("max", out var maximum) ? maximum.GetInt64() : (long?)null;
        List<long>? allowed = null;
        if (constraints.TryGetProperty("in", out var set))
        {
            allowed = new List<long>();
            foreach (var value in set.EnumerateArray())
            {
                allowed.Add(value.GetInt64());
            }

            // An empty permitted set rejects every possible value, so the message it belongs
            // to can never parse. Accepting it would yield a profile that looks healthy and
            // refuses every message of its own declared opcode (review finding L-7).
            if (allowed.Count == 0)
            {
                errors.Add(new ProfileIssue(
                    "E_PROFILE_CONSTRAINT", path, "constraint 'in' must list at least one value"));
            }
        }

        if (min is { } low && max is { } high && high < low)
        {
            errors.Add(new ProfileIssue("E_PROFILE_CONSTRAINT", path, "constraint max is below min"));
        }

        return new ProfileFieldConstraints(min, max, allowed);
    }

    private static IReadOnlyList<ProfileFixtureReference> ReadFixtures(JsonElement root)
    {
        var fixtures = new List<ProfileFixtureReference>();
        foreach (var element in root.GetProperty("fixtures").EnumerateArray())
        {
            fixtures.Add(new ProfileFixtureReference(
                element.GetProperty("path").GetString()!,
                element.GetProperty("sha256").GetString()!));
        }

        return fixtures;
    }

    private static bool CheckFixtures(
        string fullPath,
        IReadOnlyList<ProfileFixtureReference> fixtures,
        List<ProfileIssue> errors,
        List<ProfileIssue> warnings)
    {
        if (fixtures.Count == 0)
        {
            return true;
        }

        var directory = Path.GetDirectoryName(fullPath)!;
        var verified = true;
        foreach (var fixture in fixtures)
        {
            string target;
            try
            {
                target = Path.GetFullPath(Path.Combine(directory, fixture.Path));
            }
            catch (ArgumentException)
            {
                errors.Add(new ProfileIssue("E_PROFILE_FIXTURE_PATH", fixture.Path, "fixture path is invalid"));
                verified = false;
                continue;
            }

            if (!File.Exists(target))
            {
                // A missing fixture is not evidence of tampering: the test tree is not
                // deployed next to a profile. It only means the profile is unverified here.
                warnings.Add(new ProfileIssue(
                    "W_PROFILE_FIXTURE_MISSING", fixture.Path, "referenced fixture is not present"));
                verified = false;
                continue;
            }

            var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))).ToLowerInvariant();
            if (!string.Equals(actual, fixture.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new ProfileIssue(
                    "E_PROFILE_FIXTURE_HASH", fixture.Path, "referenced fixture does not match its SHA-256"));
                verified = false;
            }
        }

        return verified;
    }

    private static ProfileCompatibilityStatus ParseStatus(string text) => text switch
    {
        "VERIFIED" => ProfileCompatibilityStatus.Verified,
        "CANDIDATE" => ProfileCompatibilityStatus.Candidate,
        "SYNTHETIC" => ProfileCompatibilityStatus.Synthetic,
        _ => ProfileCompatibilityStatus.Unsupported,
    };

    private static ProfileFieldType ParseFieldType(string? text) => text switch
    {
        "u8" => ProfileFieldType.U8,
        "u16" => ProfileFieldType.U16,
        "u32" => ProfileFieldType.U32,
        "i32" => ProfileFieldType.I32,
        "u64" => ProfileFieldType.U64,
        _ => ProfileFieldType.Bytes,
    };

    private static JsonDocument LoadSchema()
    {
        var assembly = typeof(ProfileLoader).GetTypeInfo().Assembly;
        using var stream = assembly.GetManifestResourceStream(SchemaResourceName)
            ?? throw new InvalidDataException("the embedded protocol profile schema is missing");
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return JsonDocument.Parse(reader.ReadToEnd());
    }
}
