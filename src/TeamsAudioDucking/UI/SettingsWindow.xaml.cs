using System.Windows;
using TeamsAudioDucking.Core;

namespace TeamsAudioDucking.UI;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action _applySettings;
    private readonly Action _muteNow;
    private readonly Action _restoreNow;
    private readonly Func<string> _statusProvider;

    public SettingsWindow(AppSettings settings, Action applySettings, Action muteNow, Action restoreNow, Func<string> statusProvider)
    {
        InitializeComponent();
        _settings = settings;
        _applySettings = applySettings;
        _muteNow = muteNow;
        _restoreNow = restoreNow;
        _statusProvider = statusProvider;

        ChkEnabled.IsChecked = settings.Enabled;
        ChkStartup.IsChecked = StartupManager.IsEnabled();
        ChkSystemSounds.IsChecked = settings.MuteSystemSounds;
        ChkRinging.IsChecked = settings.MuteWhileRinging;
        TxtExcluded.Text = string.Join(Environment.NewLine, settings.ExcludedProcesses);
        TxtAlwaysMute.Text = string.Join(Environment.NewLine, settings.AlwaysMuteProcesses);
        VersionText.Text = $"v{AppInfo.Version}";

        RefreshStatus();
        Activated += (_, _) => RefreshStatus();
    }

    public void RefreshStatus() => StatusText.Text = _statusProvider();

    private static List<string> ParseList(string text) =>
        text.Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        _settings.Enabled = ChkEnabled.IsChecked == true;
        _settings.StartWithWindows = ChkStartup.IsChecked == true;
        _settings.MuteSystemSounds = ChkSystemSounds.IsChecked == true;
        _settings.MuteWhileRinging = ChkRinging.IsChecked == true;
        _settings.ExcludedProcesses = ParseList(TxtExcluded.Text);
        _settings.AlwaysMuteProcesses = ParseList(TxtAlwaysMute.Text);
        _settings.Save();
        _applySettings();
        RefreshStatus();
    }

    private void BtnMuteNow_Click(object sender, RoutedEventArgs e)
    {
        _muteNow();
        RefreshStatus();
    }

    private void BtnRestoreNow_Click(object sender, RoutedEventArgs e)
    {
        _restoreNow();
        RefreshStatus();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
