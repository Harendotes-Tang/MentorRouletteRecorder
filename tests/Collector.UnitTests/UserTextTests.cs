using System.Text.Json.Nodes;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Contracts.Texts;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Catalogue texts (contracts/collector-texts.md): the Chinese a Desktop without the keys shows is
/// the template with the values put in, and the reference the Desktop renders carries every value
/// by name in its wire form.
/// </summary>
public sealed class UserTextTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 15, 12, 34, 56, 789, TimeSpan.Zero);

    private static UserText Sample() => new(
        "test.sample",
        "第 {row} 行：{name} 于 {at}，{reason}",
        UserTextArg.Number("row", 12),
        UserTextArg.Duty("name", "天狼星灯塔"),
        UserTextArg.Clock("at", At),
        UserTextArg.Ref("reason", new UserText("test.reason", "原因")));

    [Fact]
    public void Chinese_IsTheTemplateWithTheValuesPutIn()
    {
        var expected = "第 12 行：天狼星灯塔 于 " +
                       At.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) +
                       "，原因";

        Assert.Equal(expected, Sample().Chinese);
        Assert.Equal(expected, Sample().ToString());
    }

    [Fact]
    public void ToJson_CarriesTheKeyAndEveryValueInItsWireForm()
    {
        var json = Sample().ToJson();

        Assert.Equal("test.sample", json["key"]!.GetValue<string>());
        var args = json["args"]!.AsObject();
        Assert.Equal(12, args["row"]!.GetValue<long>());
        Assert.Equal("天狼星灯塔", args["name"]!.GetValue<string>());
        Assert.Equal("2026-10-15T12:34:56.789Z", args["at"]!.GetValue<string>());
        Assert.Equal("test.reason", args["reason"]!["key"]!.GetValue<string>());
        Assert.Null(args["reason"]!["args"]);
    }

    [Fact]
    public void ToJson_LeavesArgsOutWhenThereAreNone()
    {
        var json = new UserText("test.plain", "没有占位符。").ToJson();

        Assert.False(json.ContainsKey("args"));
    }

    [Fact]
    public void A_ValueThatLooksLikeAPlaceholderIsNotExpandedAgain()
    {
        var text = new UserText(
            "test.nested", "{a}{b}", UserTextArg.Text("a", "{b}"), UserTextArg.Text("b", "乙"));

        Assert.Equal("{b}乙", text.Chinese);
    }

    [Fact]
    public void A_TextArgumentLongerThanTheContractAllowsIsCutTheSameWayInBothForms()
    {
        var huge = new string('字', UserTextArg.MaxTextLength + 50);

        var text = new UserText("test.long", "字段 {field}。", UserTextArg.Text("field", huge));

        var wire = text.ToJson()["args"]!["field"]!.GetValue<string>();
        Assert.Equal(UserTextArg.MaxTextLength, wire.Length);
        Assert.EndsWith("\u2026", wire, StringComparison.Ordinal);
        Assert.Equal("字段 " + wire + "。", text.Chinese);
    }

    [Fact]
    public void A_CutNeverSplitsASurrogatePair()
    {
        var huge = new string('a', UserTextArg.MaxTextLength - 2) + "\U0001F600" + new string('b', 10);

        var wire = new UserText("test.long", "{v}", UserTextArg.Text("v", huge)).ToJson()["args"]!["v"]!.GetValue<string>();

        Assert.False(char.IsHighSurrogate(wire[^2]));
        Assert.True(wire.Length <= UserTextArg.MaxTextLength);
    }

    [Fact]
    public void A_MissingArgumentIsAProgrammingError()
    {
        var text = new UserText("test.broken", "第 {row} 行");

        Assert.Throws<InvalidOperationException>(() => text.Chinese);
    }

    [Fact]
    public void Failure_CarriesTheTextAsMessageRefNextToTheChineseMessage()
    {
        var text = CollectorTexts.IpcRequestNotJson();

        var envelope = IpcEnvelope.Failure(
            Guid.NewGuid().ToString("D"), "Error", CollectorException.BadRequest(text));

        foreach (var copy in new[] { envelope["error"]!, envelope["payload"]! })
        {
            Assert.Equal(text.Chinese, copy["message"]!.GetValue<string>());
            Assert.Equal("ipc.request.not_json", copy["message_ref"]!["key"]!.GetValue<string>());
        }
    }

    [Fact]
    public void Failure_WithoutACatalogueTextHasNoMessageRef()
    {
        var envelope = IpcEnvelope.Failure(
            Guid.NewGuid().ToString("D"), "Error", new CollectorException(ErrorCodes.Internal, "系统原文"));

        Assert.False(envelope["error"]!.AsObject().ContainsKey("message_ref"));
        Assert.Equal("系统原文", envelope["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void ToJsonArray_IsParallelToTheChineseList()
    {
        var texts = new[] { new UserText("test.a", "甲"), new UserText("test.b", "乙") };

        var array = UserText.ToJsonArray(texts);

        Assert.Equal(2, array.Count);
        Assert.Equal("test.b", array[1]!["key"]!.GetValue<string>());
        Assert.IsType<JsonArray>(array);
    }
}
