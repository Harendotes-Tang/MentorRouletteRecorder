using System.Text;
using MentorRecorder.Collector.Update;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The checksum published beside every installer (<c>MentorRecorder-&lt;version&gt;-setup.exe.sha256</c>): 64 hex
/// digits, two spaces and the file name, as scripts/package.ps1 writes it. Read strictly - exactly 64 hex characters
/// at the start - because it is untrusted input from the network and the only thing that vouches for the installer.
/// </summary>
public sealed class UpdateChecksumTests
{
    private const string Digest = "f258e2f34a883fa01d88b69fba3095ec80d9807c466a46d22027caa825917d8e";

    [Theory]
    [InlineData(Digest + "  MentorRecorder-1.4.0-setup.exe\r\n")]
    [InlineData(Digest + "  MentorRecorder-1.4.0-setup.exe\n")]
    [InlineData(Digest + " *MentorRecorder-1.4.0-setup.exe")]
    [InlineData(Digest + "\tMentorRecorder-1.4.0-setup.exe")]
    [InlineData(Digest + "\r\n")]
    [InlineData(Digest)]
    public void ThePublishedShapeIsRead(string document)
    {
        Assert.True(UpdateChecksum.TryRead(Encoding.ASCII.GetBytes(document), out var sha256));
        Assert.Equal(Digest, sha256);
    }

    [Fact]
    public void UpperCaseHexIsReadAsTheSameDigest()
    {
        Assert.True(UpdateChecksum.TryRead(Encoding.ASCII.GetBytes(Digest.ToUpperInvariant() + "  x.exe"), out var sha256));
        Assert.Equal(Digest, sha256);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a checksum")]
    [InlineData(" " + Digest)]
    [InlineData("﻿" + Digest)]
    [InlineData("sha256:" + Digest)]
    [InlineData(Digest + "0")]
    [InlineData(Digest + "g")]
    [InlineData(Digest + ".")]
    [InlineData("<html>" + Digest + "</html>")]
    public void AnythingElseIsNoChecksum(string document)
    {
        Assert.False(UpdateChecksum.TryRead(Encoding.UTF8.GetBytes(document), out var sha256));
        Assert.Null(sha256);
    }

    [Fact]
    public void SixtyThreeDigitsAreNoChecksum() =>
        Assert.False(UpdateChecksum.TryRead(Encoding.ASCII.GetBytes(Digest[..63]), out _));

    [Fact]
    public void ADigitFromAnotherScriptIsNoHexDigit()
    {
        // U+0660 ARABIC-INDIC DIGIT ZERO in place of the first character: two UTF-8 bytes, neither an ASCII digit.
        var document = Encoding.UTF8.GetBytes("٠" + Digest[1..]);

        Assert.False(UpdateChecksum.TryRead(document, out _));
    }
}
