using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using MentorRecorder.Collector.Capture;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The kernel process-table reader that gives every game process its creation time without a
/// handle on it. The parser is driven with tables built byte by byte, so the offsets are pinned
/// without the operating system; one test reads the real table.
/// </summary>
public sealed class ProcessTableTests
{
    private const long Base = 0x7FF6_1000_0000;
    private const int RowBytes = 0x100;

    private static readonly DateTimeOffset Started = new(2026, 10, 3, 8, 30, 15, TimeSpan.Zero);

    [Fact]
    public void ReadsTheProcessIdNameAndCreationTimeOfEveryRow()
    {
        var table = Table(
            (0, "", 0),
            (4, "System", Started.AddHours(-5).ToFileTime()),
            (4321, "ffxiv_dx11.exe", Started.ToFileTime()));

        var entries = ProcessTable.Parse(table, Base);

        Assert.NotNull(entries);
        Assert.Equal(3, entries.Count);
        Assert.Equal(new ProcessTableEntry(0, "", null), entries[0]);
        Assert.Equal(new ProcessTableEntry(4, "System", Started.AddHours(-5)), entries[1]);
        Assert.Equal(new ProcessTableEntry(4321, "ffxiv_dx11.exe", Started), entries[2]);
    }

    [Fact]
    public void ARowWithoutACreationTimeHasNoneRatherThanTheEpoch()
    {
        var entries = ProcessTable.Parse(Table((4321, "ffxiv_dx11.exe", 0)), Base);

        Assert.Null(Assert.Single(entries!).CreatedAtUtc);
    }

    [Fact]
    public void ANameThatPointsOutsideTheTableMakesTheWholeReadFail()
    {
        var table = Table((4321, "ffxiv_dx11.exe", Started.ToFileTime()));
        BinaryPrimitives.WriteInt64LittleEndian(
            table.AsSpan(ProcessTable.ImageNameBufferField), Base + table.Length - 2);

        Assert.Null(ProcessTable.Parse(table, Base));
    }

    [Fact]
    public void ANextRowBeyondTheTableMakesTheWholeReadFail()
    {
        var table = Table((4, "System", Started.ToFileTime()), (4321, "ffxiv_dx11.exe", Started.ToFileTime()));
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(ProcessTable.NextEntryOffsetField), (uint)table.Length);

