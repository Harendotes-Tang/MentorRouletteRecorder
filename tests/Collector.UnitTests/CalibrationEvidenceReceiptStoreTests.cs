using System.Text.Json.Nodes;
using MentorRecorder.Collector.Protocol.Sharing;

namespace MentorRecorder.Collector.UnitTests;

public sealed class CalibrationEvidenceReceiptStoreTests
{
    private static CalibrationEvidenceTarget Target(int id = 0) => new("CN", SharedCalibrationTestBed.Build,
        "cn.template", new string('0', 64), id.ToString("x64"), 0);
    private static CalibrationEvidenceSession Session() => new(Guid.NewGuid().ToString("D"), true,
        new("NONE", 0, 0, 0, 0, false), new[] { new CalibrationEvidenceCheck("zone", "PASS") });

    [Fact]
    public void RetentionBoundsAndDuplicateIdentitySurviveAtomicRoundTrip()
    {
        using var bed = new SharedCalibrationTestBed();
        var root = Path.Combine(bed.Root, "receipts");
        var store = new CalibrationEvidenceReceiptStore(root);
        var session = Session();
        Assert.True(store.Append(Target(), session));
        var first = store.Find(Target())!.Serialize();
        Assert.True(store.Append(Target(), session));
        Assert.Equal(first, store.Find(Target())!.Serialize());
        for (var index = 0; index < 9; index++) Assert.True(store.Append(Target(), Session()));
        var latest = new CalibrationEvidenceReceiptStore(root).Find(Target())!;
        Assert.Equal(8, latest.Sessions.Count);
        Assert.Equal(8, latest.Sessions.Select(item => item.SessionAlias).Distinct().Count());
        Assert.NotEqual(first, latest.Serialize());
        for (var index = 1; index <= CalibrationEvidenceReceiptStore.MaxDocuments; index++)
            Assert.True(store.Append(Target(index), Session()));
        var reloaded = new CalibrationEvidenceReceiptStore(root);
        Assert.Null(reloaded.Find(Target()));
        Assert.NotNull(reloaded.Find(Target(CalibrationEvidenceReceiptStore.MaxDocuments)));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Fact]
    public void AdditionalOrPrivateDiskFieldsCannotBecomePublicEvidence()
    {
        using var bed = new SharedCalibrationTestBed();
        var root = Path.Combine(bed.Root, "receipts");
        var store = new CalibrationEvidenceReceiptStore(root);
        Assert.True(store.Append(Target(), Session()));
        var path = Path.Combine(root, "receipts.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        json[0]!["sessions"]![0]!["health"]!["device_id"] = "private-device";
        File.WriteAllText(path, json.ToJsonString());
        Assert.Null(new CalibrationEvidenceReceiptStore(root).Find(Target()));
        File.WriteAllText(path, "[");
        Assert.Null(new CalibrationEvidenceReceiptStore(root).Find(Target()));
    }

    [Fact]
    public void MissingOrNegativeHealthAndIncompleteSessionsCannotBePersisted()
    {
        var store = new CalibrationEvidenceReceiptStore();
        Assert.False(store.Append(Target(), Session() with { Complete = false }));
        Assert.False(store.Append(Target(), Session() with { Health = Session().Health with { PreexistingConnections = null } }));
        Assert.False(store.Append(Target(), Session() with { Health = Session().Health with { AdapterDropped = -1 } }));
        Assert.False(store.Append(Target(), Session() with { Health = Session().Health with { ObservationOverflow = true } }));
    }
}
