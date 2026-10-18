using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GabCode.Windows.Projects;

/// <summary>Starts a Git read suspended, assigns it to an unnamed kill-on-close job, then resumes it.
/// The job, unlike Process.Kill(entireProcessTree), retains descendants after the root exits.</summary>
internal sealed class OwnedGitProcess : IDisposable
{
    private readonly SafeFileHandle job;
    private readonly Process process;
    internal Process Process => process;
    internal StreamReader StandardOutput { get; }
    internal StreamReader StandardError { get; }

    private OwnedGitProcess(SafeFileHandle job, Process process, SafeFileHandle output, SafeFileHandle error)
    {
        this.job = job;
        this.process = process;
        StandardOutput = new StreamReader(new FileStream(output, FileAccess.Read, 4096, isAsync: false), Encoding.UTF8);
        StandardError = new StreamReader(new FileStream(error, FileAccess.Read, 4096, isAsync: false), Encoding.UTF8);
    }

    internal static OwnedGitProcess Start(ProcessStartInfo info)
    {
        SafeFileHandle? stdinRead = null, stdinWrite = null, stdoutRead = null, stdoutWrite = null, stderrRead = null, stderrWrite = null;
        SafeFileHandle? job = null, nativeProcess = null, thread = null;
        Process? process = null;
        IntPtr attributes = IntPtr.Zero, handleList = IntPtr.Zero, environment = IntPtr.Zero;
        bool resumed = false, attributesInitialized = false;
        try
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
            Check(CreatePipe(out stdinRead, out stdinWrite, ref security, 0), "Create Git stdin pipe");
            Check(CreatePipe(out stdoutRead, out stdoutWrite, ref security, 0), "Create Git stdout pipe");
            Check(CreatePipe(out stderrRead, out stderrWrite, ref security, 0), "Create Git stderr pipe");
            // The parent pipe ends must never escape into Git or any of its descendants.
            foreach (var handle in new[] { stdinWrite, stdoutRead, stderrRead })
                Check(SetHandleInformation(handle!, 1, 0), "Restrict Git pipe inheritance");
            nuint size = 0;
            _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((int)size));
            Check(InitializeProcThreadAttributeList(attributes, 1, 0, ref size), "Initialize Git handle list");
            attributesInitialized = true;
            handleList = Marshal.AllocHGlobal(IntPtr.Size * 3);
            Marshal.WriteIntPtr(handleList, 0, stdinRead!.DangerousGetHandle());
            Marshal.WriteIntPtr(handleList, IntPtr.Size, stdoutWrite!.DangerousGetHandle());
            Marshal.WriteIntPtr(handleList, IntPtr.Size * 2, stderrWrite!.DangerousGetHandle());
            Check(UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x00020002, handleList,
                (nuint)(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero), "Attach Git handle list");
            job = CreateJobObjectW(IntPtr.Zero, null);
            Check(!job.IsInvalid, "Create Git cleanup job");
            var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
            Check(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()), "Set Git job cleanup limit");
            var start = new StartupInfoEx
            {
                Startup = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100, // STARTF_USESTDHANDLES
                    StandardInput = stdinRead.DangerousGetHandle(),
                    StandardOutput = stdoutWrite.DangerousGetHandle(),
                    StandardError = stderrWrite.DangerousGetHandle()
                },
                Attributes = attributes
            };
            // The environment is explicit so Git's read-only/prompt guards survive native launch.
            var vars = string.Concat(info.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => pair.Key + "=" + pair.Value + '\0')) + '\0';
            environment = Marshal.StringToHGlobalUni(vars);
            var executable = info.FileName;
            var arguments = info.ArgumentList.Select(Quote);
            var command = new StringBuilder(Quote(executable) + " " + string.Join(" ", arguments));
            // Tests use disposable .cmd doubles. Production uses an executable (git.exe).
            if (Path.GetExtension(executable).Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(executable).Equals(".bat", StringComparison.OrdinalIgnoreCase))
            {
                var scriptArguments = info.ArgumentList.Select(arg => arg.IndexOfAny([' ', '\t', '"', '&', '|', '<', '>', '^']) >= 0 ? Quote(arg) : arg);
                command = new StringBuilder(Quote(Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe")) +
                    " /d /s /c \"" + Quote(executable) + " " + string.Join(" ", scriptArguments) + "\"");
            }
            Check(CreateProcessW(null, command, IntPtr.Zero, IntPtr.Zero, true,
                0x00080000 | 0x00000400 | 0x00000004 | 0x08000000, environment, info.WorkingDirectory, ref start, out var child),
                "Create suspended Git process");
            nativeProcess = new SafeFileHandle(child.Process, true);
            thread = new SafeFileHandle(child.Thread, true);
            Check(AssignProcessToJobObject(job, nativeProcess), "Assign Git process to cleanup job");
            process = Process.GetProcessById(checked((int)child.ProcessId));
            // Open/cache the Process handle while the child is suspended. A fast
            // root can otherwise exit before Process opens a handle for ExitCode.
            _ = process.SafeHandle;
            if (ResumeThread(thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error(), "Resume Git process");
            resumed = true;
            var owned = new OwnedGitProcess(job, process, stdoutRead!, stderrRead!);
            job = null; process = null; stdoutRead = null; stderrRead = null;
            return owned;
        }
        finally
        {
            if (!resumed && nativeProcess is { IsInvalid: false }) _ = TerminateProcess(nativeProcess, 1);
            process?.Dispose();
            job?.Dispose(); nativeProcess?.Dispose(); thread?.Dispose();
            stdinRead?.Dispose(); stdinWrite?.Dispose(); stdoutRead?.Dispose(); stdoutWrite?.Dispose(); stderrRead?.Dispose(); stderrWrite?.Dispose();
            if (attributesInitialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (handleList != IntPtr.Zero) Marshal.FreeHGlobal(handleList);
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
        }
    }

    internal void Stop()
    {
        if (!TerminateJobObject(job, 1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not stop owned Git job.");
    }

    internal async Task WaitForEmptyAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (true)
        {
            Check(QueryInformationJobObject(job, 1, out var accounting, (uint)Marshal.SizeOf<Accounting>(), IntPtr.Zero), "Query Git job cleanup");
            if (accounting.ActiveProcesses == 0) return;
            try { await Task.Delay(25, deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw new TimeoutException("Owned Git descendants did not exit within the cleanup limit."); }
        }
    }

    public void Dispose()
    {
        // Job handle is owned only by this read; close kills descendants even if the root exited.
        job.Dispose();
        StandardOutput.Dispose(); StandardError.Dispose(); process.Dispose();
    }

    private static string Quote(string arg)
    {
        var builder = new StringBuilder("\"");
        var slashes = 0;
        foreach (var ch in arg)
        {
            if (ch == '\\') { slashes++; continue; }
            if (ch == '"') { builder.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
            builder.Append('\\', slashes).Append(ch); slashes = 0;
        }
        return builder.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static void Check(bool succeeded, string step)
    {
        if (!succeeded) throw new Win32Exception(Marshal.GetLastWin32Error(), step);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { internal int Length; internal IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] internal bool InheritHandle; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal int Size;
        internal IntPtr Reserved, Desktop, Title;
        internal int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        internal short ShowWindow, Reserved2;
        internal IntPtr Reserved2Pointer, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { internal StartupInfo Startup; internal IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { internal IntPtr Process, Thread; internal uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        internal long ProcessTime, JobTime;
        internal uint Flags;
        internal nuint MinWorkingSet, MaxWorkingSet;
        internal uint ActiveLimit;
        internal nuint Affinity;
        internal uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits { internal BasicLimits Basic; internal IoCounters Io; internal nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Accounting { internal long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime; internal uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses; }

    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes security, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string? application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx start, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int kind, out Accounting accounting, uint size, IntPtr unused);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeFileHandle process, uint code);
}
