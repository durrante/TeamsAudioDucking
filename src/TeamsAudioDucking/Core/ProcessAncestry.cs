using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TeamsAudioDucking.Core;

/// <summary>
/// Attributes helper processes to Teams by walking the parent-process chain.
/// The new Teams client plays its UI sounds - the incoming-call ringtone
/// included - through a WebView2 child process (msedgewebview2.exe), so a
/// session's own process name is not always enough to recognise Teams audio.
/// Verdicts are cached per (pid, process start time); a parent must predate
/// its child, so a recycled PID cannot fake a Teams ancestor.
/// </summary>
internal static class ProcessAncestry
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        nint processHandle, int processInformationClass,
        ref ProcessBasicInformation processInformation, int processInformationLength, out int returnLength);

    private const int MaxDepth = 8;
    private static readonly object CacheLock = new();
    private static readonly Dictionary<int, (long StartTicks, bool IsTeams)> Cache = new();

    public static bool IsTeamsDescendant(int pid, AppSettings settings)
    {
        if (pid <= 4) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            long startTicks = SafeStartTicks(process);
            lock (CacheLock)
            {
                if (Cache.TryGetValue(pid, out var cached) && cached.StartTicks == startTicks)
                    return cached.IsTeams;
            }

            bool isTeams = WalkParents(process, startTicks, settings);

            lock (CacheLock)
            {
                if (Cache.Count >= 512) Cache.Clear();
                Cache[pid] = (startTicks, isTeams);
            }
            return isTeams;
        }
        catch
        {
            return false;
        }
    }

    private static bool WalkParents(Process child, long childStartTicks, AppSettings settings)
    {
        long previousStartTicks = childStartTicks;
        int parentPid = GetParentPid(child);
        for (int depth = 0; depth < MaxDepth && parentPid > 4; depth++)
        {
            try
            {
                using var parent = Process.GetProcessById(parentPid);
                long parentStartTicks = SafeStartTicks(parent);
                // A real parent must have started before its child; otherwise
                // the parent PID has been recycled by an unrelated process.
                if (previousStartTicks != 0 && parentStartTicks != 0 && parentStartTicks > previousStartTicks)
                    return false;
                if (settings.IsTeamsProcess(parent.ProcessName)) return true;
                previousStartTicks = parentStartTicks;
                parentPid = GetParentPid(parent);
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    private static int GetParentPid(Process process)
    {
        try
        {
            var info = new ProcessBasicInformation();
            if (NtQueryInformationProcess(process.Handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0)
                return 0;
            return (int)info.InheritedFromUniqueProcessId;
        }
        catch
        {
            return 0;
        }
    }

    private static long SafeStartTicks(Process process)
    {
        try { return process.StartTime.Ticks; }
        catch { return 0; }
    }
}
