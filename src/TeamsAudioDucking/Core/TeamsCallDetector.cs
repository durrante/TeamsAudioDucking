using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace TeamsAudioDucking.Core;

/// <summary>
/// Detects an active Microsoft Teams call/meeting.
///
/// Approach: there is no public local API for the new Teams client's call state.
/// The most reliable Windows-level signal is that Teams holds an open microphone
/// capture stream for the whole duration of a call or meeting (even while you are
/// muted inside Teams - it keeps the stream open so unmute is instant).
///
/// Windows tracks per-app microphone usage in the CapabilityAccessManager
/// ConsentStore registry key: while an app is using the mic, its
/// LastUsedTimeStop value is 0. We watch that key with RegNotifyChangeKeyValue
/// (event-driven, no polling) and treat "Teams is using the microphone AND a
/// Teams process is running" as "in a call". The process check makes a Teams
/// crash mid-call end the state immediately even if the registry lags.
///
/// A short debounce is applied to the "call ended" transition because Teams can
/// briefly release and reacquire the microphone when switching audio devices.
/// </summary>
public sealed class TeamsCallDetector : IDisposable
{
    private const string ConsentStorePath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    // Matches: packaged new Teams ("MSTeams_8wekyb3d8bbwe"), Teams' media
    // engine host ("Microsoft.Teams.SlimCoreVdiHost..."), non-packaged new
    // Teams ("...#ms-teams.exe") and classic Teams ("...#Teams.exe").
    private static readonly string[] RegistryTokens = { "msteams_", "slimcorevdihost", "ms-teams.exe", "teams.exe" };

    private const int EndDebounceMs = 2000;

    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(
        SafeRegistryHandle hKey, bool bWatchSubtree, int dwNotifyFilter, SafeWaitHandle hEvent, bool fAsynchronous);

    private const int RegNotifyChangeName = 0x1;
    private const int RegNotifyChangeLastSet = 0x4;
    private const int RegNotifyThreadAgnostic = 0x10000000;

    private readonly AppSettings _settings;
    private readonly object _lock = new();
    private readonly ManualResetEvent _stopEvent = new(false);
    private readonly AutoResetEvent _changeEvent = new(false);
    private Thread? _watcherThread;
    private System.Threading.Timer? _endDebounceTimer;
    private volatile bool _inCall;
    private bool _disposed;

    public bool InCall => _inCall;

    /// <summary>Raised on a background thread when the call state changes.</summary>
    public event Action<bool>? CallStateChanged;

    public TeamsCallDetector(AppSettings settings)
    {
        _settings = settings;
    }

    public void Start()
    {
        // Handles "Teams already in a call when the utility starts".
        Evaluate();
        _watcherThread = new Thread(WatchLoop) { IsBackground = true, Name = "TeamsCallWatcher" };
        _watcherThread.Start();
        Logger.Info("Teams call detector started (microphone ConsentStore watcher)");
    }

    private void WatchLoop()
    {
        while (!_stopEvent.WaitOne(0))
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(ConsentStorePath);
                if (key == null)
                {
                    // Key missing (unusual); retry later.
                    if (_stopEvent.WaitOne(15000)) return;
                    continue;
                }

                while (!_stopEvent.WaitOne(0))
                {
                    int hr = RegNotifyChangeKeyValue(
                        key.Handle, true,
                        RegNotifyChangeName | RegNotifyChangeLastSet | RegNotifyThreadAgnostic,
                        _changeEvent.SafeWaitHandle, true);
                    if (hr != 0)
                    {
                        Logger.Warn($"RegNotifyChangeKeyValue failed (0x{hr:X8}); retrying");
                        if (_stopEvent.WaitOne(5000)) return;
                        break; // reopen the key
                    }

                    int signalled = WaitHandle.WaitAny(new WaitHandle[] { _changeEvent, _stopEvent });
                    if (signalled == 1) return;
                    Evaluate();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Registry watcher error; retrying in 15s", ex);
                if (_stopEvent.WaitOne(15000)) return;
            }
        }
    }

    /// <summary>
    /// Re-checks the call state now. Cheap; also called from the slow
    /// reconciliation timer and after resume from sleep.
    /// </summary>
    public void Evaluate()
    {
        bool raw;
        try
        {
            raw = TeamsMicInUse() && TeamsProcessRunning();
        }
        catch (Exception ex)
        {
            Logger.Error("Call-state evaluation failed", ex);
            return;
        }

        bool? fire = null;
        lock (_lock)
        {
            if (_disposed) return;
            if (raw)
            {
                _endDebounceTimer?.Dispose();
                _endDebounceTimer = null;
                if (!_inCall)
                {
                    _inCall = true;
                    fire = true;
                }
            }
            else if (_inCall && _endDebounceTimer == null)
            {
                // Confirm the end after a short delay to ignore brief mic
                // releases (e.g. Teams switching audio devices mid-call).
                _endDebounceTimer = new System.Threading.Timer(_ => ConfirmEnded(), null, EndDebounceMs, Timeout.Infinite);
            }
        }

        if (fire.HasValue) CallStateChanged?.Invoke(fire.Value);
    }

    private void ConfirmEnded()
    {
        bool raw;
        try
        {
            raw = TeamsMicInUse() && TeamsProcessRunning();
        }
        catch
        {
            raw = false;
        }

        bool fire = false;
        lock (_lock)
        {
            if (_disposed) return;
            _endDebounceTimer?.Dispose();
            _endDebounceTimer = null;
            if (!raw && _inCall)
            {
                _inCall = false;
                fire = true;
            }
        }

        if (fire) CallStateChanged?.Invoke(false);
    }

    private static bool TeamsMicInUse()
    {
        using var root = Registry.CurrentUser.OpenSubKey(ConsentStorePath);
        if (root == null) return false;

        foreach (var name in root.GetSubKeyNames())
        {
            if (name.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
            {
                using var nonPackaged = root.OpenSubKey(name);
                if (nonPackaged == null) continue;
                foreach (var npName in nonPackaged.GetSubKeyNames())
                {
                    if (MatchesTeams(npName) && EntryInUse(nonPackaged, npName)) return true;
                }
            }
            else if (MatchesTeams(name) && EntryInUse(root, name))
            {
                return true;
            }
        }
        return false;
    }

    private static bool MatchesTeams(string keyName)
    {
        var n = keyName.ToLowerInvariant();
        return RegistryTokens.Any(t => n.Contains(t));
    }

    private static bool EntryInUse(RegistryKey parent, string subKeyName)
    {
        using var key = parent.OpenSubKey(subKeyName);
        if (key == null) return false;
        return key.GetValue("LastUsedTimeStart") is long start && start != 0
            && key.GetValue("LastUsedTimeStop") is long stop && stop == 0;
    }

    private bool TeamsProcessRunning()
    {
        foreach (var name in _settings.TeamsProcessNames)
        {
            var processes = Process.GetProcessesByName(AppSettings.Normalize(name));
            bool any = processes.Length > 0;
            foreach (var p in processes) p.Dispose();
            if (any) return true;
        }
        return false;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _endDebounceTimer?.Dispose();
            _endDebounceTimer = null;
        }
        _stopEvent.Set();
        _watcherThread?.Join(2000);
        _stopEvent.Dispose();
        _changeEvent.Dispose();
    }
}
