using System.Diagnostics;
using System.IO;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace TeamsAudioDucking.Core;

/// <summary>
/// Mutes and restores per-application audio sessions using the Windows Core
/// Audio (WASAPI) session APIs. Never touches the master volume, the
/// microphone, the default device or any Teams session.
///
/// Event-driven: new sessions are caught via IAudioSessionNotification
/// (OnSessionCreated) on every active render device, and device hot-plug via
/// IMMNotificationClient. A slow reconciliation sweep (driven by the app's 5s
/// timer, and only doing real work during a call) acts as a safety net.
/// </summary>
public sealed class AudioDucker : IDisposable
{
    private sealed class DeviceHolder
    {
        public string Id = "";
        public string Name = "";
        public MMDevice Device = null!;
        public AudioSessionManager Manager = null!;
        public AudioSessionManager.SessionCreatedDelegate? Handler;
    }

    private sealed class TeamsSessionEntry
    {
        public AudioSessionControl Control = null!;
        public TeamsSessionEventsHandler Handler = null!;
        public string ProcessName = "";
        public bool Active;
        /// <summary>
        /// Only calibrated sessions count towards "Teams is playing audio".
        /// Sessions we saw being created are trusted immediately; sessions
        /// found by a scan must first be seen Inactive once, so a Teams
        /// session that happens to sit permanently Active cannot fake a
        /// perpetual ring.
        /// </summary>
        public bool Calibrated;
    }

