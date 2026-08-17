using System.IO;
using System.Windows;
using Microsoft.Win32;
using TeamsAudioDucking.Core;
using TeamsAudioDucking.Tray;
using TeamsAudioDucking.UI;

namespace TeamsAudioDucking;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    private AppSettings _settings = null!;
    private AudioDucker _ducker = null!;
    private TeamsCallDetector _detector = null!;
    private TrayIcon _tray = null!;
    private System.Threading.Timer? _reconcileTimer;
    private SettingsWindow? _settingsWindow;
    private bool _started;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, @"Local\TeamsAudioDucking.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        Logger.Init(Path.Combine(AppSettings.DataDirectory, "logs"));
        _settings = AppSettings.Load();
        if (_settings.StartWithWindows != StartupManager.IsEnabled())
            StartupManager.SetEnabled(_settings.StartWithWindows);

        _ducker = new AudioDucker(_settings);
        _ducker.StateChanged += () => RunOnUi(UpdateTray);

        _detector = new TeamsCallDetector(_settings);
        _detector.CallStateChanged += OnCallStateChanged;
        // Teams playback activity feeds the ring heuristic; wire before the
        // ducker starts so no early event is lost.
        _ducker.TeamsPlaybackChanged += _detector.NotifyTeamsPlayback;
        _ducker.Start();

        _tray = new TrayIcon();
        _tray.EnableToggled += OnEnableToggled;
        _tray.MuteNowClicked += () => { _ducker.DuckAll("manual"); RunOnUi(UpdateTray); };
        _tray.RestoreNowClicked += () => { _ducker.RestoreAll("manual"); RunOnUi(UpdateTray); };
        _tray.SettingsClicked += ShowSettings;
        _tray.AboutClicked += ShowAbout;
        _tray.ExitClicked += ExitApplication;

        _detector.Start(); // synchronously evaluates once; fires the event if already in a call

        // If a previous run was killed while apps were muted (and we are not in
        // a call now), put their audio back.
        if (!_detector.InCall)
            _ducker.RestoreLeftoversFromPreviousRun();

        // Slow safety net: re-check call state and (only during calls) sweep
        // for sessions whose events were missed. Very cheap when idle.
        _reconcileTimer = new System.Threading.Timer(_ => Reconcile(), null, 5000, 5000);

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;

        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Error("Unhandled UI exception", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Logger.Error($"Unhandled exception: {args.ExceptionObject}");

        _started = true;
        UpdateTray();
        Logger.Info("Teams Audio Ducking started");
    }

    private void OnCallStateChanged(bool inCall)
    {
        Logger.Info(inCall ? "Teams call detected" : "Teams call ended");
        if (_settings.Enabled)
        {
            if (inCall) _ducker.DuckAll("Teams call started");
            else _ducker.RestoreAll("Teams call ended");
        }
        RunOnUi(UpdateTray);
    }

    private void OnEnableToggled(bool enabled)
    {
        _settings.Enabled = enabled;
        _settings.Save();
        ApplyEnabledState();
    }

    /// <summary>Called after settings change (tray toggle or settings window save).</summary>
    private void ApplySettings()
    {
        if (_settings.StartWithWindows != StartupManager.IsEnabled())
            StartupManager.SetEnabled(_settings.StartWithWindows);
        ApplyEnabledState();
        // Newly enabled call volume (or a changed level) takes effect mid-call.
        if (_settings.Enabled && _ducker.IsDucking)
        {
            _ducker.EnsureDucked();
            _ducker.ReapplyCallVolume();
        }
    }

    private void ApplyEnabledState()
    {
        if (!_settings.Enabled && _ducker.IsDucking)
            _ducker.RestoreAll("muting disabled by user");
        else if (_settings.Enabled && _detector.InCall && !_ducker.IsDucking)
            _ducker.DuckAll("muting enabled during an active call");
        RunOnUi(UpdateTray);
    }

    private void Reconcile()
    {
        try
        {
            _detector.Evaluate();
            if (_settings.Enabled) _ducker.EnsureDucked();
            if (_settings.TraceSessionEvents) _ducker.LogSessionSnapshot();
        }
        catch (Exception ex)
        {
            Logger.Error("Reconcile sweep failed", ex);
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        Logger.Info("System resumed; re-checking devices and call state");
        Task.Run(() =>
        {
            // Give the audio stack a moment to settle after wake.
            Thread.Sleep(3000);
            try
            {
                _ducker.RefreshDevices();
                _ducker.RescanTeamsSessions();
                _detector.Evaluate();
                if (_settings.Enabled) _ducker.EnsureDucked();
            }
            catch (Exception ex)
            {
                Logger.Error("Post-resume refresh failed", ex);
            }
        });
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        // Lock/unlock does not change audio sessions, but re-evaluating is
        // free and keeps the state honest.
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLock)
            _detector.Evaluate();
    }

    private string StatusText() =>
        $"{(_settings.Enabled ? "Enabled" : "Disabled")} | " +
        $"{(_detector.InCall ? "in a Teams call" : "no Teams call")} | " +
        $"{_ducker.MutedCount} app(s) muted";

    private void UpdateTray()
    {
        if (!_started) return;
        _tray.UpdateState(_settings.Enabled, _detector.InCall, _ducker.MutedCount, _ducker.IsDucking);
    }

    private void ShowSettings()
    {
        RunOnUi(() =>
        {
            if (_settingsWindow is { IsLoaded: true })
            {
                _settingsWindow.Activate();
                return;
            }
            _settingsWindow = new SettingsWindow(
                _settings,
                ApplySettings,
                () => { _ducker.DuckAll("manual"); RunOnUi(UpdateTray); },
                () => { _ducker.RestoreAll("manual"); RunOnUi(UpdateTray); },
                StatusText);
            _settingsWindow.Show();
        });
    }

    private void ShowAbout()
    {
        RunOnUi(() => System.Windows.MessageBox.Show(
            $"Teams Audio Ducking v{AppInfo.Version}\n\n" +
            "Automatically mutes other applications' audio during Microsoft Teams " +
            "calls and restores each app's exact previous state afterwards.\n\n" +
            $"Settings and logs:\n{AppSettings.DataDirectory}\n\n" +
            "Runs entirely locally: no internet access, no telemetry, no audio recording.",
            "About Teams Audio Ducking",
            MessageBoxButton.OK, MessageBoxImage.Information));
    }

    private void ExitApplication()
    {
        Logger.Info("Exit requested");
        try
        {
            if (_ducker.IsDucking) _ducker.RestoreAll("application exit");
        }
        catch (Exception ex)
        {
            Logger.Error("Restore on exit failed", ex);
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_started)
        {
            _reconcileTimer?.Dispose();
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            try { _detector.Dispose(); } catch { }
            try { _ducker.Dispose(); } catch { }
            try { _tray.Dispose(); } catch { }
            Logger.Info("Teams Audio Ducking stopped");
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void RunOnUi(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(action);
    }
}
