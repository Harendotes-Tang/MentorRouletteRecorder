using System.Text;
using System.Text.Json;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>Builds well-formed and deliberately malformed profile files for the tests.</summary>
internal static class ProfileTestFiles
{
    /// <summary>A profile that loads: synthetic, three messages, every field type covered.</summary>
    public static Dictionary<string, object> Valid() => new(StringComparer.Ordinal)
    {
        ["schema_version"] = 1,
        ["profile_id"] = "placeholder",
        ["region"] = "UNKNOWN",
        ["game_build"] = "test-build-1",
        ["generated_at"] = "2026-09-04T00:00:00.000Z",
        ["mentor_roulette_id"] = 42,
        ["compatibility_status"] = "SYNTHETIC",
        ["match_window_seconds"] = 45,
        ["messages"] = new List<object>
        {
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["name"] = "CONTENT_FINDER_POP",
                ["opcode"] = 100,
                ["direction"] = "SERVER_TO_CLIENT",
                ["expected_length"] = 24,
                ["fields"] = new List<object>
                {
                    Field("roulette_id", 0, "u16", endian: "little", min: 1),
                    Field("content_id", 4, "u32", endian: "big"),
                    Field("token", 8, "u64", endian: "little"),
                    Field("marker", 16, "i32", endian: "big"),
                    Field("padding", 20, "bytes", length: 4),
                },
            },
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["name"] = "ZONE_INITIALIZATION",
                ["opcode"] = 101,
                ["direction"] = "SERVER_TO_CLIENT",
                ["expected_length"] = 8,
                ["fields"] = new List<object>
                {
                    Field("territory_id", 0, "u16", endian: "little"),
                    Field("content_id", 2, "u16", endian: "big"),
                    Field("is_duty_instance", 4, "u8", allowed: new[] { 0, 1 }),
                },
            },
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["name"] = "DUTY_RESULT",
                ["opcode"] = 102,
                ["direction"] = "SERVER_TO_CLIENT",
                ["expected_length"] = 2,
                ["victory_values"] = new List<object> { 7 },
                ["fields"] = new List<object> { Field("outcome", 0, "u8") },
            },
        },
        ["fixtures"] = new List<object>(),
        ["provenance"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["summary"] = "SYNTHETIC test profile. Every constant below is invented.",
            ["capture_fixture"] = null!,
            ["capture_fixture_sha256"] = null!,
            ["evidence"] = new List<object>(),
        },
        ["profile_sha256"] = new string('0', 64),
    };

    /// <summary>Builds one field definition.</summary>
    public static Dictionary<string, object> Field(
        string name,
        int offset,
        string type,
        string? endian = null,
        int? length = null,
        long? min = null,
        long? max = null,
        int[]? allowed = null,
        string? role = null)
    {
        var field = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["name"] = name,
            ["offset"] = offset,
            ["type"] = type,
        };
        if (role is not null)
        {
            field["role"] = role;
        }

        if (length is { } declaredLength)
        {
            field["length"] = declaredLength;
        }

        if (endian is not null)
        {
            field["endian"] = endian;
        }

        if (min is not null || max is not null || allowed is not null)
        {
            var constraints = new Dictionary<string, object>(StringComparer.Ordinal);
            if (min is { } low)
            {
                constraints["min"] = low;
            }

            if (max is { } high)
            {
                constraints["max"] = high;
            }

            if (allowed is not null)
            {
                constraints["in"] = allowed.Select(value => (object)value).ToList();
            }

            field["constraints"] = constraints;
        }

        return field;
    }

    /// <summary>Writes a profile document, stamping its canonical hash unless told not to.</summary>
    /// <param name="directory">Directory to write into.</param>
    /// <param name="profileId">File name stem.</param>
    /// <param name="document">Document to write.</param>
    /// <param name="stamp">Whether to write a correct <c>profile_sha256</c>.</param>
    /// <param name="rewriteId">Whether to set <c>profile_id</c> to <paramref name="profileId"/>.</param>
    public static string Write(
        string directory,
        string profileId,
        Dictionary<string, object> document,
        bool stamp = true,
        bool rewriteId = true)
    {
        if (rewriteId)
        {
            document["profile_id"] = profileId;
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(document, options);
        if (stamp)
        {
            document["profile_sha256"] = ProfileLoader.ComputeProfileHash(json);
            json = JsonSerializer.Serialize(document, options);
        }

        var path = Path.Combine(directory, profileId + ".json");
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }
}
