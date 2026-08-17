using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace TeamsAudioDucking.Core;

/// <summary>
/// Detects an active Microsoft Teams call/meeting using two signals:
///
/// 1. Microphone (authoritative): Teams holds an open microphone capture
///    stream for the whole duration of a connected call - even while muted
///    inside Teams. Windows tracks per-app mic usage in the
///    CapabilityAccessManager ConsentStore registry key (LastUsedTimeStop == 0
///    while in use), which we watch with RegNotifyChangeKeyValue
///    (event-driven, no polling). A process-liveness check makes a Teams
///    crash end the call state immediately.
///
/// 2. Playback (ring heuristic): while an outbound call is ringing (or an
///    incoming call is ringing), Teams has not opened the mic yet, but it IS
///    playing the ring tone through its own render session. AudioDucker
///    measures how loud Teams' own sessions actually are and reports sustained
///    real sound here, which counts as the call starting. Chat pings and other
///    short notification sounds are too brief to qualify (the test lives in
///    AudioDucker, where the sessions are). Once the mic has been seen in a
///    call, the mic signal alone decides when the call ends, so lingering
///    playback cannot delay the restore.
///
/// A short debounce is applied to the "call ended" transition because Teams
/// can briefly release the mic when switching audio devices mid-call.
/// </summary>
public sealed class TeamsCallDetector : IDisposable
{
    private const string ConsentStorePath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    // Matches: packaged new Teams ("MSTeams_8wekyb3d8bbwe"), Teams' media
    // engine host ("Microsoft.Teams.SlimCoreVdiHost..."), non-packaged new
    // Teams ("...#ms-teams.exe"), classic Teams ("...#Teams.exe") and the
    // local AVD media-optimisation client ("...#MsTeamsVdi.exe").
    private static readonly string[] RegistryTokens = { "msteams_", "slimcorevdihost", "ms-teams.exe", "teams.exe", "msteamsvdi" };

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
    private bool _playbackActive;    // Teams making a sustained sound (per AudioDucker)
    private bool _ringQualified;     // that sound counts as a ring/call
    private bool _micSeenThisCall;   // once the mic was used, only the mic ends the call
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
        Logger.Info("Teams call detector started (microphone ConsentStore watcher + playback heuristic)");
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
    /// Called by AudioDucker (any thread) when Teams starts or stops making a
    /// sustained sound. AudioDucker has already ruled out chat pings and other
    /// brief noises, so this counts as "ringing/starting a call" as it stands.
    /// </summary>
    public void NotifyTeamsPlayback(bool playing)
    {
        bool evaluate = false;
        lock (_lock)
        {
            if (_disposed) return;
            _playbackActive = playing;
            if (playing != _ringQualified)
            {
                _ringQualified = playing;
                evaluate = true; // started ringing, or stopped without connecting
            }
        }
        if (evaluate)
        {
            if (playing) Logger.Info("Teams playback sustained (ringing or call audio)");
            Evaluate();
        }
    }

    /// <summary>
    /// Re-checks the call state now. Cheap; also called from the slow
    /// reconciliation timer and after resume from sleep.
    /// </summary>
    public void Evaluate()
    {
        bool mic;
        try
        {
            mic = TeamsProcessRunning() && TeamsMicInUse();
        }
        catch (Exception ex)
        {
            Logger.Error("Call-state evaluation failed", ex);
            return;
        }

        bool? fire = null;
        string trigger = "microphone";
        lock (_lock)
        {
            if (_disposed) return;
            if (_inCall && mic) _micSeenThisCall = true;
            bool ring = _ringQualified && _settings.MuteWhileRinging;
            // Once the mic has been in use, lingering playback (hang-up tone,
            // Teams UI sounds) must not keep the call alive.
            bool raw = _micSeenThisCall ? mic : (mic || ring);

            if (raw)
            {
                _endDebounceTimer?.Dispose();
                _endDebounceTimer = null;
                if (!_inCall)
                {
                    _inCall = true;
                    if (mic) _micSeenThisCall = true;
                    else trigger = "ringing";
                    fire = true;
                }
            }
            else if (_inCall && _endDebounceTimer == null)
            {
                _endDebounceTimer = new System.Threading.Timer(_ => ConfirmEnded(), null, EndDebounceMs, Timeout.Infinite);
            }
        }

        if (fire.HasValue)
        {
            Logger.Info($"Call state -> active (trigger: {trigger})");
            CallStateChanged?.Invoke(fire.Value);
        }
    }

    private void ConfirmEnded()
    {
        bool mic;
        try
        {
            mic = TeamsProcessRunning() && TeamsMicInUse();
        }
        catch
        {
            mic = false;
        }

        bool fire = false;
        lock (_lock)
        {
            if (_disposed) return;
            _endDebounceTimer?.Dispose();
            _endDebounceTimer = null;
            bool ring = _ringQualified && _settings.MuteWhileRinging;
            bool raw = _micSeenThisCall ? mic : (mic || ring);
            if (!raw && _inCall)
            {
                _inCall = false;
                _micSeenThisCall = false;
                // Require a fresh sustained-playback episode to re-enter via
                // the ring path (prevents a hang-up tone re-triggering).
                _ringQualified = false;
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
