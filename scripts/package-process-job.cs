// Packaging verification only. This does not change production process-access policy.
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace MentorRecorder.Packaging
{
    public sealed class VerificationResult
    {
        public int ExitCode { get; set; }
        public bool TimedOut { get; set; }
        public string Stdout { get; set; }
        public string Stderr { get; set; }
    }

    // One operation owns one job; analysis callers may start several operations
    // before waiting. Disposal cancels only this operation and waits for cleanup.
    public sealed class VerificationOperation : IDisposable
    {
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private bool disposed;
        public Task<VerificationResult> Completion { get; private set; }
        public bool IsCompleted { get { return Completion.IsCompleted; } }

        internal VerificationOperation(ProcessStartInfo info, int timeoutSeconds)
        {
            Stopwatch budget = Stopwatch.StartNew();
            Completion = Task.Factory.StartNew(() => VerificationJob.RunCore(info, timeoutSeconds,
                cancellation.Token, budget), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public VerificationResult Wait() { return Completion.GetAwaiter().GetResult(); }

        public void Dispose()
        {
            if (disposed) return;
            cancellation.Cancel();
            bool completed;
            try { completed = Completion.Wait(10000); }
            catch (AggregateException error)
            {
                // Cancellation is expected during disposal; a failed native cleanup
                // or launch must stay observable even when nobody called Wait().
                if (error.Flatten().InnerExceptions.Any(inner => !(inner is OperationCanceledException))) throw;
                completed = true;
            }
            finally
            {
                if (Completion.IsCompleted)
                {
                    cancellation.Dispose();
                    disposed = true;
                }
            }
            if (!completed) throw new TimeoutException("Verification operation cleanup did not finish within its budget.");
        }
    }

    public static class VerificationJob
    {
        // No breakaway flags: all descendants remain in this owned job. The job
        // handle is not inherited, so closing this owner also cleans up on failure.
        private const uint KillOnClose = 0x2000;
        private const uint CreateSuspended = 0x4;
        private const uint CreateUnicodeEnvironment = 0x400;
        private const uint ExtendedStartupInfoPresent = 0x80000;
        private const uint CreateNoWindow = 0x08000000;
        private const uint TimeoutExitCode = 124;

        public static VerificationOperation Start(ProcessStartInfo info, int timeoutSeconds)
        {
            return new VerificationOperation(info, timeoutSeconds);
        }

        internal static VerificationResult RunCore(ProcessStartInfo info, int timeoutSeconds,
            CancellationToken cancellation, Stopwatch budget)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                throw new PlatformNotSupportedException("Package verification jobs require Windows.");
            if (info.UseShellExecute || !info.RedirectStandardOutput || !info.RedirectStandardError ||
                info.RedirectStandardInput || !String.IsNullOrEmpty(info.UserName))
                throw new ArgumentException("Package probes require redirected output, inherited credentials and no interactive input.");
            if (timeoutSeconds < 1 || timeoutSeconds > 7200)
                throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));

            IntPtr job = IntPtr.Zero, stdoutRead = IntPtr.Zero, stdoutWrite = IntPtr.Zero;
            IntPtr stderrRead = IntPtr.Zero, stderrWrite = IntPtr.Zero, stdin = IntPtr.Zero;
            IntPtr attributes = IntPtr.Zero, inheritedHandles = IntPtr.Zero, environment = IntPtr.Zero;
            bool attributesInitialized = false;
            bool processAssigned = false;
            ProcessInformation process = new ProcessInformation();
            StreamReader stdout = null, stderr = null;
            Exception primaryFailure = null;
            try
            {
                job = CreateJobObject(IntPtr.Zero, null);
                Check(job != IntPtr.Zero, "CreateJobObject");
                ExtendedLimitInformation limits = new ExtendedLimitInformation();
                limits.Basic.LimitFlags = KillOnClose;
                Check(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()),
                    "SetInformationJobObject");

                SecurityAttributes security = new SecurityAttributes();
                security.Length = Marshal.SizeOf<SecurityAttributes>();
                security.InheritHandle = true;
                Check(CreatePipe(out stdoutRead, out stdoutWrite, ref security, 0), "CreatePipe(stdout)");
                Check(SetHandleInformation(stdoutRead, 1, 0), "SetHandleInformation(stdout)");
                Check(CreatePipe(out stderrRead, out stderrWrite, ref security, 0), "CreatePipe(stderr)");
                Check(SetHandleInformation(stderrRead, 1, 0), "SetHandleInformation(stderr)");
                stdin = CreateFile("NUL", 0x80000000, 3, ref security, 3, 0, IntPtr.Zero);
                Check(stdin != new IntPtr(-1), "CreateFile(NUL)");

                IntPtr attributeBytes = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeBytes);
                attributes = Marshal.AllocHGlobal(attributeBytes);
                Check(InitializeProcThreadAttributeList(attributes, 1, 0, ref attributeBytes),
                    "InitializeProcThreadAttributeList");
                attributesInitialized = true;
                inheritedHandles = Marshal.AllocHGlobal(IntPtr.Size * 3);
                Marshal.WriteIntPtr(inheritedHandles, 0, stdin);
                Marshal.WriteIntPtr(inheritedHandles, IntPtr.Size, stdoutWrite);
                Marshal.WriteIntPtr(inheritedHandles, IntPtr.Size * 2, stderrWrite);
                // Restrict inheritance to these three streams, not other inheritable
                // handles in a PowerShell host executing unrelated work concurrently.
                Check(UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x20002), inheritedHandles,
                    new IntPtr(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero), "UpdateProcThreadAttribute");
                StartupInfoEx startup = new StartupInfoEx();
                startup.Info.Size = Marshal.SizeOf<StartupInfoEx>();
                startup.Info.Flags = 0x100;
                startup.Info.StandardInput = stdin;
                startup.Info.StandardOutput = stdoutWrite;
                startup.Info.StandardError = stderrWrite;
                startup.Attributes = attributes;

                // ProcessStartInfo's isolated environment is authoritative. The
                // explicit Unicode block preserves it without changing this host.
                string block = String.Join("\0", info.Environment.OrderBy(pair => pair.Key,
                    StringComparer.OrdinalIgnoreCase).Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
                environment = Marshal.StringToHGlobalUni(block);
                string arguments = info.ArgumentList.Count == 0 ? info.Arguments :
                    String.Join(" ", info.ArgumentList.Select(QuoteArgument));
                StringBuilder commandLine = new StringBuilder(QuoteArgument(info.FileName) + " " + arguments);
                cancellation.ThrowIfCancellationRequested();
                Check(CreateProcess(info.FileName, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                    CreateSuspended | CreateUnicodeEnvironment | ExtendedStartupInfoPresent |
                    (info.CreateNoWindow ? CreateNoWindow : 0), environment,
                    String.IsNullOrEmpty(info.WorkingDirectory) ? null : info.WorkingDirectory,
                    ref startup, out process), "CreateProcess");
                // No probe instruction has run yet. If assignment fails (e.g. an
                // incompatible outer job), finally terminates this suspended handle.
                Check(AssignProcessToJobObject(job, process.Process), "AssignProcessToJobObject");
                processAssigned = true;
                Close(ref stdoutWrite);
                Close(ref stderrWrite);
                Close(ref stdin);
                stdout = Reader(ref stdoutRead, info.StandardOutputEncoding);
                stderr = Reader(ref stderrRead, info.StandardErrorEncoding);
                Task<string> stdoutTask = stdout.ReadToEndAsync();
                Task<string> stderrTask = stderr.ReadToEndAsync();
                Check(ResumeThread(process.Thread) != UInt32.MaxValue, "ResumeThread");
                Close(ref process.Thread);

                // Parent exit is insufficient: a child can retain the pipes and
                // package/data files. Wait for the owned job, under the same budget.
                bool timedOut = !WaitForEmptyJob(job, budget, timeoutSeconds * 1000L, cancellation);
                if (timedOut)
                {
                    Check(TerminateJobObject(job, TimeoutExitCode), "TerminateJobObject");
                    if (!WaitForEmptyJob(job, Stopwatch.StartNew(), 5000, CancellationToken.None))
                        throw new TimeoutException("Package verification job did not stop within its cleanup budget.");
                }
                Check(WaitForSingleObject(process.Process, 5000) == 0, "WaitForSingleObject(process)");
                uint exitCode;
                Check(GetExitCodeProcess(process.Process, out exitCode), "GetExitCodeProcess");
                if (!Task.WhenAll(stdoutTask, stderrTask).Wait(5000))
                    throw new TimeoutException("Package verification output did not close within its drain budget.");
                return new VerificationResult
                {
                    ExitCode = unchecked((int)exitCode), TimedOut = timedOut, // BOUNDARY-ALLOW(INJ-009): Plain verification result fields populated from the owned child; no Process member access.
                    Stdout = stdoutTask.GetAwaiter().GetResult(), Stderr = stderrTask.GetAwaiter().GetResult()
                };
            }
            catch (Exception error)
            {
                primaryFailure = error;
                throw;
            }
            finally
            {
                // Use only handles created above. Never enumerate or reopen PIDs;
                // this also covers assignment failure before the suspended resume.
                List<Exception> cleanupFailures = new List<Exception>();
                if (job != IntPtr.Zero)
                {
                    try
                    {
                        Check(TerminateJobObject(job, TimeoutExitCode), "TerminateJobObject(cleanup)");
                        if (!WaitForEmptyJob(job, Stopwatch.StartNew(), 5000, CancellationToken.None))
                            throw new TimeoutException("Verification job still has active processes after cleanup.");
                    }
                    catch (Exception error) { cleanupFailures.Add(error); }
                }
                if (process.Process != IntPtr.Zero && WaitForSingleObject(process.Process, 0) != 0)
                {
                    try
                    {
                        // An assigned root is already terminating with the job;
                        // TerminateProcess can return ACCESS_DENIED in that short
                        // interval. Only the unassigned suspended root needs it.
                        if (!processAssigned)
                            Check(TerminateProcess(process.Process, TimeoutExitCode), "TerminateProcess(cleanup)");
                        if (WaitForSingleObject(process.Process, 5000) != 0)
                            throw new TimeoutException("Verification root did not terminate during cleanup.");
                    }
                    catch (Exception error) { cleanupFailures.Add(error); }
                }
                Close(ref process.Thread);
                Close(ref process.Process);
                Close(ref job);
                Close(ref stdin);
                Close(ref stdoutWrite);
                Close(ref stderrWrite);
                try { if (stdout != null) stdout.Dispose(); }
                catch (Exception error) { cleanupFailures.Add(error); }
                try { if (stderr != null) stderr.Dispose(); }
                catch (Exception error) { cleanupFailures.Add(error); }
                Close(ref stdoutRead);
                Close(ref stderrRead);
                if (attributes != IntPtr.Zero)
                {
                    if (attributesInitialized) DeleteProcThreadAttributeList(attributes);
                    Marshal.FreeHGlobal(attributes);
                }
                if (inheritedHandles != IntPtr.Zero) Marshal.FreeHGlobal(inheritedHandles);
                if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
                if (cleanupFailures.Count != 0)
                {
                    if (primaryFailure != null) cleanupFailures.Insert(0, primaryFailure);
                    throw new AggregateException("Verification process cleanup failed.", cleanupFailures);
                }
            }
        }

        private static StreamReader Reader(ref IntPtr readHandle, Encoding encoding)
        {
            SafeFileHandle owned = new SafeFileHandle(readHandle, true);
            readHandle = IntPtr.Zero;
            try { return new StreamReader(new FileStream(owned, FileAccess.Read), encoding ?? Encoding.UTF8, true); }
            catch { owned.Dispose(); throw; }
        }

        private static bool WaitForEmptyJob(IntPtr job, Stopwatch budget, long milliseconds,
            CancellationToken cancellation)
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                BasicAccountingInformation accounting;
                Check(QueryInformationJobObject(job, 1, out accounting,
                    (uint)Marshal.SizeOf<BasicAccountingInformation>(), IntPtr.Zero), "QueryInformationJobObject");
                if (accounting.ActiveProcesses == 0) return true;
                long remaining = milliseconds - budget.ElapsedMilliseconds;
                if (remaining <= 0) return false;
                Thread.Sleep((int)Math.Min(25, remaining));
            }
        }

        private static string QuoteArgument(string value)
        {
            string escaped = System.Text.RegularExpressions.Regex.Replace(value, "(\\\\*)\"", "$1$1\\\"");
            escaped = System.Text.RegularExpressions.Regex.Replace(escaped, "(\\\\+)$", "$1$1");
            return "\"" + escaped + "\"";
        }

        private static void Check(bool success, string operation)
        {
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), operation);
        }

        private static void Close(ref IntPtr handle)
        {
            if (handle != IntPtr.Zero && handle != new IntPtr(-1)) CloseHandle(handle);
            handle = IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes
        { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
        [StructLayout(LayoutKind.Sequential)] private struct StartupInfo
        {
            public int Size; public IntPtr Reserved, Desktop, Title;
            public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            public ushort ShowWindow, ReservedBytes; public IntPtr ReservedData, StandardInput, StandardOutput, StandardError;
        }
        [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx
        { public StartupInfo Info; public IntPtr Attributes; }
        [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation
        { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimitInformation
        {
            public long ProcessTimeLimit, JobTimeLimit; public uint LimitFlags;
            public UIntPtr MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcessLimit;
            public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters
        { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimitInformation
        {
            public BasicLimitInformation Basic; public IoCounters Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
        }
        [StructLayout(LayoutKind.Sequential)] private struct BasicAccountingInformation
        {
            public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
            public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
        }
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int kind, ref ExtendedLimitInformation information, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool QueryInformationJobObject(IntPtr job, int kind, out BasicAccountingInformation information, uint length, IntPtr returnedLength);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref SecurityAttributes attributes, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFile(string name, uint access, uint share, ref SecurityAttributes attributes, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnedSize);
        [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory,
            ref StartupInfoEx startup, out ProcessInformation process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    }
}
