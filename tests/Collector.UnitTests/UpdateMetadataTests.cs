using System.Text;
using MentorRecorder.Collector.Update;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The published build metadata as untrusted input. Nothing in the file is acted on beyond one
/// version string of three numbers; every other key is ignored and every malformed document is
/// refused whole, with a reason that never repeats the file's content.
/// </summary>
public sealed class UpdateMetadataTests
{
    private static UpdateMetadataReadResult Read(string text) =>
        UpdateMetadata.Read(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void AVersionOfThreeNumbersIsRead()
    {
        var result = Read("{\"version\": \"1.2.3\"}");

        Assert.True(result.IsReadable);
        Assert.Equal("1.2.3", result.Version);
        Assert.Null(result.Refusal);
    }

    [Fact]
    public void EveryOtherKeyIsIgnored()
    {
        var result = Read(
            "{\"schema_version\": 4, \"version\": \"0.9.2\", \"commit\": \"deadbeef\", " +
            "\"assets\": [{\"name\": \"x\"}], \"notes\": null}");

        Assert.True(result.IsReadable);
        Assert.Equal("0.9.2", result.Version);
    }

    [Fact]
    public void AByteOrderMarkIsTolerated()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes("{\"version\": \"2.0.0\"}"))
            .ToArray();

        var result = UpdateMetadata.Read(bytes);

        Assert.True(result.IsReadable);
        Assert.Equal("2.0.0", result.Version);
    }

    [Theory]
    [InlineData("", "NOT_JSON")]
    [InlineData("   ", "NOT_JSON")]
    [InlineData("{\"version\": \"1.2.3\"", "NOT_JSON")]
    [InlineData("{\"version\": \"1.2.3\",}", "NOT_JSON")]
    [InlineData("// nothing\n{\"version\": \"1.2.3\"}", "NOT_JSON")]
    [InlineData("[{\"version\": \"1.2.3\"}]", "NOT_AN_OBJECT")]
    [InlineData("\"1.2.3\"", "NOT_AN_OBJECT")]
    [InlineData("null", "NOT_AN_OBJECT")]
    [InlineData("{\"version\": \"1.2.3\", \"version\": \"9.9.9\"}", "DUPLICATE_KEY")]
    [InlineData("{}", "MISSING:version")]
    [InlineData("{\"Version\": \"1.2.3\"}", "MISSING:version")]
    [InlineData("{\"version\": null}", "INVALID:version")]
    [InlineData("{\"version\": 123}", "INVALID:version")]
    [InlineData("{\"version\": \"v1.2.3\"}", "INVALID:version")]
    [InlineData("{\"version\": \"1.2\"}", "INVALID:version")]
    [InlineData("{\"version\": \"1.2.3-rc.1\"}", "INVALID:version")]
    [InlineData("{\"version\": \" 1.2.3\"}", "INVALID:version")]
    public void AnythingElseIsRefusedWithAReason(string text, string refusal)
    {
        var result = Read(text);

        Assert.False(result.IsReadable);
        Assert.Equal(refusal, result.Refusal);
        Assert.Null(result.Version);
    }

    [Fact]
    public void BytesThatAreNotUtf8AreRefused()
    {
        var result = UpdateMetadata.Read(new byte[] { 0x7B, 0xFF, 0xFE, 0x7D });

        Assert.False(result.IsReadable);
        Assert.Equal("NOT_JSON", result.Refusal);
    }

    [Fact]
    public void ADeeplyNestedDocumentIsRefusedRatherThanWalked()
    {
        var nested = string.Concat(Enumerable.Repeat("{\"a\":", 64)) + "1" +
            string.Concat(Enumerable.Repeat("}", 64));

        Assert.Equal("NOT_JSON", Read(nested).Refusal);
    }

    [Fact]
    public void ARefusalNeverRepeatsTheDocument()
    {
        var result = Read("{\"version\": \"PRIVATE-MARKER\"}");

        Assert.Equal("INVALID:version", result.Refusal);
        Assert.DoesNotContain("PRIVATE-MARKER", result.Refusal, StringComparison.Ordinal);
    }
}
