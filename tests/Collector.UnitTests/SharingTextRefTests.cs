using System.Reflection;
using MentorRecorder.Collector.Contracts.Texts;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Sharing;
using MentorRecorder.Collector.Tests;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Shared-calibration sentences are catalogue texts (contracts/collector-texts.md): a share-code
/// rejection, the <c>ImportCalibrationCode</c> message with its <c>message_ref</c>, and the reasons
/// <c>GetCalibrationShareCode</c> gives none. The Chinese is unchanged, so sharecode.py still agrees.
/// </summary>
public sealed class SharingTextRefTests
{
    public static TheoryData<string> Tokens()
    {
        var data = new TheoryData<string>();
        foreach (var field in typeof(ShareCodeRejection).GetFields(BindingFlags.Public | BindingFlags.Static)
                     .Where(field => field.IsLiteral && field.FieldType == typeof(string)))
        {
            data.Add((string)field.GetRawConstantValue()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Tokens))]
    public void EveryRejectionTokenIsWordedByASharingCodeKey(string token)
    {
        var rejection = ShareCodeRejection.For(token, "detail");

        Assert.StartsWith("sharing.code.", rejection.Text.Key, StringComparison.Ordinal);
        Assert.Equal(rejection.Text.Chinese, rejection.Message);
        Assert.Equal(rejection.Message, CollectorTextCatalogue.Render(rejection.Text.ToJson()));
    }

    [Fact]
    public void AnUnknownTokenFallsBackToTheGeneralSentence()
    {
        Assert.Equal("sharing.code.unusable", ShareCodeRejection.For("E_SHARE_CODE_UNKNOWN", string.Empty).Text.Key);
        Assert.Equal("sharing.code.characters", ShareCode.Decode("MRC1.???").Rejection!.Text.Key);
    }

    [Fact]
    public void TheImportAnswerCarriesItsMessageAsAReferenceWithTheRejectionNested()
    {
        var malformed = SharedCalibrationHandlers.ImportResponse(new SharedImportResult(
            SharedImportOutcome.Malformed, ShareCodeRejection.Characters,
            CollectorTexts.SharingImportNotImported(ShareCode.Decode("MRC1.???").Rejection!.Text)));

        Assert.Equal("sharing.import.not_imported", malformed["message_ref"]!["key"]!.GetValue<string>());
        Assert.Equal("sharing.code.characters", malformed["message_ref"]!["args"]!["reason"]!["key"]!.GetValue<string>());
        Assert.StartsWith("校准码未导入：", malformed["message"]!.GetValue<string>(), StringComparison.Ordinal);
        CollectorTextCatalogue.AssertParallel(malformed["message"], malformed["message_ref"]);

        var otherBuild = SharedCalibrationHandlers.ImportResponse(new SharedImportResult(
            SharedImportOutcome.NotApplicable, "OTHER_BUILD",
            CollectorTexts.SharingImportOtherBuild("2026.09.01.0000.0000", "2026.10.06.0000.0000"), new string('a', 64)));

        var args = otherBuild["message_ref"]!["args"]!;
        Assert.Equal("2026.09.01.0000.0000", args["code_build"]!.GetValue<string>());
        Assert.Equal("2026.10.06.0000.0000", args["current_build"]!.GetValue<string>());
        Assert.Contains("它适用于游戏版本 2026.09.01.0000.0000，你当前的版本是 2026.10.06.0000.0000；", otherBuild["message"]!.GetValue<string>(), StringComparison.Ordinal);
        CollectorTextCatalogue.AssertParallel(otherBuild["message"], otherBuild["message_ref"]);
    }

    [Theory]
    [InlineData(SharedShareCodeRefusal.NoProfile, "sharing.share_code.no_profile")]
    [InlineData(SharedShareCodeRefusal.NotLocal, "sharing.share_code.not_local")]
    [InlineData(SharedShareCodeRefusal.Shared, "sharing.share_code.shared_missing")]
    [InlineData(SharedShareCodeRefusal.SharedNotProven, "sharing.share_code.shared_not_proven")]
    [InlineData(SharedShareCodeRefusal.NotShareable, "sharing.share_code.local_unusable")]
    public void EveryShareCodeRefusalIsACatalogueText(SharedShareCodeRefusal refusal, string key)
    {
        var result = SharedShareCodeResult.Refused(refusal);

        Assert.Equal(key, result.Text!.Key);
        Assert.Equal(result.Text.Chinese, result.Message);
    }
}
