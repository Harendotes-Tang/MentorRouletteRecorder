using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using Microsoft.Win32.SafeHandles;

namespace MentorRecorder.Collector.Diagnostics;

/// <summary>Reads a bounded, local Windows Event Log query for a diagnostic export.</summary>
internal static class WindowsDesktopHangLog
{
    private const uint ChannelPath = 1;
    private const uint ReverseOrder = 0x200;
    private const uint RenderXml = 1;
    private const int NoMoreItems = 259;
    private const int MaxEvents = 32;
    private const uint EventTimeoutMs = 200;
    private const int MaxXmlBytes = 128 * 1024;
    private static readonly string Filter = "*[System[TimeCreated[timediff(@SystemTime) <= " +
        (DesktopHangReading.LookbackDays * 86_400_000L).ToString(CultureInfo.InvariantCulture) + "] and " +
        $"((Provider[@Name='{DesktopHangReading.ApplicationHang}'] and EventID={DesktopHangReading.HangEventId}) or " +
        $"(Provider[@Name='{DesktopHangReading.WindowsErrorReporting}'] and EventID={DesktopHangReading.WerEventId}))] " +
        $"and EventData[Data[@Name='AppName']='{DesktopHangReading.DesktopImage}' or Data[1]='{DesktopHangReading.DesktopImage}' or " +
        $"(Data[@Name='P1']='{DesktopHangReading.DesktopImage}' and Data[@Name='EventName']='{DesktopHangReading.AppHangB1}')]]";

    internal static DesktopHangReading Read(DateTimeOffset now)
    {
        try
        {
            return DesktopHangReading.FromEvents(Query("Application", ChannelPath), now);
        }
        catch (Exception ex) when (ex is Win32Exception or XmlException)
        {
            // The report still exports with an explicit unavailable status. Do not copy
            // exception text, Windows event XML, or log file location into diagnostics.
            return new DesktopHangReading(false);
        }
    }

    private static IEnumerable<string> Query(string source, uint flags)
    {
        using var query = new EventHandle(EvtQuery(0, source, Filter, flags | ReverseOrder));
        if (query.IsInvalid)
            throw new Win32Exception(Marshal.GetLastPInvokeError());

        var buffer = new byte[MaxXmlBytes];
        for (var i = 0; i < MaxEvents; ++i)
        {
            if (!EvtNext(query, 1, out var next, EventTimeoutMs, 0, out _))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == NoMoreItems)
                    yield break;
                throw new Win32Exception(error);
            }

            using var entry = new EventHandle(next);
            if (!EvtRender(0, entry, RenderXml, (uint)buffer.Length, buffer, out var bytes, out _))
                throw new Win32Exception(Marshal.GetLastPInvokeError());

            // The API's byte count includes the UTF-16 terminator.
            yield return Encoding.Unicode.GetString(buffer, 0, checked((int)bytes - sizeof(char)));
        }
    }

    private sealed class EventHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal EventHandle(nint value) : base(ownsHandle: true) => SetHandle(value);

        protected override bool ReleaseHandle() => EvtClose(handle);
    }

    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint EvtQuery(nint session, string path, string query, uint flags);

    [DllImport("wevtapi.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool EvtNext(EventHandle query, uint size, out nint entry, uint timeout, uint flags, out uint returned);

    [DllImport("wevtapi.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool EvtRender(nint context, EventHandle entry, uint flags, uint size, [Out] byte[] buffer,
        out uint used, out uint propertyCount);

    [DllImport("wevtapi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool EvtClose(nint handle);
}