        Assert.Null(ProcessTable.Parse(table, Base));
    }

    [Fact]
    public void ATruncatedTableIsAFailureNotAShorterList()
    {
        var table = Table((4, "System", Started.ToFileTime()), (4321, "ffxiv_dx11.exe", Started.ToFileTime()));

        Assert.Null(ProcessTable.Parse(table.AsSpan(0, RowBytes + 0x40), Base));
        Assert.Null(ProcessTable.Parse(ReadOnlySpan<byte>.Empty, Base));
    }

    [Theory]
    [InlineData("ffxiv_dx11.exe", "ffxiv_dx11")]
    [InlineData("FFXIV_DX11.EXE", "FFXIV_DX11")]
    [InlineData(@"\Device\HarddiskVolume3\game\ffxiv.exe", "ffxiv")]
    [InlineData("System", "System")]
    [InlineData("", "")]
    public void ShortNameDropsTheDirectoryAndTheExeExtension(string imageName, string expected)
    {
        Assert.Equal(expected, ProcessTable.ShortName(imageName));
    }

    [Theory]
    [InlineData(512 * 1024, 0, 1024 * 1024)]
    [InlineData(512 * 1024, 900 * 1024, 1125 * 1024)]
    [InlineData(int.MaxValue / 2 + 1, 0, int.MaxValue)]
    public void TheBufferAlwaysGrowsAfterALengthMismatch(int current, int needed, int expected)
    {
        Assert.Equal(expected, ProcessTable.NextBufferBytes(current, needed));
    }

    [Fact]
    public void TheRealTableListsThisProcessWithAPlausibleCreationTime()
    {
        using var self = Process.GetCurrentProcess();
        var now = DateTimeOffset.UtcNow;
        var boot = now - TimeSpan.FromMilliseconds(Environment.TickCount64);

        var entries = ProcessTable.TryRead();

        Assert.NotNull(entries);
        var entry = Assert.Single(entries, row => row.ProcessId == Environment.ProcessId);
        Assert.Equal(self.ProcessName, ProcessTable.ShortName(entry.ImageName), ignoreCase: true);
        // Between the machine's boot and now. The neighbouring fields of the row are durations
        // (user and kernel time) or a cycle count, none of which lands in this window.
        Assert.NotNull(entry.CreatedAtUtc);
        Assert.InRange(entry.CreatedAtUtc.Value, boot - TimeSpan.FromMinutes(1), now + TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void TheWindowsProviderTakesTheStartTimeFromTheTable()
    {
        // No process 999999 exists, so no handle could have produced this start time: it can only
        // have come from the table row.
        var provider = new WindowsGameProcessProvider(() => new[]
        {
            new ProcessTableEntry(4, "System", Started.AddHours(-5)),
            new ProcessTableEntry(999_999, "ffxiv_dx11.exe", Started),
            new ProcessTableEntry(1_000_003, "notepad.exe", Started),
        });

        var client = Assert.Single(provider.ByName(GameProcessLocator.Dx11ProcessName));

        Assert.Equal(999_999, client.ProcessId);
        Assert.Equal(GameProcessLocator.Dx11ProcessName, client.ProcessName);
        Assert.Equal(Started, client.StartedAtUtc);
    }

    [Fact]
    public void AnUnreadableTableIsAFailedListingNotAnEmptyOne()
    {
        var provider = new WindowsGameProcessProvider(() => null);

        Assert.Throws<InvalidOperationException>(() => provider.ByName(GameProcessLocator.Dx11ProcessName));
        Assert.Null(new GameProcessLocator(provider).ListCandidates());
    }

    [Fact]
    public void TheShippedProviderFindsThisProcessWithTheTablesStartTime()
    {
        using var self = Process.GetCurrentProcess();
        var row = Assert.Single(ProcessTable.TryRead()!, entry => entry.ProcessId == Environment.ProcessId);

        var listed = WindowsGameProcessProvider.Instance.ByName(self.ProcessName);

        var me = Assert.Single(listed, candidate => candidate.ProcessId == Environment.ProcessId);
        Assert.NotNull(me.StartedAtUtc);
        Assert.Equal(row.CreatedAtUtc, me.StartedAtUtc);
    }

    /// <summary>
    /// Builds a table the way the kernel lays it out: fixed-size rows chained by
    /// NextEntryOffset, each name stored inside the row's own slot and referenced by an absolute
    /// pointer based at <see cref="Base"/>.
    /// </summary>
    private static byte[] Table(params (long Pid, string Name, long CreateTime)[] rows)
    {
        var table = new byte[rows.Length * RowBytes];
        for (var index = 0; index < rows.Length; index++)
        {
            var row = table.AsSpan(index * RowBytes, RowBytes);
            var (pid, name, created) = rows[index];
            BinaryPrimitives.WriteUInt32LittleEndian(
                row[ProcessTable.NextEntryOffsetField..], index == rows.Length - 1 ? 0u : RowBytes);
            BinaryPrimitives.WriteInt64LittleEndian(row[ProcessTable.CreateTimeField..], created);
            BinaryPrimitives.WriteInt64LittleEndian(row[ProcessTable.UniqueProcessIdField..], pid);
            var nameBytes = Encoding.Unicode.GetBytes(name);
            BinaryPrimitives.WriteUInt16LittleEndian(row[ProcessTable.ImageNameLengthField..], (ushort)nameBytes.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(row[(ProcessTable.ImageNameLengthField + 2)..], (ushort)nameBytes.Length);
            if (nameBytes.Length > 0)
            {
                const int nameAt = 0x80;
                nameBytes.CopyTo(row[nameAt..]);
                BinaryPrimitives.WriteInt64LittleEndian(
                    row[ProcessTable.ImageNameBufferField..], Base + (index * RowBytes) + nameAt);
            }
        }

        return table;
    }
}