    private sealed class TeamsSessionEventsHandler : IAudioSessionEventsHandler
    {
        private readonly AudioDucker _owner;
        private readonly string _key;
        public TeamsSessionEventsHandler(AudioDucker owner, string key) { _owner = owner; _key = key; }
        public void OnStateChanged(AudioSessionState state) => _owner.OnTeamsSessionStateChanged(_key, state);
        public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason)
            => _owner.OnTeamsSessionStateChanged(_key, AudioSessionState.AudioSessionStateExpired);
        public void OnVolumeChanged(float volume, bool isMuted) { }
        public void OnDisplayNameChanged(string displayName) { }
        public void OnIconPathChanged(string iconPath) { }
        public void OnChannelVolumeChanged(uint channelCount, nint newVolumes, uint channelIndex) { }
        public void OnGroupingParamChanged(ref Guid groupingId) { }
    }

    private sealed class EndpointNotificationClient : IMMNotificationClient
    {
        private readonly AudioDucker _owner;
        public EndpointNotificationClient(AudioDucker owner) => _owner = owner;
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _owner.ScheduleDeviceRefresh();
        public void OnDeviceAdded(string pwstrDeviceId) => _owner.ScheduleDeviceRefresh();
        public void OnDeviceRemoved(string deviceId) => _owner.ScheduleDeviceRefresh();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }

    private sealed class PersistedState
    {
        public List<MuteRecord> Records { get; set; } = new();
        public List<MuteRecord> Pending { get; set; } = new();
    }

    private readonly AppSettings _settings;
    private readonly object _lock = new();
    private readonly Dictionary<string, DeviceHolder> _devices = new();
    private readonly int _ownPid = Environment.ProcessId;
    private List<MuteRecord> _records = new();   // sessions we muted, awaiting restore
    private List<MuteRecord> _pending = new();   // sessions that vanished while muted; restore when they reappear
    private readonly Dictionary<string, TeamsSessionEntry> _teamsSessions = new();
    private MMDeviceEnumerator? _enumerator;
    private EndpointNotificationClient? _notificationClient;
    private System.Threading.Timer? _deviceRefreshTimer;
    private bool _teamsPlaybackActive;
    private bool _ducking;
    private bool _disposed;

    private static string StatePath => Path.Combine(AppSettings.DataDirectory, "muted-state.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>Raised (on arbitrary threads) whenever the muted set changes.</summary>
    public event Action? StateChanged;

    /// <summary>
    /// Raised (on arbitrary threads) when Teams starts/stops playing audio
    /// through any of its render sessions. Feeds the ring-detection heuristic
    /// in <see cref="TeamsCallDetector"/>.
    /// </summary>
    public event Action<bool>? TeamsPlaybackChanged;

    public bool IsDucking { get { lock (_lock) return _ducking; } }
    public int MutedCount { get { lock (_lock) return _records.Count; } }
    public bool HasLeftoverRecords { get { lock (_lock) return _records.Count > 0; } }

    public AudioDucker(AppSettings settings) => _settings = settings;

    public void Start()
    {
        int deviceCount;
        bool playbackChanged, playbackNow;
        lock (_lock)
        {
            _enumerator = new MMDeviceEnumerator();
            _notificationClient = new EndpointNotificationClient(this);
            _enumerator.RegisterEndpointNotificationCallback(_notificationClient);
            RefreshDevicesLocked();
            LoadStateLocked();
            playbackChanged = ScanTeamsSessionsLocked();
            playbackNow = _teamsPlaybackActive;
            deviceCount = _devices.Count;
        }
        Logger.Info($"Audio ducker started ({deviceCount} active render device(s))");
        if (playbackChanged) TeamsPlaybackChanged?.Invoke(playbackNow);
    }

    // ------------------------------------------------------------------ mute

    public void DuckAll(string reason)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _ducking = true;
            Logger.Info($"Muting other applications ({reason})");
            RefreshDevicesLocked();
            foreach (var holder in _devices.Values) MuteDeviceSessionsLocked(holder);
            PersistLocked();
        }
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Safety-net sweep while a call is active: catches sessions whose creation
    /// event was missed (e.g. across sleep/resume). No-op when not ducking.
    /// </summary>
    public void EnsureDucked()
    {
        bool changed;
        lock (_lock)
        {
            if (_disposed || !_ducking) return;
            int before = _records.Count;
            RefreshDevicesLocked();
            foreach (var holder in _devices.Values) MuteDeviceSessionsLocked(holder);
            changed = _records.Count != before;
            if (changed) PersistLocked();
        }
        if (changed) StateChanged?.Invoke();
    }

    private void MuteDeviceSessionsLocked(DeviceHolder holder)
    {
        foreach (var session in GetSessionsLocked(holder))
        {
            try
            {
                TryMuteSessionLocked(holder, session);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to process a session on '{holder.Name}': {ex.Message}");
            }
        }
    }

    private void TryMuteSessionLocked(DeviceHolder holder, AudioSessionControl session)
    {
        if (session.State == AudioSessionState.AudioSessionStateExpired) return;

        bool isSystemSounds = session.IsSystemSoundsSession;
        int pid = 0;
        string? processName;
        if (isSystemSounds)
        {
            processName = "System Sounds";
        }
        else
        {
            try { pid = (int)session.GetProcessID; } catch { return; }
            if (pid == 0 || pid == _ownPid) return;
            processName = GetProcessName(pid);
            if (processName == null) return; // process already gone
        }

        // Teams is exempt no matter what - covers every Teams audio session,
        // on any device, including ones created mid-call.
        if (_settings.IsTeamsProcess(processName)) return;
        if (isSystemSounds && !_settings.MuteSystemSounds) return;
        if (!_settings.IsAlwaysMuted(processName) && _settings.IsExcluded(processName)) return;

        string instanceId = SafeInstanceId(session);

        // Already handled this exact session.
        var existing = instanceId.Length > 0
            ? _records.FirstOrDefault(r => r.SessionInstanceId == instanceId)
            : null;
        if (existing != null)
        {
            try
            {
                var v = session.SimpleAudioVolume;
                if (!v.Mute) v.Mute = true; // someone unmuted it; re-assert
            }
            catch { }
            return;
        }

        // An app we muted earlier restarted (or re-created its session) during
        // the call. Windows persists per-app mute, so the new session starts
        // muted - carry the ORIGINAL pre-call state over to the new session so
        // the eventual restore is still correct.
        var orphan = _records.FirstOrDefault(r =>
            r.IsSystemSounds == isSystemSounds &&
            AppSettings.Normalize(r.ProcessName) == AppSettings.Normalize(processName) &&
            r.SessionInstanceId != instanceId &&
            !ProcessAlive(r.Pid, r.ProcessName));
        if (orphan != null)
        {
            orphan.SessionInstanceId = instanceId;
            orphan.Pid = pid;
            orphan.DeviceId = holder.Id;
            try { session.SimpleAudioVolume.Mute = true; } catch { }
            Logger.Info($"Re-muted restarted application: {processName}");
            return;
        }

        SimpleAudioVolume volume;
        try
        {
            volume = session.SimpleAudioVolume;
            if (volume.Mute) return; // already muted before the call: leave it alone entirely
        }
        catch
        {
            return;
        }

        var record = new MuteRecord
        {
            DeviceId = holder.Id,
            SessionInstanceId = instanceId,
            Pid = pid,
            ProcessName = processName,
            IsSystemSounds = isSystemSounds,
            WasMuted = false,
            PreviousVolume = SafeVolume(volume),
            MutedAtUtc = DateTime.UtcNow,
        };

        try
        {
            volume.Mute = true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not mute {processName}: {ex.Message}");
            return;
        }

        _records.Add(record);
        Logger.Info($"Muted: {processName} (volume was {(int)(record.PreviousVolume * 100)}%, device '{holder.Name}')");
    }

    // --------------------------------------------------------------- restore

    public void RestoreAll(string reason)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _ducking = false;
            if (_records.Count == 0)
            {
                PersistLocked();
                Logger.Info($"Restore requested ({reason}); nothing to restore");
            }
            else
            {
                Logger.Info($"Restoring application audio ({reason})");
                RefreshDevicesLocked();

                var sessionsByDevice = new Dictionary<string, List<AudioSessionControl>>();
                foreach (var holder in _devices.Values)
                    sessionsByDevice[holder.Id] = GetSessionsLocked(holder);

                var stillPending = new List<MuteRecord>();
                foreach (var record in _records)
                {
                    if (TryRestoreLocked(record, sessionsByDevice))
                    {
                        Logger.Info($"Restored: {record.ProcessName} (volume {(int)(record.PreviousVolume * 100)}%, muted={record.WasMuted})");
                    }
                    else
                    {
                        stillPending.Add(record);
                        Logger.Warn($"Session for {record.ProcessName} no longer present; will restore it if it reappears");
                    }
                }
                _records = new List<MuteRecord>();
                _pending.AddRange(stillPending);
                PrunePendingLocked();
                PersistLocked();
            }
        }
        StateChanged?.Invoke();
    }

    private bool TryRestoreLocked(MuteRecord record, Dictionary<string, List<AudioSessionControl>> sessionsByDevice)
    {
        // Pass 1: exact session instance. Pass 2: same process, currently muted
        // session (covers an app that restarted and got a new instance id).
        foreach (bool exact in new[] { true, false })
        {
            foreach (var deviceId in OrderedDeviceIds(sessionsByDevice, record.DeviceId))
            {
                foreach (var session in sessionsByDevice[deviceId])
                {
                    try
                    {
                        if (session.State == AudioSessionState.AudioSessionStateExpired) continue;
                        if (exact)
                        {
                            if (record.SessionInstanceId.Length == 0) continue;
                            if (SafeInstanceId(session) != record.SessionInstanceId) continue;
                        }
                        else if (record.IsSystemSounds)
                        {
                            if (!session.IsSystemSoundsSession) continue;
                            if (!session.SimpleAudioVolume.Mute) continue;
                        }
                        else
                        {
                            if (session.IsSystemSoundsSession) continue;
                            int pid;
                            try { pid = (int)session.GetProcessID; } catch { continue; }
                            var name = GetProcessName(pid);
                            if (name == null) continue;
                            if (AppSettings.Normalize(name) != AppSettings.Normalize(record.ProcessName)) continue;
                            if (!session.SimpleAudioVolume.Mute) continue; // only touch sessions that are actually muted
                        }

                        var volume = session.SimpleAudioVolume;
                        volume.Mute = record.WasMuted;
                        volume.Volume = record.PreviousVolume;
                        return true;
                    }
                    catch
                    {
                        // Session died under us; keep looking.
                    }
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Called at startup: if the previous run was terminated while sessions
    /// were muted, put everything back.
    /// </summary>
    public void RestoreLeftoversFromPreviousRun()
    {
        if (!HasLeftoverRecords) return;
        Logger.Info("Found un-restored sessions from a previous run; recovering");
        RestoreAll("recovery from previous run");
    }

    // ---------------------------------------------------- new session events

    private void HandleSessionCreated(string deviceId, IAudioSessionControl rawSession)
    {
        // Keep the COM callback itself fast; do the work on the pool.
        Task.Run(() =>
        {
            bool changed = false;
            bool playbackChanged = false, playbackNow = false;
            try
            {
                var session = new AudioSessionControl(rawSession);
                lock (_lock)
                {
                    if (_disposed) return;
                    if (!_devices.TryGetValue(deviceId, out var holder))
                    {
                        RefreshDevicesLocked();
                        _devices.TryGetValue(deviceId, out holder);
                    }
                    if (holder == null) return;

                    var processName = ResolveSessionProcessName(session, out _);
                    if (_settings.TraceSessionEvents)
                        Logger.Info($"[trace] Session created: {processName ?? "<gone>"} on '{holder.Name}', state={SafeState(session)}");

                    // Teams' own sessions are never muted, but we watch their
                    // activity for ring detection. A session we see being
                    // created is trusted immediately (an outbound ring makes a
                    // fresh, already-active session).
                    if (processName != null && _settings.IsTeamsProcess(processName))
                    {
                        RegisterTeamsSessionLocked(session, processName, trustInitialState: true);
                        playbackChanged = RecomputeTeamsPlaybackLocked();
                        playbackNow = _teamsPlaybackActive;
                    }
                    else if (_ducking)
                    {
                        int before = _records.Count;
                        TryMuteSessionLocked(holder, session);
                        changed = _records.Count != before;
                        if (changed) PersistLocked();
                    }
                    else
                    {
                        changed = TryApplyPendingLocked(session);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"New-session handling failed: {ex.Message}");
            }
            if (playbackChanged) TeamsPlaybackChanged?.Invoke(playbackNow);
            if (changed) StateChanged?.Invoke();
        });
    }

    // ------------------------------------------- Teams playback (ring) watch

    private void RegisterTeamsSessionLocked(AudioSessionControl session, string processName, bool trustInitialState)
    {
        string key = SafeInstanceId(session);
        if (key.Length == 0) key = Guid.NewGuid().ToString("N");
        if (_teamsSessions.ContainsKey(key)) return;

        var entry = new TeamsSessionEntry
        {
            Control = session,
            ProcessName = processName,
            Handler = new TeamsSessionEventsHandler(this, key),
        };
        try
        {
            entry.Active = session.State == AudioSessionState.AudioSessionStateActive;
            session.RegisterEventClient(entry.Handler);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not watch Teams session ({processName}): {ex.Message}");
            return;
        }
        // Scan-discovered sessions must be seen Inactive once before their
        // Active state counts (see TeamsSessionEntry.Calibrated).
        entry.Calibrated = trustInitialState || !entry.Active;
        _teamsSessions[key] = entry;
        if (_settings.TraceSessionEvents)
            Logger.Info($"[trace] Watching Teams session: {processName} (active={entry.Active}, calibrated={entry.Calibrated})");
    }

    private void OnTeamsSessionStateChanged(string key, AudioSessionState state)
    {
        bool playbackChanged, playbackNow;
        lock (_lock)
        {
            if (_disposed) return;
            if (!_teamsSessions.TryGetValue(key, out var entry)) return;

            if (state == AudioSessionState.AudioSessionStateExpired)
            {
                UnregisterTeamsSessionLocked(key, entry);
            }
            else
            {
                entry.Active = state == AudioSessionState.AudioSessionStateActive;
                if (state == AudioSessionState.AudioSessionStateInactive) entry.Calibrated = true;
            }
            playbackChanged = RecomputeTeamsPlaybackLocked();
            playbackNow = _teamsPlaybackActive;
        }
        if (playbackChanged) TeamsPlaybackChanged?.Invoke(playbackNow);
    }

    private bool RecomputeTeamsPlaybackLocked()
    {
        bool active = _teamsSessions.Values.Any(e => e.Calibrated && e.Active);
        if (active == _teamsPlaybackActive) return false;
        _teamsPlaybackActive = active;
        if (_settings.TraceSessionEvents)
            Logger.Info($"[trace] Teams playback -> {(active ? "active" : "inactive")}");
        return true;
    }

    /// <summary>
    /// Safety net: prunes dead Teams sessions and registers any this instance
    /// has not seen yet (with calibration required). Returns true if the
    /// overall playback state changed.
    /// </summary>
    private bool ScanTeamsSessionsLocked()
    {
        foreach (var (key, entry) in _teamsSessions.ToList())
        {
            bool dead;
            try { dead = entry.Control.State == AudioSessionState.AudioSessionStateExpired; }
            catch { dead = true; }
            if (dead) UnregisterTeamsSessionLocked(key, entry);
        }

        foreach (var holder in _devices.Values)
        {
            foreach (var session in GetSessionsLocked(holder))
            {
                try
                {
                    if (session.State == AudioSessionState.AudioSessionStateExpired) continue;
                    string id = SafeInstanceId(session);
                    if (id.Length > 0 && _teamsSessions.ContainsKey(id)) continue;
                    var processName = ResolveSessionProcessName(session, out bool isSystemSounds);
                    if (isSystemSounds || processName == null) continue;
                    if (_settings.IsTeamsProcess(processName))
                        RegisterTeamsSessionLocked(session, processName, trustInitialState: false);
                }
                catch { }
            }
        }
        return RecomputeTeamsPlaybackLocked();
    }

    private void UnregisterTeamsSessionLocked(string key, TeamsSessionEntry entry)
    {
        _teamsSessions.Remove(key);
        try { entry.Control.UnRegisterEventClient(entry.Handler); } catch { }
        if (_settings.TraceSessionEvents)
            Logger.Info($"[trace] Teams session gone: {entry.ProcessName}");
    }

    /// <summary>Public wrapper used after resume from sleep and by the reconcile sweep.</summary>
    public void RescanTeamsSessions()
    {
        bool playbackChanged, playbackNow;
        lock (_lock)
        {
            if (_disposed) return;
            playbackChanged = ScanTeamsSessionsLocked();
            playbackNow = _teamsPlaybackActive;
        }
        if (playbackChanged) TeamsPlaybackChanged?.Invoke(playbackNow);
    }

    /// <summary>Diagnostic dump of every session on every device (trace mode).</summary>
    public void LogSessionSnapshot()
    {
        lock (_lock)
        {
            if (_disposed) return;
            foreach (var holder in _devices.Values)
            {
                foreach (var session in GetSessionsLocked(holder))
                {
                    try
                    {
                        var name = ResolveSessionProcessName(session, out bool sys) ?? "<gone>";
                        Logger.Info($"[trace] '{holder.Name}': {(sys ? "System Sounds" : name)} state={SafeState(session)} muted={session.SimpleAudioVolume.Mute}");
                    }
                    catch { }
                }
            }
        }
    }

    /// <summary>
    /// After a call, an app that was closed while muted may come back with its
    /// Windows-persisted muted state. When its session reappears, restore it.
    /// </summary>
    private bool TryApplyPendingLocked(AudioSessionControl session)
    {
        if (_pending.Count == 0) return false;

        bool isSystemSounds = session.IsSystemSoundsSession;
        string? processName;
        if (isSystemSounds)
        {
            processName = "System Sounds";
        }
        else
        {
            int pid;
            try { pid = (int)session.GetProcessID; } catch { return false; }
            processName = GetProcessName(pid);
            if (processName == null) return false;
        }

        var match = _pending.FirstOrDefault(p =>
            p.IsSystemSounds == isSystemSounds &&
            AppSettings.Normalize(p.ProcessName) == AppSettings.Normalize(processName));
        if (match == null) return false;

        try
        {
            var volume = session.SimpleAudioVolume;
            volume.Mute = match.WasMuted;
            volume.Volume = match.PreviousVolume;
        }
        catch
        {
            return false;
        }

        _pending.Remove(match);
        PersistLocked();
        Logger.Info($"Restored (after app restart): {processName} to {(int)(match.PreviousVolume * 100)}%");
        return true;
    }

    // ----------------------------------------------------------- device mgmt

    /// <summary>Public wrapper used after resume from sleep.</summary>
    public void RefreshDevices()
    {
        lock (_lock)
        {
            if (_disposed) return;
            RefreshDevicesLocked();
        }
    }

    private void RefreshDevicesLocked()
    {
        if (_enumerator == null) return;
        var seen = new HashSet<string>();
        try
        {
            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                seen.Add(device.ID);
                if (_devices.ContainsKey(device.ID))
                {
                    device.Dispose(); // already tracked; discard the duplicate wrapper
                    continue;
                }

                var holder = new DeviceHolder { Id = device.ID, Device = device };
                try { holder.Name = device.FriendlyName; } catch { holder.Name = device.ID; }
                try
                {
                    holder.Manager = device.AudioSessionManager;
                    holder.Handler = (_, newSession) => HandleSessionCreated(holder.Id, newSession);
                    holder.Manager.OnSessionCreated += holder.Handler;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Could not watch sessions on '{holder.Name}': {ex.Message}");
                    device.Dispose();
                    continue;
                }
                _devices[holder.Id] = holder;
                Logger.Info($"Watching audio device: {holder.Name}");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Device enumeration failed", ex);
        }

        foreach (var staleId in _devices.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            var holder = _devices[staleId];
            _devices.Remove(staleId);
            try { if (holder.Handler != null) holder.Manager.OnSessionCreated -= holder.Handler; } catch { }
            try { holder.Device.Dispose(); } catch { }
            Logger.Info($"Audio device removed: {holder.Name}");
        }
    }

    private void ScheduleDeviceRefresh()
    {
        // Debounce: device notifications arrive in bursts.
        lock (_lock)
        {
            if (_disposed) return;
            _deviceRefreshTimer?.Dispose();
            _deviceRefreshTimer = new System.Threading.Timer(_ =>
            {
                try
                {
                    bool playbackChanged, playbackNow;
                    lock (_lock)
                    {
                        if (_disposed) return;
                        RefreshDevicesLocked();
                        if (_ducking)
                        {
                            foreach (var holder in _devices.Values) MuteDeviceSessionsLocked(holder);
                            PersistLocked();
                        }
                        playbackChanged = ScanTeamsSessionsLocked();
                        playbackNow = _teamsPlaybackActive;
                    }
                    if (playbackChanged) TeamsPlaybackChanged?.Invoke(playbackNow);
                    StateChanged?.Invoke();
                }
                catch (Exception ex)
                {
                    Logger.Error("Device refresh failed", ex);
                }
            }, null, 1000, Timeout.Infinite);
        }
    }

    // --------------------------------------------------------------- helpers

    private List<AudioSessionControl> GetSessionsLocked(DeviceHolder holder)
    {
        var list = new List<AudioSessionControl>();
        try
        {
            holder.Manager.RefreshSessions();
            var collection = holder.Manager.Sessions;
            for (int i = 0; i < collection.Count; i++)
            {
                try { list.Add(collection[i]); } catch { }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not enumerate sessions on '{holder.Name}': {ex.Message}");
        }
        return list;
    }

    private static IEnumerable<string> OrderedDeviceIds(Dictionary<string, List<AudioSessionControl>> map, string preferred)
    {
        if (map.ContainsKey(preferred)) yield return preferred;
        foreach (var id in map.Keys)
        {
            if (id != preferred) yield return id;
        }
    }

    /// <summary>Process name for a session, or "System Sounds"; null if the process is gone.</summary>
    private string? ResolveSessionProcessName(AudioSessionControl session, out bool isSystemSounds)
    {
        isSystemSounds = false;
        try
        {
            if (session.IsSystemSoundsSession)
            {
                isSystemSounds = true;
                return "System Sounds";
            }
            int pid = (int)session.GetProcessID;
            if (pid == 0 || pid == _ownPid) return null;
            return GetProcessName(pid);
        }
        catch
        {
            return null;
        }
    }

    private static string SafeState(AudioSessionControl session)
    {
        try
        {
            return session.State switch
            {
                AudioSessionState.AudioSessionStateActive => "Active",
                AudioSessionState.AudioSessionStateInactive => "Inactive",
                AudioSessionState.AudioSessionStateExpired => "Expired",
                _ => session.State.ToString(),
            };
        }
        catch
        {
            return "?";
        }
    }

    private static string SafeInstanceId(AudioSessionControl session)
    {
        try { return session.GetSessionInstanceIdentifier ?? ""; } catch { return ""; }
    }

    private static float SafeVolume(SimpleAudioVolume volume)
    {
        try { return volume.Volume; } catch { return 1f; }
    }

    private static string? GetProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private static bool ProcessAlive(int pid, string expectedName)
    {
        var name = GetProcessName(pid);
        return name != null && AppSettings.Normalize(name) == AppSettings.Normalize(expectedName);
    }

    // ----------------------------------------------------------- persistence

    private void PersistLocked()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDirectory);
            var state = new PersistedState { Records = _records, Pending = _pending };
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state, JsonOpts));
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not persist mute state: {ex.Message}");
        }
    }

    private void LoadStateLocked()
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            var state = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(StatePath));
            if (state == null) return;
            _records = state.Records;
            _pending = state.Pending;
            if (_records.Count > 0 || _pending.Count > 0)
                Logger.Info($"Loaded persisted state: {_records.Count} muted, {_pending.Count} pending restore");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not load mute state: {ex.Message}");
        }
    }

    private void PrunePendingLocked()
    {
        _pending.RemoveAll(p => DateTime.UtcNow - p.MutedAtUtc > TimeSpan.FromHours(24));
    }

    // --------------------------------------------------------------- dispose

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _deviceRefreshTimer?.Dispose();
            foreach (var (_, entry) in _teamsSessions.ToList())
            {
                try { entry.Control.UnRegisterEventClient(entry.Handler); } catch { }
            }
            _teamsSessions.Clear();
            try
            {
                if (_enumerator != null && _notificationClient != null)
                    _enumerator.UnregisterEndpointNotificationCallback(_notificationClient);
            }
            catch { }
            foreach (var holder in _devices.Values)
            {
                try { if (holder.Handler != null) holder.Manager.OnSessionCreated -= holder.Handler; } catch { }
                try { holder.Device.Dispose(); } catch { }
            }
            _devices.Clear();
            try { _enumerator?.Dispose(); } catch { }
        }
    }
}
