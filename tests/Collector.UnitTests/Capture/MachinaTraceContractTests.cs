using System.Reflection;
using System.Text;
using MentorRecorder.Collector.Capture;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The version gate under <see cref="MachinaCaptureSource.DecodeFailureTraceMarkers"/>.
///
/// Machina 2.4.7.7 reports a bundle decompression failure by writing an English debug line to
/// <see cref="System.Diagnostics.Trace"/> and returning null. It never raises the message, so
/// nothing downstream can count the failure and <c>decode_error_count</c> is derived from the
/// text of those lines. An upgrade that rewords one string must fail here; otherwise the
/// counter stays at zero with every test still green, and a session that was nothing but
/// failures reads as clean evidence (Codex review finding H-3).
///
/// The check runs against the assembly on disk rather than by provoking a decode failure: the
/// strings live in the assembly's UTF-16 user-string heap whether or not this machine has
/// Npcap, the game, or a compressed bundle to fail on.
/// </summary>
public sealed class MachinaTraceContractTests
{
    /// <summary>The exact Machina.FFXIV release the trace markers were read from.</summary>
    public const string PinnedVersion = "2.4.7.7";

    [Fact]
    public void TheReferencedMachinaPackageIsStillTheOneTheMarkersWereReadFrom()
    {
        var assembly = MachinaAssembly();

        Assert.Equal(PinnedVersion, assembly.GetName().Version!.ToString());
    }

    [Fact]
    public void MachinaStillContainsEveryDecodeFailureTraceMarkerThisBuildMatchesOn()
    {
        var image = File.ReadAllBytes(MachinaAssembly().Location);

        foreach (var marker in MachinaCaptureSource.DecodeFailureTraceMarkers)
        {
            Assert.True(
                Contains(image, Encoding.Unicode.GetBytes(marker)),
                "Machina.FFXIV " + PinnedVersion + " no longer contains the trace marker \"" + marker +
                "\". decode_error_count is derived from these strings, so a reworded upstream " +
                "message silently zeroes it. Re-read the strings from the new package, update " +
                "MachinaCaptureSource.DecodeFailureTraceMarkers and PinnedVersion together.");
        }
    }

    /// <summary>
    /// The fatal markers that come from Machina rather than from SharpPcap are pinned the same
    /// way, read from the list the product matches on, so a misspelt or invented marker fails
    /// here (audit 2026-11-03, A3-3). <c>PcapException</c> and <c>Error opening</c> are SharpPcap's,
    /// kept in a list of their own: matching them is a separate contract.
    /// </summary>
    [Fact]
    public void MachinaStillContainsTheFatalTraceMarkersThisBuildMatchesOn()
    {
        var image = File.ReadAllBytes(MachinaAssembly().Location);
        var markers = MachinaCaptureSource.MachinaFatalTraceMarkers
            .Concat(MachinaCaptureSource.MissingExecutableTraceMarkers).ToArray();

        Assert.NotEmpty(MachinaCaptureSource.MachinaFatalTraceMarkers);
        foreach (var marker in markers)
        {
            Assert.True(
                Contains(image, Encoding.Unicode.GetBytes(marker)),
                "Machina.FFXIV " + PinnedVersion + " no longer contains the fatal trace marker \"" +
                marker + "\"; a capture that died on startup would read as merely idle.");
        }
    }

    [Fact]
    public void EveryFatalTraceMarkerIsEitherMachinasOrSharpPcaps()
    {
        Assert.Equal(
            MachinaCaptureSource.MachinaFatalTraceMarkers.Concat(MachinaCaptureSource.SharpPcapFatalTraceMarkers),
            MachinaCaptureSource.FatalTraceMarkers);
        Assert.Empty(MachinaCaptureSource.MachinaFatalTraceMarkers.Intersect(MachinaCaptureSource.SharpPcapFatalTraceMarkers));
    }

    private static Assembly MachinaAssembly()
    {
        var assembly = typeof(Machina.FFXIV.Oodle.OodleFactory).Assembly;
        Assert.False(
            string.IsNullOrEmpty(assembly.Location),
            "the Machina.FFXIV assembly must be on disk for its string table to be read");
        return assembly;
    }

    /// <summary>Plain byte search; the user-string heap is UTF-16 and not compressed.</summary>
    /// <param name="haystack">Whole assembly image.</param>
    /// <param name="needle">UTF-16 bytes of the marker.</param>
    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (var start = 0; start + needle.Length <= haystack.Length; start++)
        {
            var matched = true;
            for (var offset = 0; offset < needle.Length; offset++)
            {
                if (haystack[start + offset] != needle[offset])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }
}
